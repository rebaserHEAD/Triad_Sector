using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Shared._Triad.ShipSize;
using Microsoft.EntityFrameworkCore;
using Robust.Shared.IoC;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// Every database query the drydock owns. It goes through
/// <see cref="IServerDbManager.RunTriadDbCommand{T}"/> rather than adding methods to
/// <see cref="IServerDbManager"/> itself, which is the seam this fork already uses for
/// feature-owned stores: the queries here make no sense in core, and keeping them out of an
/// upstream file means an upstream pull conflicts on nothing.
/// </summary>
public sealed class DrydockStore
{
    [Dependency] private readonly IServerDbManager _db = default!;

    /// <summary>
    /// Files a new revision against a ship, creating the hull row if this is its first store.
    ///
    /// <para>Everything happens in one transaction: the ship row, the revision, the blob, the blob
    /// pruning, and the audit entry. A torn write here is the failure this design exists to prevent,
    /// where a prune deletes the old blob without the new one landing.</para>
    ///
    /// <para>Two stores of the same ship racing cannot dupe. The revision number is read from the
    /// ship row and incremented, so a race produces the same number twice, and the composite primary
    /// key on (ship_guid, revision) makes the second transaction fail loudly rather than overwrite.
    /// Failing loudly is the point: the caller still holds a live grid and can refuse.</para>
    /// </summary>
    /// <param name="keepBlobs">
    /// How many revisions keep their document. Zero or less prunes nothing. The revision just filed
    /// is never pruned, whatever this says.
    /// </param>
    /// <returns>The outcome, the revision number filed, and the berth the ship now sits in.</returns>
    public Task<DrydockFileResult> FileRevision(DrydockRevisionRequest request, byte[] blob, int keepBlobs, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) =>
        {
            // A store that picks a berth can lose it to another store committing in the same
            // instant, and the unique index on the ship's berth column is the arbiter. Each attempt
            // is its own transaction against a cleared tracker, the lost berth is excluded from the
            // next pick, and three attempts is more free berths than any real garage has. A ship
            // that kept the berth it already held never retries: a violation there is the revision
            // key, and that one has to stay loud.
            var excluded = new HashSet<int>();
            for (var attempt = 0; attempt < 3; attempt++)
            {
                int? picked = null;
                try
                {
                    return await FileRevisionOnce(db, request, blob, keepBlobs, excluded, id => picked = id, token);
                }
                catch (DbUpdateException e) when (picked is { } lost && IsBerthUniqueViolation(e))
                {
                    db.ChangeTracker.Clear();
                    excluded.Add(lost);
                }
            }

            return new DrydockFileResult(DrydockBerthResult.Conflict, 0, null);
        }, ct);
    }

    /// <summary>
    /// Whether a failed write was the berth unique index and nothing else. The retry above must
    /// only ever swallow that one fault: the abort test found the first draft catching every
    /// update exception while a berth was picked, which turned a round foreign-key failure into a
    /// polite "no free berth" and hid the real fault from everyone. Provider-typed on purpose;
    /// the constraint name is the one EF generates for the index on the berth column.
    /// </summary>
    internal static bool IsBerthUniqueViolation(DbUpdateException e)
    {
        return e.InnerException switch
        {
            Npgsql.PostgresException pg => pg.SqlState == "23505"
                && pg.ConstraintName is { } name
                && name.Contains("drydock_ship_berth_id", StringComparison.Ordinal),
            Microsoft.Data.Sqlite.SqliteException sq => sq.SqliteErrorCode == 19
                && sq.Message.Contains("UNIQUE", StringComparison.Ordinal)
                && sq.Message.Contains("drydock_ship.berth_id", StringComparison.Ordinal),
            _ => false,
        };
    }

    /// <summary>
    /// Makes a filed ship retrievable. The pipeline calls this after the grid is gone, never
    /// before: filing and marking are two steps because the filing write yields, and an occupant
    /// who boards during it has to refuse the store without leaving a retrievable row behind a
    /// live ship, which is a duplicate. A held ship stays held; the hold is an admin's decision.
    /// </summary>
    public Task<bool> MarkStored(Guid shipGuid, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) =>
        {
            var now = DateTime.UtcNow;
            var moved = await db.DrydockShip
                .Where(s => s.ShipGuid == shipGuid && s.State == DrydockShipState.CheckedOut)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(s => s.State, DrydockShipState.Stored)
                    .SetProperty(s => s.StateChangedAt, now)
                    .SetProperty(s => s.CheckedOutRoundId, (int?)null)
                    .SetProperty(s => s.UpdatedAt, now), token);

            return moved > 0;
        }, ct);
    }

    private static async Task<DrydockFileResult> FileRevisionOnce(
        ServerDbContext db,
        DrydockRevisionRequest request,
        byte[] blob,
        int keepBlobs,
        HashSet<int> excludedBerths,
        Action<int> berthPicked,
        CancellationToken token)
    {
        await using var tx = await db.Database.BeginTransactionAsync(token);

        var now = DateTime.UtcNow;

        var ship = await db.DrydockShip
            .SingleOrDefaultAsync(s => s.ShipGuid == request.ShipGuid, token);

        if (ship == null)
        {
            // A new hull is out in the world at the moment it is first filed. It becomes stored
            // below only if the caller says so, or by MarkStored once the grid is gone.
            ship = new DrydockShip
            {
                ShipGuid = request.ShipGuid,
                OwnerUserId = request.OwnerUserId,
                State = DrydockShipState.CheckedOut,
                StateChangedAt = now,
                CheckedOutRoundId = request.CreatedRoundId,
                CreatedAt = now,
            };
            db.DrydockShip.Add(ship);
        }

        // Display cache, refreshed on every store. Ownership is NOT refreshed here: a transfer
        // is its own operation with its own audit row, and a store must never quietly move a
        // ship to whoever happened to be flying it.
        ship.ShipName = request.ShipName;
        ship.VesselProto = request.VesselProto;
        ship.SizeClass = request.SizeClass;
        ship.UpdatedAt = now;

        // A player store needs somewhere to put the hull. A re-bake rewrites a document and never
        // touches the berth, because the ship may be out flying while the ladder runs. Refusing
        // here rolls the whole transaction back: nothing is filed for a ship with nowhere to go.
        if (request.Kind is DrydockRevisionKind.PlayerStore or DrydockRevisionKind.LegacyImport)
        {
            var seated = await SeatShip(db, ship, request.SizeClass, request.BerthId, excludedBerths, berthPicked, token);
            if (seated != DrydockBerthResult.Success)
                return new DrydockFileResult(seated, 0, null);
        }

        var revision = ship.CurrentRevision + 1;

        db.DrydockRevision.Add(new DrydockRevision
        {
            ShipGuid = request.ShipGuid,
            Revision = revision,
            Kind = request.Kind,
            DerivedFromRevision = request.DerivedFromRevision,
            RebakeVersion = request.RebakeVersion,
            ActorUserId = request.ActorUserId,
            CreatedRoundId = request.CreatedRoundId,
            CreatedAt = now,
            EngineFormatVer = request.EngineFormatVer,
            DrydockFormatVer = request.DrydockFormatVer,
            ProtoFingerprint = request.ProtoFingerprint,
            CapturedKeyHash = request.CapturedKeyHash,
            Checksum = request.Checksum,
            SizeBytes = request.SizeBytes,
            AppraisedValue = request.AppraisedValue,
            Manifest = request.Manifest,
        });

        db.DrydockBlob.Add(new DrydockBlob
        {
            ShipGuid = request.ShipGuid,
            Revision = revision,
            Blob = blob,
        });

        ship.CurrentRevision = revision;

        // An import has no live grid, so it is stored the moment it is filed. A player store is
        // marked stored by the pipeline after the grid is gone, unless the caller asks for it
        // here. A system re-bake must leave the state alone: the ship may be checked out and
        // flying while the ladder rewrites an older revision.
        if (request.Kind == DrydockRevisionKind.LegacyImport
            || (request.Kind == DrydockRevisionKind.PlayerStore && request.MarkStored))
        {
            ship.State = DrydockShipState.Stored;
            ship.StateChangedAt = now;
            ship.CheckedOutRoundId = null;
        }

        // Prune blobs, never revisions. The floor is the revision we just filed, which is the
        // one a retrieve reads, so it survives whatever keepBlobs says. Zero or less means no
        // pruning rather than keep nothing: of the two readings, only this one costs disk when
        // it is misconfigured.
        if (keepBlobs > 0)
        {
            var floor = revision - keepBlobs + 1;
            var stale = await db.DrydockBlob
                .Where(b => b.ShipGuid == request.ShipGuid && b.Revision < floor)
                .ToListAsync(token);

            db.DrydockBlob.RemoveRange(stale);
        }

        db.DrydockAudit.Add(new DrydockAudit
        {
            ShipGuid = request.ShipGuid,
            BerthId = ship.BerthId,
            ShipName = request.ShipName,
            Action = request.Kind == DrydockRevisionKind.SystemRebake
                ? DrydockAuditAction.Rebake
                : DrydockAuditAction.Store,
            ActorUserId = request.ActorUserId,
            Revision = revision,
            RoundId = request.CreatedRoundId,
            CreatedAt = now,
        });

        await db.SaveChangesAsync(token);
        await tx.CommitAsync(token);

        return new DrydockFileResult(DrydockBerthResult.Success, revision, ship.BerthId);
    }

    /// <summary>
    /// Puts a hull in a berth as part of a store. Keeps the berth it already holds if that still
    /// fits (a crash between confirm and vacate leaves one behind, and this is where it heals),
    /// otherwise takes the named berth, otherwise picks one: its own old slot if free, else the
    /// smallest free berth that fits. Owner and vacancy are enforced by the database as well, so a
    /// pick that turns out to be taken by the time this commits fails on the unique index rather
    /// than filing two hulls into one slot.
    /// </summary>
    private static async Task<DrydockBerthResult> SeatShip(
        ServerDbContext db,
        DrydockShip ship,
        string? hullClass,
        int? requestedBerth,
        HashSet<int> excludedBerths,
        Action<int> berthPicked,
        CancellationToken token)
    {
        if (ship.BerthId is { } held && (requestedBerth == null || requestedBerth == held))
        {
            var current = await db.DrydockBerth.AsNoTracking()
                .SingleOrDefaultAsync(b => b.BerthId == held, token);

            if (current != null && Fits(hullClass, current.MaxSizeClass))
                return DrydockBerthResult.Success;
        }

        if (requestedBerth is { } wanted)
        {
            var named = await db.DrydockBerth.AsNoTracking()
                .SingleOrDefaultAsync(b => b.BerthId == wanted && b.OwnerUserId == ship.OwnerUserId, token);

            if (named == null)
                return DrydockBerthResult.NotFound;

            if (!Fits(hullClass, named.MaxSizeClass))
                return DrydockBerthResult.BerthTooSmall;

            if (await db.DrydockShip.AnyAsync(s => s.BerthId == wanted && s.ShipGuid != ship.ShipGuid, token))
                return DrydockBerthResult.BerthOccupied;

            ship.LastBerthId = ship.BerthId;
            ship.BerthId = wanted;
            berthPicked(wanted);
            return DrydockBerthResult.Success;
        }

        var (outcome, pick) = await PickFreeBerth(db, ship.OwnerUserId, hullClass, ship.LastBerthId, excludedBerths, token);
        if (outcome != DrydockBerthResult.Success)
            return outcome;

        ship.LastBerthId = ship.BerthId;
        ship.BerthId = pick;
        berthPicked(pick!.Value);
        return DrydockBerthResult.Success;
    }

    /// <summary>
    /// The owner's free berths that accept the hull, preferring the ship's own old slot and then
    /// the smallest that fits so the big ones stay available. Free means no ship row points at
    /// it, which the unique index on that column answers directly.
    /// </summary>
    private static async Task<(DrydockBerthResult Outcome, int? BerthId)> PickFreeBerth(
        ServerDbContext db,
        Guid ownerUserId,
        string? hullClass,
        int? preferredBerth,
        HashSet<int> excludedBerths,
        CancellationToken token)
    {
        // A hull class that does not parse is a taxonomy the berths cannot answer for. Fail closed.
        if (!TryParseClass(hullClass, out var hull))
            return (DrydockBerthResult.BerthTooSmall, null);

        var free = await db.DrydockBerth.AsNoTracking()
            .Where(b => b.OwnerUserId == ownerUserId && !db.DrydockShip.Any(s => s.BerthId == b.BerthId))
            .ToListAsync(token);

        free.RemoveAll(b => excludedBerths.Contains(b.BerthId));

        if (free.Count == 0)
            return (DrydockBerthResult.NoBerth, null);

        var fitting = free
            .Where(b => TryParseClass(b.MaxSizeClass, out var max) && hull <= max)
            .ToList();

        if (fitting.Count == 0)
            return (DrydockBerthResult.BerthTooSmall, null);

        var pick = fitting.FirstOrDefault(b => b.BerthId == preferredBerth)
            ?? fitting
                .OrderBy(b => TryParseClass(b.MaxSizeClass, out var max) ? (int)max : int.MaxValue)
                .ThenBy(b => b.BerthId)
                .First();

        return (DrydockBerthResult.Success, pick.BerthId);
    }

    /// <summary>
    /// Both classes are stored as text so a taxonomy change cannot invalidate rows; the comparison
    /// happens here, after parsing, never in SQL. Anything that does not parse fits nothing.
    /// </summary>
    internal static bool Fits(string? hullClass, string? berthClass)
    {
        return TryParseClass(hullClass, out var hull)
            && TryParseClass(berthClass, out var max)
            && hull <= max;
    }

    internal static bool TryParseClass(string? text, out ShipSizeClass sizeClass)
    {
        return Enum.TryParse(text, ignoreCase: false, out sizeClass) && Enum.IsDefined(sizeClass);
    }

    /// <summary>
    /// Empties the ship's berth. Called as the LAST step of a successful retrieve, after the ship
    /// is docked and the claim is confirmed, and deliberately not inside the state claim: the
    /// claim is what blocks a second retrieve, and a failure after it releases the state without
    /// ever having to re-seat a berth somebody else may have taken. The old slot is remembered so
    /// the next store can put the ship back where it was.
    /// </summary>
    public Task VacateBerth(Guid shipGuid, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) =>
        {
            await db.DrydockShip
                .Where(s => s.ShipGuid == shipGuid && s.BerthId != null)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(s => s.LastBerthId, s => s.BerthId)
                    .SetProperty(s => s.BerthId, (int?)null)
                    .SetProperty(s => s.UpdatedAt, DateTime.UtcNow), token);
        }, ct);
    }

    /// <summary>
    /// Whether a store for this hull has somewhere to go, checked before the pipeline mutates
    /// anything so a full garage refuses cheaply. The answer is advisory: the filing transaction
    /// checks again and the unique index makes that one final.
    /// </summary>
    public Task<DrydockBerthResult> CheckBerthForStore(Guid shipGuid, Guid ownerUserId, string hullClass, int? requestedBerth = null, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) =>
        {
            var ship = await db.DrydockShip.AsNoTracking()
                .SingleOrDefaultAsync(s => s.ShipGuid == shipGuid, token);

            // The row's owner, not the caller's: a store never moves a ship between garages.
            var owner = ship?.OwnerUserId ?? ownerUserId;

            if (ship?.BerthId is { } held && (requestedBerth == null || requestedBerth == held))
            {
                var current = await db.DrydockBerth.AsNoTracking()
                    .SingleOrDefaultAsync(b => b.BerthId == held, token);

                if (current != null && Fits(hullClass, current.MaxSizeClass))
                    return DrydockBerthResult.Success;
            }

            // The same three checks the filing transaction makes for a named berth, so the
            // player hears "too small" or "occupied" before anything aboard is touched.
            if (requestedBerth is { } wanted)
            {
                var named = await db.DrydockBerth.AsNoTracking()
                    .SingleOrDefaultAsync(b => b.BerthId == wanted && b.OwnerUserId == owner, token);

                if (named == null)
                    return DrydockBerthResult.NotFound;

                if (!Fits(hullClass, named.MaxSizeClass))
                    return DrydockBerthResult.BerthTooSmall;

                return await db.DrydockShip.AnyAsync(s => s.BerthId == wanted && s.ShipGuid != shipGuid, token)
                    ? DrydockBerthResult.BerthOccupied
                    : DrydockBerthResult.Success;
            }

            var (outcome, _) = await PickFreeBerth(db, owner, hullClass, ship?.LastBerthId, new HashSet<int>(), token);
            return outcome;
        }, ct);
    }

    /// <summary>
    /// Reads a ship's current revision and its blob, or null when the ship is unknown or its blob
    /// has been pruned. A pruned current revision should be impossible, since pruning has a floor,
    /// so a null here with a live ship row is worth an operator's attention rather than a retry.
    /// </summary>
    public Task<DrydockLoad?> LoadCurrent(Guid shipGuid, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) =>
        {
            var ship = await db.DrydockShip
                .AsNoTracking()
                .SingleOrDefaultAsync(s => s.ShipGuid == shipGuid, token);

            if (ship == null)
                return null;

            var revision = await db.DrydockRevision
                .AsNoTracking()
                .SingleOrDefaultAsync(r => r.ShipGuid == shipGuid && r.Revision == ship.CurrentRevision, token);

            if (revision == null)
                return null;

            var blob = await db.DrydockBlob
                .AsNoTracking()
                .SingleOrDefaultAsync(b => b.ShipGuid == shipGuid && b.Revision == ship.CurrentRevision, token);

            if (blob == null)
                return null;

            return new DrydockLoad(ship, revision, blob.Blob);
        }, ct);
    }

    /// <summary>
    /// Reads one specific revision and its blob, which is what the retrieve fallback walks when the
    /// current revision fails to decompress or fails its checksum. Null when that revision has no
    /// blob left, which is the ordinary outcome once pruning has been past it.
    /// </summary>
    public Task<DrydockLoad?> LoadRevision(Guid shipGuid, int revision, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) =>
        {
            var ship = await db.DrydockShip.AsNoTracking()
                .SingleOrDefaultAsync(s => s.ShipGuid == shipGuid, token);

            if (ship == null)
                return null;

            var row = await db.DrydockRevision.AsNoTracking()
                .SingleOrDefaultAsync(r => r.ShipGuid == shipGuid && r.Revision == revision, token);

            if (row == null)
                return null;

            var blob = await db.DrydockBlob.AsNoTracking()
                .SingleOrDefaultAsync(b => b.ShipGuid == shipGuid && b.Revision == revision, token);

            return blob == null ? null : new DrydockLoad(ship, row, blob.Blob);
        }, ct);
    }

    /// <summary>
    /// The hull row alone, for the ownership and state checks a console makes before it commits
    /// to anything. Deliberately not <see cref="LoadCurrent"/>: that reads the document too, and a
    /// refusal should not cost a blob.
    /// </summary>
    public Task<DrydockShip?> GetShipHeader(Guid shipGuid, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) => await db.DrydockShip
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.ShipGuid == shipGuid, token), ct);
    }

    /// <summary>The stored-ship list for a console, drawn from the display cache alone.</summary>
    public Task<List<DrydockShip>> GetShipsByOwner(Guid ownerUserId, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) => await db.DrydockShip
            .AsNoTracking()
            .Where(s => s.OwnerUserId == ownerUserId)
            .OrderBy(s => s.ShipName)
            .ToListAsync(token), ct);
    }

    /// <summary>
    /// Moves a ship's state and records why, as a single conditional update.
    ///
    /// <para>The condition is the point. Retrieve gates on this transition, so "is it stored" and
    /// "mark it checked out" have to be one statement: read-then-write lets two concurrent retrieves
    /// both read <see cref="DrydockShipState.Stored"/> and both proceed, which is a duplicated ship.
    /// SQLite serializes writers and would never show it; Postgres at read committed would. The
    /// database is the only thing that can close this window, since a process-local guard does not
    /// survive a restart and does not span two server processes.</para>
    ///
    /// <para>The audit row is written only when the update actually moved something, so the timeline
    /// can never claim a change that did not happen.</para>
    /// </summary>
    /// <param name="expected">
    /// The state the ship must currently be in for the move to happen. Null means the caller does
    /// not care, which is right for administrative actions and wrong for anything racing.
    /// </param>
    /// <returns>False when the ship is unknown, is not in <paramref name="expected"/>, or is already
    /// in the requested state.</returns>
    public Task<bool> TrySetState(
        Guid shipGuid,
        DrydockShipState? expected,
        DrydockShipState state,
        DrydockAuditAction action,
        Guid? actorUserId,
        int? roundId,
        string? reason,
        CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);

            var snapshot = await db.DrydockShip
                .AsNoTracking()
                .Where(s => s.ShipGuid == shipGuid)
                .Select(s => new { s.ShipName, s.CurrentRevision, s.BerthId })
                .SingleOrDefaultAsync(token);

            if (snapshot == null)
                return false;

            var now = DateTime.UtcNow;

            // Only a checkout records a round. Coming back clears it, so "checked out in round N and
            // never came back" stays answerable from the row rather than by reading the timeline.
            var checkedOutRound = state == DrydockShipState.CheckedOut ? roundId : null;

            var query = db.DrydockShip.Where(s => s.ShipGuid == shipGuid && s.State != state);
            if (expected is { } required)
                query = query.Where(s => s.State == required);

            var moved = await query.ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.State, state)
                .SetProperty(s => s.StateChangedAt, now)
                .SetProperty(s => s.UpdatedAt, now)
                .SetProperty(s => s.CheckedOutRoundId, checkedOutRound), token);

            if (moved == 0)
                return false;

            db.DrydockAudit.Add(new DrydockAudit
            {
                ShipGuid = shipGuid,
                BerthId = snapshot.BerthId,
                ShipName = snapshot.ShipName,
                Action = action,
                ActorUserId = actorUserId,
                Revision = snapshot.CurrentRevision,
                RoundId = roundId,
                Reason = reason,
                CreatedAt = now,
            });

            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);

            return true;
        }, ct);
    }

    /// <summary>
    /// Writes a standalone timeline entry, for the actions that record something without moving the
    /// ship's state: a transfer, a deletion, an adjudication.
    /// </summary>
    public Task WriteAudit(DrydockAudit entry, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) =>
        {
            entry.CreatedAt = DateTime.UtcNow;
            db.DrydockAudit.Add(entry);
            await db.SaveChangesAsync(token);
        }, ct);
    }

    /// <summary>
    /// What one account has done, newest first. This is the read behind "what has this player been
    /// sending", which the ship timeline cannot answer for a refusal on a ship that has no row yet.
    /// </summary>
    public Task<List<DrydockAudit>> GetAuditByActor(Guid actorUserId, int limit, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) => await db.DrydockAudit
            .AsNoTracking()
            .Where(a => a.ActorUserId == actorUserId)
            .OrderByDescending(a => a.CreatedAt)
            .Take(Math.Max(1, limit))
            .ToListAsync(token), ct);
    }

    /// <summary>The ship's timeline, oldest first.</summary>
    public Task<List<DrydockAudit>> GetAudit(Guid shipGuid, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) => await db.DrydockAudit
            .AsNoTracking()
            .Where(a => a.ShipGuid == shipGuid)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(token), ct);
    }

    /// <summary>Every berth an owner has, each with the hull sitting in it, for the terminal and the admin panel.</summary>
    public Task<List<DrydockBerthSlot>> GetBerths(Guid ownerUserId, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) =>
        {
            var berths = await db.DrydockBerth.AsNoTracking()
                .Where(b => b.OwnerUserId == ownerUserId)
                .OrderBy(b => b.BerthId)
                .ToListAsync(token);

            // Filtering occupants by owner is sound because the composite foreign key guarantees
            // a berth's occupant is its owner's ship.
            var occupants = await db.DrydockShip.AsNoTracking()
                .Where(s => s.OwnerUserId == ownerUserId && s.BerthId != null)
                .ToListAsync(token);

            var byBerth = occupants.ToDictionary(s => s.BerthId!.Value);
            return berths.Select(b => new DrydockBerthSlot(b, byBerth.GetValueOrDefault(b.BerthId))).ToList();
        }, ct);
    }

    /// <summary>
    /// Creates a berth. A grant records a price of zero whatever is passed, so a grant can never be
    /// sold for credits. The money itself moves at the terminal before this is called, and the
    /// caller refunds if this throws.
    /// </summary>
    public Task<int> AddBerth(
        Guid ownerUserId,
        ShipSizeClass maxSizeClass,
        DrydockBerthKind kind,
        int pricePaid,
        Guid? actorUserId,
        int? roundId,
        CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);

            var now = DateTime.UtcNow;
            var berth = new DrydockBerth
            {
                OwnerUserId = ownerUserId,
                MaxSizeClass = maxSizeClass.ToString(),
                Kind = kind,
                PricePaid = kind == DrydockBerthKind.Granted ? 0 : Math.Max(0, pricePaid),
                PurchasedAt = now,
                PurchasedRoundId = roundId,
            };

            db.DrydockBerth.Add(berth);
            await db.SaveChangesAsync(token);

            db.DrydockAudit.Add(new DrydockAudit
            {
                BerthId = berth.BerthId,
                Action = kind == DrydockBerthKind.Granted ? DrydockAuditAction.BerthGrant : DrydockAuditAction.BerthPurchase,
                ActorUserId = actorUserId,
                SubjectUserId = ownerUserId,
                RoundId = roundId,
                Reason = $"{maxSizeClass} berth, {berth.PricePaid} paid",
                CreatedAt = now,
            });

            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);

            return berth.BerthId;
        }, ct);
    }

    /// <summary>
    /// Sells or deletes an empty berth; a null <paramref name="requiredOwner"/> is the admin path.
    /// Returns the removed row so the caller can compute a refund from what was actually paid. A
    /// store that lands between the vacancy check and the delete trips the foreign key instead,
    /// and reads as occupied, which it is.
    /// </summary>
    public Task<(DrydockBerthResult Outcome, DrydockBerth? Berth)> TryRemoveBerth(
        int berthId,
        Guid? requiredOwner,
        DrydockAuditAction action,
        Guid? actorUserId,
        int? roundId,
        CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand<(DrydockBerthResult, DrydockBerth?)>(async (db, token) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);

            var berth = await db.DrydockBerth
                .SingleOrDefaultAsync(b => b.BerthId == berthId && (requiredOwner == null || b.OwnerUserId == requiredOwner), token);

            if (berth == null)
                return (DrydockBerthResult.NotFound, null);

            if (await db.DrydockShip.AnyAsync(s => s.BerthId == berthId, token))
                return (DrydockBerthResult.BerthOccupied, null);

            db.DrydockBerth.Remove(berth);

            db.DrydockAudit.Add(new DrydockAudit
            {
                BerthId = berthId,
                Action = action,
                ActorUserId = actorUserId,
                SubjectUserId = berth.OwnerUserId,
                RoundId = roundId,
                Reason = $"{berth.Kind} {berth.MaxSizeClass} berth, {berth.PricePaid} paid",
                CreatedAt = DateTime.UtcNow,
            });

            try
            {
                await db.SaveChangesAsync(token);
                await tx.CommitAsync(token);
            }
            catch (DbUpdateException)
            {
                return (DrydockBerthResult.BerthOccupied, null);
            }

            return (DrydockBerthResult.Success, berth);
        }, ct);
    }

    /// <summary>
    /// Raises a berth's class in place, for a hull that grew while it was out. The delta was really
    /// paid, so it is refundable even on a granted berth; only the free base of a grant stays worth
    /// nothing, which is what flipping the kind records.
    /// </summary>
    public Task<DrydockBerthResult> TryUpgradeBerth(
        int berthId,
        Guid ownerUserId,
        ShipSizeClass newClass,
        int priceDelta,
        Guid? actorUserId,
        int? roundId,
        CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);

            var berth = await db.DrydockBerth
                .SingleOrDefaultAsync(b => b.BerthId == berthId && b.OwnerUserId == ownerUserId, token);

            if (berth == null)
                return DrydockBerthResult.NotFound;

            if (!TryParseClass(berth.MaxSizeClass, out var current) || newClass <= current)
                return DrydockBerthResult.WrongState;

            berth.MaxSizeClass = newClass.ToString();
            berth.PricePaid += Math.Max(0, priceDelta);
            if (berth.PricePaid > 0)
                berth.Kind = DrydockBerthKind.Purchased;

            db.DrydockAudit.Add(new DrydockAudit
            {
                BerthId = berthId,
                Action = DrydockAuditAction.BerthUpgrade,
                ActorUserId = actorUserId,
                SubjectUserId = ownerUserId,
                RoundId = roundId,
                Reason = $"{current} to {newClass}, {priceDelta} paid",
                CreatedAt = DateTime.UtcNow,
            });

            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);

            return DrydockBerthResult.Success;
        }, ct);
    }

    /// <summary>
    /// Admin: moves a stored ship to another of its owner's berths, or with a null target vacates
    /// the berth a ship that is out is still shown in. A cross-owner move is a transfer, and the
    /// composite foreign key refuses it before anything here has to.
    /// </summary>
    public Task<DrydockBerthResult> TryMoveShip(
        Guid shipGuid,
        int? targetBerthId,
        Guid? actorUserId,
        int? roundId,
        string? reason,
        CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);

            var ship = await db.DrydockShip.SingleOrDefaultAsync(s => s.ShipGuid == shipGuid, token);
            if (ship == null)
                return DrydockBerthResult.NotFound;

            var now = DateTime.UtcNow;

            if (targetBerthId is { } target)
            {
                // Seating a ship that is out is a restore, which is a different decision.
                if (ship.State != DrydockShipState.Stored)
                    return DrydockBerthResult.WrongState;

                var berth = await db.DrydockBerth.AsNoTracking()
                    .SingleOrDefaultAsync(b => b.BerthId == target && b.OwnerUserId == ship.OwnerUserId, token);

                if (berth == null)
                    return DrydockBerthResult.NotFound;

                if (!Fits(ship.SizeClass, berth.MaxSizeClass))
                    return DrydockBerthResult.BerthTooSmall;

                if (await db.DrydockShip.AnyAsync(s => s.BerthId == target && s.ShipGuid != shipGuid, token))
                    return DrydockBerthResult.BerthOccupied;

                ship.LastBerthId = ship.BerthId;
                ship.BerthId = target;
            }
            else
            {
                if (ship.BerthId == null)
                    return DrydockBerthResult.WrongState;

                ship.LastBerthId = ship.BerthId;
                ship.BerthId = null;
            }

            ship.UpdatedAt = now;

            db.DrydockAudit.Add(new DrydockAudit
            {
                ShipGuid = shipGuid,
                ShipName = ship.ShipName,
                BerthId = targetBerthId ?? ship.LastBerthId,
                Action = DrydockAuditAction.BerthMove,
                ActorUserId = actorUserId,
                SubjectUserId = ship.OwnerUserId,
                Revision = ship.CurrentRevision,
                RoundId = roundId,
                Reason = reason,
                CreatedAt = now,
            });

            try
            {
                await db.SaveChangesAsync(token);
                await tx.CommitAsync(token);
            }
            catch (DbUpdateException)
            {
                return DrydockBerthResult.Conflict;
            }

            return DrydockBerthResult.Success;
        }, ct);
    }

    /// <summary>
    /// Moves a stored ship to another player, into a free berth of theirs that fits, in one
    /// transaction with its audit row. The composite foreign key on the ship row is what makes
    /// updating the owner without the berth impossible, here and everywhere else.
    /// </summary>
    public Task<(DrydockBerthResult Outcome, int? BerthId)> TryTransferShip(
        Guid shipGuid,
        Guid fromUserId,
        Guid toUserId,
        int? roundId,
        string? reason,
        CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand<(DrydockBerthResult, int?)>(async (db, token) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);

            var ship = await db.DrydockShip.SingleOrDefaultAsync(s => s.ShipGuid == shipGuid, token);
            if (ship == null || ship.OwnerUserId != fromUserId)
                return (DrydockBerthResult.NotFound, null);

            if (fromUserId == toUserId || ship.State != DrydockShipState.Stored || ship.Investigating)
                return (DrydockBerthResult.WrongState, null);

            var (outcome, pick) = await PickFreeBerth(db, toUserId, ship.SizeClass, null, new HashSet<int>(), token);
            if (outcome != DrydockBerthResult.Success)
                return (outcome, null);

            var now = DateTime.UtcNow;
            ship.OwnerUserId = toUserId;
            ship.BerthId = pick;
            ship.LastBerthId = null;
            ship.UpdatedAt = now;

            db.DrydockAudit.Add(new DrydockAudit
            {
                ShipGuid = shipGuid,
                ShipName = ship.ShipName,
                BerthId = pick,
                Action = DrydockAuditAction.Transfer,
                ActorUserId = fromUserId,
                SubjectUserId = toUserId,
                Revision = ship.CurrentRevision,
                RoundId = roundId,
                Reason = reason,
                CreatedAt = now,
            });

            try
            {
                await db.SaveChangesAsync(token);
                await tx.CommitAsync(token);
            }
            catch (DbUpdateException)
            {
                return (DrydockBerthResult.Conflict, null);
            }

            return (DrydockBerthResult.Success, pick);
        }, ct);
    }

    // ---------------------------------------------------------------- Transfers

    /// <summary>
    /// Opens an offer: the ship goes into escrow, keeping its berth, and one pending transfer row
    /// says to whom and until when. The recipient must have a free berth the hull fits right now,
    /// so an offer that could never be accepted is refused at the start rather than after thirty
    /// minutes. The filtered unique index makes a second pending offer on the same ship fail at
    /// the database, which reads back as a conflict.
    /// </summary>
    public Task<(DrydockBerthResult Outcome, DrydockTransfer? Transfer)> TryOfferTransfer(
        Guid shipGuid,
        Guid fromUserId,
        Guid toUserId,
        TimeSpan duration,
        int? roundId,
        CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand<(DrydockBerthResult, DrydockTransfer?)>(async (db, token) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);

            var ship = await db.DrydockShip.SingleOrDefaultAsync(s => s.ShipGuid == shipGuid, token);
            if (ship == null || ship.OwnerUserId != fromUserId)
                return (DrydockBerthResult.NotFound, null);

            if (fromUserId == toUserId || ship.State != DrydockShipState.Stored || ship.Investigating)
                return (DrydockBerthResult.WrongState, null);

            var (fit, _) = await PickFreeBerth(db, toUserId, ship.SizeClass, null, new HashSet<int>(), token);
            if (fit != DrydockBerthResult.Success)
                return (fit, null);

            var now = DateTime.UtcNow;
            ship.State = DrydockShipState.InEscrow;
            ship.StateChangedAt = now;
            ship.UpdatedAt = now;

            var transfer = new DrydockTransfer
            {
                ShipGuid = shipGuid,
                FromUserId = fromUserId,
                ToUserId = toUserId,
                CreatedAt = now,
                ExpiresAt = now + duration,
                Resolution = DrydockTransferResolution.Pending,
                RoundId = roundId,
            };
            db.DrydockTransfer.Add(transfer);

            db.DrydockAudit.Add(new DrydockAudit
            {
                ShipGuid = shipGuid,
                ShipName = ship.ShipName,
                BerthId = ship.BerthId,
                Action = DrydockAuditAction.TransferOffered,
                ActorUserId = fromUserId,
                SubjectUserId = toUserId,
                Revision = ship.CurrentRevision,
                RoundId = roundId,
                Reason = $"expires {transfer.ExpiresAt:u}",
                CreatedAt = now,
            });

            try
            {
                await db.SaveChangesAsync(token);
                await tx.CommitAsync(token);
            }
            catch (DbUpdateException)
            {
                return (DrydockBerthResult.Conflict, null);
            }

            return (DrydockBerthResult.Success, transfer);
        }, ct);
    }

    /// <summary>
    /// Ends a pending offer without moving the ship: declined by the recipient, cancelled by the
    /// owner, or expired by the sweep. The ship leaves escrow and is stored again. The actor has to
    /// be the right party for the resolution, or null for the sweep.
    /// </summary>
    public Task<DrydockTransfer?> TryResolveTransfer(
        long transferId,
        DrydockTransferResolution resolution,
        Guid? actorUserId,
        int? roundId,
        CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand<DrydockTransfer?>(async (db, token) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);

            var transfer = await db.DrydockTransfer
                .SingleOrDefaultAsync(t => t.Id == transferId && t.Resolution == DrydockTransferResolution.Pending, token);
            if (transfer == null)
                return null;

            var allowed = resolution switch
            {
                DrydockTransferResolution.Declined => actorUserId == transfer.ToUserId,
                DrydockTransferResolution.Cancelled => actorUserId == transfer.FromUserId,
                DrydockTransferResolution.Expired => actorUserId == null,
                _ => false,
            };
            if (!allowed)
                return null;

            var now = DateTime.UtcNow;
            transfer.Resolution = resolution;
            transfer.ResolvedAt = now;

            var ship = await db.DrydockShip.SingleOrDefaultAsync(s => s.ShipGuid == transfer.ShipGuid, token);
            if (ship is { State: DrydockShipState.InEscrow })
            {
                ship.State = DrydockShipState.Stored;
                ship.StateChangedAt = now;
                ship.UpdatedAt = now;
            }

            db.DrydockAudit.Add(new DrydockAudit
            {
                ShipGuid = transfer.ShipGuid,
                ShipName = ship?.ShipName,
                BerthId = ship?.BerthId,
                Action = resolution switch
                {
                    DrydockTransferResolution.Declined => DrydockAuditAction.TransferDeclined,
                    DrydockTransferResolution.Cancelled => DrydockAuditAction.TransferCancelled,
                    _ => DrydockAuditAction.TransferExpired,
                },
                ActorUserId = actorUserId,
                SubjectUserId = resolution == DrydockTransferResolution.Cancelled ? transfer.ToUserId : transfer.FromUserId,
                Revision = ship?.CurrentRevision,
                RoundId = roundId,
                CreatedAt = now,
            });

            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);
            return transfer;
        }, ct);
    }

    /// <summary>
    /// The recipient takes the ship: owner and berth move in one transaction, the offer resolves,
    /// and the ship is stored again under its new owner. The berth is picked now, not when the
    /// offer was made, because the recipient's garage may have changed in the meantime; a
    /// recipient with nowhere left to put it is told so and the offer stands.
    /// </summary>
    public Task<(DrydockBerthResult Outcome, int? BerthId, DrydockShip? Ship)> TryAcceptTransfer(
        long transferId,
        Guid toUserId,
        int? roundId,
        CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand<(DrydockBerthResult, int?, DrydockShip?)>(async (db, token) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);

            var transfer = await db.DrydockTransfer
                .SingleOrDefaultAsync(t => t.Id == transferId && t.Resolution == DrydockTransferResolution.Pending, token);
            if (transfer == null || transfer.ToUserId != toUserId)
                return (DrydockBerthResult.NotFound, null, null);

            var now = DateTime.UtcNow;
            if (transfer.ExpiresAt <= now)
                return (DrydockBerthResult.WrongState, null, null);

            var ship = await db.DrydockShip.SingleOrDefaultAsync(s => s.ShipGuid == transfer.ShipGuid, token);
            if (ship == null || ship.OwnerUserId != transfer.FromUserId || ship.State != DrydockShipState.InEscrow)
                return (DrydockBerthResult.WrongState, null, null);

            var (outcome, pick) = await PickFreeBerth(db, toUserId, ship.SizeClass, null, new HashSet<int>(), token);
            if (outcome != DrydockBerthResult.Success)
                return (outcome, null, null);

            ship.OwnerUserId = toUserId;
            ship.BerthId = pick;
            ship.LastBerthId = null;
            ship.State = DrydockShipState.Stored;
            ship.StateChangedAt = now;
            ship.UpdatedAt = now;

            transfer.Resolution = DrydockTransferResolution.Accepted;
            transfer.ResolvedAt = now;

            db.DrydockAudit.Add(new DrydockAudit
            {
                ShipGuid = ship.ShipGuid,
                ShipName = ship.ShipName,
                BerthId = pick,
                Action = DrydockAuditAction.Transfer,
                ActorUserId = toUserId,
                SubjectUserId = transfer.FromUserId,
                Revision = ship.CurrentRevision,
                RoundId = roundId,
                Reason = "accepted the offer",
                CreatedAt = now,
            });

            try
            {
                await db.SaveChangesAsync(token);
                await tx.CommitAsync(token);
            }
            catch (DbUpdateException)
            {
                return (DrydockBerthResult.Conflict, null, null);
            }

            return (DrydockBerthResult.Success, pick, ship);
        }, ct);
    }

    /// <summary>
    /// Expires every pending offer past its deadline and returns the ships released. Run on boot,
    /// so a restart mid-offer cannot strand a ship in escrow, and on a slow tick after that.
    /// </summary>
    public async Task<List<Guid>> ExpireTransfers(DateTime now, int? roundId, CancellationToken ct = default)
    {
        var due = await _db.RunTriadDbCommand(async (db, token) => await db.DrydockTransfer
            .AsNoTracking()
            .Where(t => t.Resolution == DrydockTransferResolution.Pending && t.ExpiresAt <= now)
            .Select(t => new { t.Id, t.ShipGuid })
            .ToListAsync(token), ct);

        var released = new List<Guid>();
        foreach (var row in due)
        {
            if (await TryResolveTransfer(row.Id, DrydockTransferResolution.Expired, null, roundId, ct) != null)
                released.Add(row.ShipGuid);
        }

        return released;
    }

    /// <summary>
    /// One standing offer with the ship it names, or null when there is no pending offer by that
    /// id. The console reads this before answering an offer so a message from the wrong party
    /// can be refused by name and written to the timeline, rather than swallowed by the resolve.
    /// </summary>
    public Task<(DrydockTransfer Transfer, DrydockShip Ship)?> GetPendingTransfer(long transferId, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand<(DrydockTransfer, DrydockShip)?>(async (db, token) =>
        {
            var transfer = await db.DrydockTransfer.AsNoTracking()
                .SingleOrDefaultAsync(t => t.Id == transferId && t.Resolution == DrydockTransferResolution.Pending, token);
            if (transfer == null)
                return null;

            var ship = await db.DrydockShip.AsNoTracking().SingleOrDefaultAsync(s => s.ShipGuid == transfer.ShipGuid, token);
            return ship == null ? null : (transfer, ship);
        }, ct);
    }

    /// <summary>Pending offers addressed to an account, each with the ship it is for: the recipient's alert.</summary>
    public Task<List<(DrydockTransfer Transfer, DrydockShip Ship)>> GetPendingOffersFor(Guid toUserId, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) =>
        {
            var transfers = await db.DrydockTransfer.AsNoTracking()
                .Where(t => t.ToUserId == toUserId && t.Resolution == DrydockTransferResolution.Pending)
                .OrderBy(t => t.ExpiresAt)
                .ToListAsync(token);

            var guids = transfers.Select(t => t.ShipGuid).ToList();
            var ships = await db.DrydockShip.AsNoTracking()
                .Where(s => guids.Contains(s.ShipGuid))
                .ToDictionaryAsync(s => s.ShipGuid, token);

            return transfers
                .Where(t => ships.ContainsKey(t.ShipGuid))
                .Select(t => (t, ships[t.ShipGuid]))
                .ToList();
        }, ct);
    }

    // ---------------------------------------------------------------- Sell, rename, move

    /// <summary>
    /// The appraisal on each of an account's ships' current revision, for the sale quote on the
    /// tab. Null for a revision filed before the column existed; such a ship cannot be sold until
    /// it has been out and stored again.
    /// </summary>
    public Task<Dictionary<Guid, int?>> GetCurrentAppraisals(Guid ownerUserId, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) => await db.DrydockShip.AsNoTracking()
            .Where(s => s.OwnerUserId == ownerUserId)
            .Join(db.DrydockRevision.AsNoTracking(),
                s => new { s.ShipGuid, Revision = s.CurrentRevision },
                r => new { r.ShipGuid, r.Revision },
                (s, r) => new { s.ShipGuid, r.AppraisedValue })
            .ToDictionaryAsync(x => x.ShipGuid, x => x.AppraisedValue, token), ct);
    }

    /// <summary>
    /// The owner scraps a stored ship. The row goes to <see cref="DrydockShipState.Sold"/> and
    /// leaves its berth; revisions and blobs stay under normal retention so an admin can undo a
    /// sale made in anger. The price was computed by the caller from the appraisal it read; both
    /// are written to the timeline so the reversal knows what to take back.
    /// </summary>
    public Task<(DrydockBerthResult Outcome, DrydockShip? Ship)> TrySellShip(
        Guid shipGuid,
        Guid ownerUserId,
        int price,
        int appraisal,
        int? roundId,
        CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand<(DrydockBerthResult, DrydockShip?)>(async (db, token) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);

            var ship = await db.DrydockShip.SingleOrDefaultAsync(s => s.ShipGuid == shipGuid, token);
            if (ship == null || ship.OwnerUserId != ownerUserId)
                return (DrydockBerthResult.NotFound, null);

            if (ship.State != DrydockShipState.Stored || ship.Investigating)
                return (DrydockBerthResult.WrongState, null);

            var now = DateTime.UtcNow;
            ship.LastBerthId = ship.BerthId;
            ship.BerthId = null;
            ship.State = DrydockShipState.Sold;
            ship.StateChangedAt = now;
            ship.UpdatedAt = now;

            db.DrydockAudit.Add(new DrydockAudit
            {
                ShipGuid = shipGuid,
                ShipName = ship.ShipName,
                BerthId = ship.LastBerthId,
                Action = DrydockAuditAction.ShipSold,
                ActorUserId = ownerUserId,
                SubjectUserId = ownerUserId,
                Revision = ship.CurrentRevision,
                RoundId = roundId,
                Reason = $"sold for {price} (appraisal {appraisal})",
                CreatedAt = now,
            });

            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);
            return (DrydockBerthResult.Success, ship);
        }, ct);
    }

    /// <summary>
    /// The owner renames a stored ship. A row update only: the hull and its deed take the name
    /// the next time the ship is retrieved. The old and new names go on the timeline, and the
    /// old one stays searchable there.
    /// </summary>
    public Task<DrydockBerthResult> TryRenameShip(
        Guid shipGuid,
        Guid ownerUserId,
        string newName,
        int? roundId,
        CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);

            var ship = await db.DrydockShip.SingleOrDefaultAsync(s => s.ShipGuid == shipGuid, token);
            if (ship == null || ship.OwnerUserId != ownerUserId)
                return DrydockBerthResult.NotFound;

            if (ship.State != DrydockShipState.Stored || ship.Investigating)
                return DrydockBerthResult.WrongState;

            var now = DateTime.UtcNow;
            var oldName = ship.ShipName;
            ship.ShipName = newName;
            ship.UpdatedAt = now;

            db.DrydockAudit.Add(new DrydockAudit
            {
                ShipGuid = shipGuid,
                ShipName = oldName,
                BerthId = ship.BerthId,
                Action = DrydockAuditAction.Renamed,
                ActorUserId = ownerUserId,
                SubjectUserId = ownerUserId,
                Revision = ship.CurrentRevision,
                RoundId = roundId,
                Reason = $"{oldName} -> {newName}",
                CreatedAt = now,
            });

            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);
            return DrydockBerthResult.Success;
        }, ct);
    }

    /// <summary>Pending offers an account has made, keyed by ship: what the owner's berth rows say about escrow.</summary>
    public Task<Dictionary<Guid, DrydockTransfer>> GetPendingOffersFrom(Guid fromUserId, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) => await db.DrydockTransfer.AsNoTracking()
            .Where(t => t.FromUserId == fromUserId && t.Resolution == DrydockTransferResolution.Pending)
            .ToDictionaryAsync(t => t.ShipGuid, token), ct);
    }

    /// <summary>
    /// The classes of every free berth each of these accounts owns, in one query, so the transfer
    /// picker can say who has room without a round trip per online player.
    /// </summary>
    public Task<Dictionary<Guid, List<string>>> GetFreeBerthClasses(IEnumerable<Guid> owners, CancellationToken ct = default)
    {
        var ids = owners.Distinct().ToList();
        if (ids.Count == 0)
            return Task.FromResult(new Dictionary<Guid, List<string>>());

        return _db.RunTriadDbCommand(async (db, token) =>
        {
            var free = await db.DrydockBerth.AsNoTracking()
                .Where(b => ids.Contains(b.OwnerUserId) && !db.DrydockShip.Any(s => s.BerthId == b.BerthId))
                .Select(b => new { b.OwnerUserId, b.MaxSizeClass })
                .ToListAsync(token);

            return free.GroupBy(b => b.OwnerUserId).ToDictionary(g => g.Key, g => g.Select(b => b.MaxSizeClass).ToList());
        }, ct);
    }

    /// <summary>A filtered page of hulls for the admin panel, newest activity first, owners loaded.</summary>
    public Task<(List<DrydockShip> Rows, int Total)> QueryShips(DrydockShipFilter filter, int page, int pageSize, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) =>
        {
            var query = db.DrydockShip.AsNoTracking().Include(s => s.Owner).AsQueryable();

            if (filter.OwnerUserId is { } owner)
                query = query.Where(s => s.OwnerUserId == owner);
            else if (!string.IsNullOrWhiteSpace(filter.OwnerNameContains))
            {
                var needle = filter.OwnerNameContains.ToLowerInvariant();
                query = query.Where(s => s.Owner.LastSeenUserName.ToLower().Contains(needle));
            }

            if (!string.IsNullOrWhiteSpace(filter.ShipNameContains))
            {
                var needle = filter.ShipNameContains.ToLowerInvariant();
                query = query.Where(s => s.ShipName.ToLower().Contains(needle));
            }

            if (filter.State is { } state)
                query = query.Where(s => s.State == state);

            // Checked out in a round that is over, or in no round at all: the adjudication list.
            if (filter.StrandedOnly)
            {
                var round = filter.CurrentRoundId;
                query = query.Where(s => s.State == DrydockShipState.CheckedOut
                    && (s.CheckedOutRoundId == null || s.CheckedOutRoundId != round));
            }

            var total = await query.CountAsync(token);
            var rows = await query
                .OrderByDescending(s => s.UpdatedAt)
                .Skip(Math.Max(0, page) * pageSize)
                .Take(pageSize)
                .ToListAsync(token);

            return (rows, total);
        }, ct);
    }

    /// <summary>One hull with its whole history and timeline, for the admin panel's detail view.</summary>
    public Task<DrydockShipDetail?> GetShipDetail(Guid shipGuid, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand<DrydockShipDetail?>(async (db, token) =>
        {
            var ship = await db.DrydockShip.AsNoTracking()
                .Include(s => s.Owner)
                .SingleOrDefaultAsync(s => s.ShipGuid == shipGuid, token);

            if (ship == null)
                return null;

            var revisions = await db.DrydockRevision.AsNoTracking()
                .Where(r => r.ShipGuid == shipGuid)
                .OrderByDescending(r => r.Revision)
                .ToListAsync(token);

            var withBlob = await db.DrydockBlob.AsNoTracking()
                .Where(b => b.ShipGuid == shipGuid)
                .Select(b => b.Revision)
                .ToListAsync(token);

            var timeline = await db.DrydockAudit.AsNoTracking()
                .Where(a => a.ShipGuid == shipGuid)
                .OrderByDescending(a => a.CreatedAt)
                .ToListAsync(token);

            return new DrydockShipDetail(ship, revisions, withBlob.ToHashSet(), timeline);
        }, ct);
    }

    /// <summary>Display names for a set of players, from the player table. Online sessions are the caller's to prefer.</summary>
    public Task<Dictionary<Guid, string>> GetPlayerNames(IEnumerable<Guid> userIds, CancellationToken ct = default)
    {
        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0)
            return Task.FromResult(new Dictionary<Guid, string>());

        return _db.RunTriadDbCommand(async (db, token) => await db.Player.AsNoTracking()
            .Where(p => ids.Contains(p.UserId))
            .Select(p => new { p.UserId, p.LastSeenUserName })
            .ToDictionaryAsync(p => p.UserId, p => p.LastSeenUserName, token), ct);
    }

    /// <summary>Admin: flags or clears a ship for investigation, on the timeline. Retrieve refuses while flagged.</summary>
    public Task<bool> SetInvestigating(Guid shipGuid, bool investigating, Guid? actorUserId, int? roundId, string? reason, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);

            var ship = await db.DrydockShip.SingleOrDefaultAsync(s => s.ShipGuid == shipGuid, token);
            if (ship == null || ship.Investigating == investigating)
                return false;

            var now = DateTime.UtcNow;
            ship.Investigating = investigating;
            ship.UpdatedAt = now;

            db.DrydockAudit.Add(new DrydockAudit
            {
                ShipGuid = shipGuid,
                ShipName = ship.ShipName,
                BerthId = ship.BerthId,
                Action = investigating ? DrydockAuditAction.InvestigationOpened : DrydockAuditAction.InvestigationClosed,
                ActorUserId = actorUserId,
                SubjectUserId = ship.OwnerUserId,
                Revision = ship.CurrentRevision,
                RoundId = roundId,
                Reason = reason,
                CreatedAt = now,
            });

            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);
            return true;
        }, ct);
    }

    /// <summary>Admin scratch notes on a hull. Not on the timeline: the timeline is for decisions.</summary>
    public Task<bool> SetAdminNotes(Guid shipGuid, string? notes, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) =>
        {
            var moved = await db.DrydockShip
                .Where(s => s.ShipGuid == shipGuid)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(s => s.AdminNotes, notes)
                    .SetProperty(s => s.UpdatedAt, DateTime.UtcNow), token);

            return moved > 0;
        }, ct);
    }

    /// <summary>
    /// Admin: promotes an older revision to current by filing it again as a new one, kind
    /// AdminRestore, derived from the original. History stays append-only; the promoted document
    /// is copied, never moved, and the usual keep-N pruning runs with the new revision as floor.
    /// </summary>
    public Task<(DrydockBerthResult Outcome, int Revision)> TryPromoteRevision(
        Guid shipGuid,
        int revision,
        Guid? actorUserId,
        int? roundId,
        string? reason,
        int keepBlobs,
        CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand<(DrydockBerthResult, int)>(async (db, token) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);

            var ship = await db.DrydockShip.SingleOrDefaultAsync(s => s.ShipGuid == shipGuid, token);
            if (ship == null)
                return (DrydockBerthResult.NotFound, 0);

            var source = await db.DrydockRevision.AsNoTracking()
                .SingleOrDefaultAsync(r => r.ShipGuid == shipGuid && r.Revision == revision, token);

            var blob = await db.DrydockBlob.AsNoTracking()
                .SingleOrDefaultAsync(b => b.ShipGuid == shipGuid && b.Revision == revision, token);

            // History without a document cannot be promoted; that is what pruning took.
            if (source == null || blob == null)
                return (DrydockBerthResult.NotFound, 0);

            var now = DateTime.UtcNow;
            var next = ship.CurrentRevision + 1;

            db.DrydockRevision.Add(new DrydockRevision
            {
                ShipGuid = shipGuid,
                Revision = next,
                Kind = DrydockRevisionKind.AdminRestore,
                DerivedFromRevision = revision,
                RebakeVersion = source.RebakeVersion,
                ActorUserId = actorUserId,
                CreatedRoundId = roundId,
                CreatedAt = now,
                EngineFormatVer = source.EngineFormatVer,
                DrydockFormatVer = source.DrydockFormatVer,
                ProtoFingerprint = source.ProtoFingerprint,
                CapturedKeyHash = source.CapturedKeyHash,
                Checksum = source.Checksum,
                SizeBytes = source.SizeBytes,
                Manifest = source.Manifest,
            });

            db.DrydockBlob.Add(new DrydockBlob
            {
                ShipGuid = shipGuid,
                Revision = next,
                Blob = blob.Blob,
            });

            ship.CurrentRevision = next;
            ship.UpdatedAt = now;

            if (keepBlobs > 0)
            {
                var floor = next - keepBlobs + 1;
                var stale = await db.DrydockBlob
                    .Where(b => b.ShipGuid == shipGuid && b.Revision < floor)
                    .ToListAsync(token);
                db.DrydockBlob.RemoveRange(stale);
            }

            db.DrydockAudit.Add(new DrydockAudit
            {
                ShipGuid = shipGuid,
                ShipName = ship.ShipName,
                BerthId = ship.BerthId,
                Action = DrydockAuditAction.Restore,
                ActorUserId = actorUserId,
                SubjectUserId = ship.OwnerUserId,
                Revision = next,
                RoundId = roundId,
                Reason = $"promoted revision {revision}: {reason}",
                CreatedAt = now,
            });

            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);
            return (DrydockBerthResult.Success, next);
        }, ct);
    }

    /// <summary>
    /// Admin: deletes a hull and, by cascade, its revisions and blobs. The timeline row is written
    /// first and has no foreign key, so the evidence of the deletion outlives the thing deleted.
    /// The berth the hull sat in is left empty rather than removed.
    /// </summary>
    public Task<DrydockBerthResult> TryDeleteShip(Guid shipGuid, Guid? actorUserId, int? roundId, string? reason, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);

            var ship = await db.DrydockShip.SingleOrDefaultAsync(s => s.ShipGuid == shipGuid, token);
            if (ship == null)
                return DrydockBerthResult.NotFound;

            db.DrydockAudit.Add(new DrydockAudit
            {
                ShipGuid = shipGuid,
                ShipName = ship.ShipName,
                BerthId = ship.BerthId,
                Action = DrydockAuditAction.Delete,
                ActorUserId = actorUserId,
                SubjectUserId = ship.OwnerUserId,
                Revision = ship.CurrentRevision,
                RoundId = roundId,
                Reason = reason,
                CreatedAt = DateTime.UtcNow,
            });

            db.DrydockShip.Remove(ship);

            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);
            return DrydockBerthResult.Success;
        }, ct);
    }

    /// <summary>
    /// Admin: returns a ship that is out or held to the drydock, into a named berth. Whether the
    /// ship is really lost is the admin's call. The one thing this cannot know is whether a live
    /// grid still carries the id, and the system checks that before calling.
    /// </summary>
    public Task<DrydockBerthResult> TryRestoreShip(
        Guid shipGuid,
        int berthId,
        Guid? actorUserId,
        int? roundId,
        string reason,
        CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);

            var ship = await db.DrydockShip.SingleOrDefaultAsync(s => s.ShipGuid == shipGuid, token);
            if (ship == null)
                return DrydockBerthResult.NotFound;

            if (ship.State == DrydockShipState.Stored)
                return DrydockBerthResult.WrongState;

            var berth = await db.DrydockBerth.AsNoTracking()
                .SingleOrDefaultAsync(b => b.BerthId == berthId && b.OwnerUserId == ship.OwnerUserId, token);

            if (berth == null)
                return DrydockBerthResult.NotFound;

            if (!Fits(ship.SizeClass, berth.MaxSizeClass))
                return DrydockBerthResult.BerthTooSmall;

            if (await db.DrydockShip.AnyAsync(s => s.BerthId == berthId && s.ShipGuid != shipGuid, token))
                return DrydockBerthResult.BerthOccupied;

            var now = DateTime.UtcNow;
            ship.State = DrydockShipState.Stored;
            ship.StateChangedAt = now;
            ship.CheckedOutRoundId = null;
            ship.LastBerthId = ship.BerthId;
            ship.BerthId = berthId;
            ship.UpdatedAt = now;

            db.DrydockAudit.Add(new DrydockAudit
            {
                ShipGuid = shipGuid,
                ShipName = ship.ShipName,
                BerthId = berthId,
                Action = DrydockAuditAction.Restore,
                ActorUserId = actorUserId,
                SubjectUserId = ship.OwnerUserId,
                Revision = ship.CurrentRevision,
                RoundId = roundId,
                Reason = reason,
                CreatedAt = now,
            });

            try
            {
                await db.SaveChangesAsync(token);
                await tx.CommitAsync(token);
            }
            catch (DbUpdateException)
            {
                return DrydockBerthResult.Conflict;
            }

            return DrydockBerthResult.Success;
        }, ct);
    }
}

/// <summary>
/// Everything a single <see cref="DrydockStore.FileRevision"/> needs. A record rather than a long
/// parameter list because every field here is a column, and a positional argument that silently
/// swaps two hashes is not a mistake worth being able to make.
/// </summary>
public sealed class DrydockRevisionRequest
{
    public required Guid ShipGuid { get; init; }

    /// <summary>Only used when creating the hull row. An existing ship keeps its owner.</summary>
    public required Guid OwnerUserId { get; init; }

    public required string ShipName { get; init; }

    public string? VesselProto { get; init; }

    public string? SizeClass { get; init; }

    /// <summary>
    /// Name the berth rather than letting the store pick one. The import bridge and admin paths
    /// use it; a player store leaves it null. It still has to be the owner's, free, and large
    /// enough, and the store checks all three.
    /// </summary>
    public int? BerthId { get; init; }

    /// <summary>
    /// Mark the ship stored in the same transaction that files the revision. The pipeline leaves
    /// this false and calls <see cref="DrydockStore.MarkStored"/> once the grid is despawned, so
    /// a store that is refused after the write never leaves a retrievable row behind a live ship.
    /// Callers with no live grid, such as tests filing documents directly, set it.
    /// </summary>
    public bool MarkStored { get; init; }

    public required DrydockRevisionKind Kind { get; init; }

    public int? DerivedFromRevision { get; init; }

    public int RebakeVersion { get; init; }

    /// <summary>Null for the system.</summary>
    public Guid? ActorUserId { get; init; }

    /// <summary>Null between rounds, which is when the re-bake ladder runs.</summary>
    public int? CreatedRoundId { get; init; }

    public required int EngineFormatVer { get; init; }

    public int DrydockFormatVer { get; init; } = DrydockFormat.Current;

    public required byte[] ProtoFingerprint { get; init; }

    public required byte[] CapturedKeyHash { get; init; }

    public required byte[] Checksum { get; init; }

    public required int SizeBytes { get; init; }

    /// <summary>The shipyard's appraisal of the live hull, so a sale of the stored ship has a price. Null when nothing appraised it.</summary>
    public int? AppraisedValue { get; init; }

    public required string Manifest { get; init; }
}

/// <summary>What a retrieve reads: the hull row, the revision it is about to rebuild, and the document.</summary>
public sealed record DrydockLoad(DrydockShip Ship, DrydockRevision Revision, byte[] Blob);

/// <summary>The admin panel's list filter. Every field null or false means "any".</summary>
public sealed record DrydockShipFilter(
    Guid? OwnerUserId,
    string? OwnerNameContains,
    string? ShipNameContains,
    DrydockShipState? State,
    bool StrandedOnly,
    int? CurrentRoundId);

/// <summary>One hull with its history and timeline, newest first, and which revisions still have a document.</summary>
public sealed record DrydockShipDetail(
    DrydockShip Ship,
    List<DrydockRevision> Revisions,
    HashSet<int> RevisionsWithBlob,
    List<DrydockAudit> Timeline);
