using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
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
public sealed partial class DrydockStore
{
    [Dependency] private IServerDbManager _db = default!;

    /// <summary>
    /// The sale price lives only in the ShipSold audit row's reason text; this reads it back for
    /// the admin panel and the restore-from-sale dialog.
    /// </summary>
    private static readonly Regex SoldForPattern = new(@"sold for (\d+)", RegexOptions.Compiled);

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
    /// swallow that one fault only: catching every update exception turns an unrelated failure into
    /// a polite "no free berth" and hides it. Provider-typed on purpose; the constraint name is the
    /// one EF generates for the index on the berth column. Takes the raw provider exception as well
    /// as EF's wrapper: a conditional <c>ExecuteUpdate</c> throws the former, a tracked
    /// <c>SaveChanges</c> the latter.
    /// </summary>
    internal static bool IsBerthUniqueViolation(Exception e)
    {
        var cause = e is DbUpdateException update ? update.InnerException : e;
        return cause switch
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
    /// live ship, which is a duplicate.
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

    /// <summary>
    /// Takes a ship that is sitting in a berth into the impound lot: no hull, no document, one
    /// transaction. The state move is conditional on <see cref="DrydockShipState.Stored"/>, which
    /// is what keeps a retrieve that claimed the row an instant earlier from being overwritten, and
    /// what refuses a row in escrow (withdraw the offer first) or a terminal one (its state is a
    /// verdict, not a hull). The berth is vacated and remembered, the terms are written, and the
    /// audit row carries the fee, all under the same commit, so no failure between them can leave a
    /// row that reads impounded while still holding its berth.
    ///
    /// <para>The fee is taken against the current revision's
    /// <see cref="DrydockRevision.AppraisedValue"/>, because a stored ship has nothing left to
    /// appraise; a revision that never recorded one charges nothing, which is the safe direction to
    /// be wrong in.</para>
    /// </summary>
    /// <returns>The outcome, and on success the credits the row was left owing.</returns>
    public Task<(DrydockBerthResult Outcome, int Fee)> TryImpoundStored(
        Guid shipGuid,
        DrydockImpound impound,
        int? roundId,
        CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand<(DrydockBerthResult, int)>(async (db, token) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);

            var snapshot = await db.DrydockShip
                .AsNoTracking()
                .Where(s => s.ShipGuid == shipGuid)
                .Select(s => new { s.ShipName, s.OwnerUserId, s.CurrentRevision, s.BerthId, s.State })
                .SingleOrDefaultAsync(token);

            if (snapshot == null)
                return (DrydockBerthResult.NotFound, 0);

            if (snapshot.State != DrydockShipState.Stored)
                return (DrydockBerthResult.WrongState, 0);

            var appraisal = await db.DrydockRevision
                .AsNoTracking()
                .Where(r => r.ShipGuid == shipGuid && r.Revision == snapshot.CurrentRevision)
                .Select(r => r.AppraisedValue)
                .SingleOrDefaultAsync(token) ?? 0;

            var fee = impound.FeeAgainst(appraisal);
            var now = DateTime.UtcNow;

            var moved = await db.DrydockShip
                .Where(s => s.ShipGuid == shipGuid && s.State == DrydockShipState.Stored)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(s => s.State, DrydockShipState.Impounded)
                    .SetProperty(s => s.StateChangedAt, now)
                    .SetProperty(s => s.UpdatedAt, now)
                    .SetProperty(s => s.ImpoundFee, fee)
                    .SetProperty(s => s.ImpoundReason, impound.Reason)
                    .SetProperty(s => s.ImpoundRedeemable, impound.Redeemable)
                    .SetProperty(s => s.LastBerthId, s => s.BerthId ?? s.LastBerthId)
                    .SetProperty(s => s.BerthId, (int?)null), token);

            if (moved == 0)
                return (DrydockBerthResult.WrongState, 0);

            db.DrydockAudit.Add(new DrydockAudit
            {
                ShipGuid = shipGuid,
                BerthId = snapshot.BerthId,
                ShipName = snapshot.ShipName,
                Action = DrydockAuditAction.Impound,
                ActorUserId = impound.ActorUserId,
                SubjectUserId = snapshot.OwnerUserId,
                Revision = snapshot.CurrentRevision,
                RoundId = roundId,
                Reason = impound.AuditReason(fee, appraisal, evicted: 0),
                CreatedAt = now,
            });

            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);

            return (DrydockBerthResult.Success, fee);
        }, ct);
    }

    /// <summary>
    /// The impound's counterpart to <see cref="MarkStored"/>, called once the hull is gone. Two
    /// steps for the same reason a store is two steps: the filing write yields, and a row that says
    /// impounded while a live grid still carries the ship is the duplicate the split exists to
    /// prevent.
    ///
    /// <para>Unconditional in the state it moves from, unlike <see cref="MarkStored"/>, because the
    /// hull was in the world whatever the row said about it, and the world is what an impound
    /// answers to. The round it left in is cleared, because the hull is now in the lot and nothing
    /// is out.</para>
    /// </summary>
    public Task<bool> MarkImpounded(Guid shipGuid, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) =>
        {
            var now = DateTime.UtcNow;
            var moved = await db.DrydockShip
                .Where(s => s.ShipGuid == shipGuid && s.State != DrydockShipState.Impounded)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(s => s.State, DrydockShipState.Impounded)
                    .SetProperty(s => s.StateChangedAt, now)
                    .SetProperty(s => s.CheckedOutRoundId, (int?)null)
                    .SetProperty(s => s.UpdatedAt, now), token);

            return moved > 0;
        }, ct);
    }

    /// <summary>
    /// Lifts an impound into one of the owner's berths, for nothing. An impounded ship holds no
    /// berth, so the only way out of the lot is into one: the named berth when the caller names it,
    /// otherwise the hull's last berth if it is still free and fits, otherwise the smallest free
    /// berth that fits, which is the seating rule a store uses. With nowhere to seat it the ship
    /// stays impounded and the outcome says why; granting a berth is the admin's answer to that.
    ///
    /// <para>The impound terms stay on the row on the way out. A reversal needs something to restore
    /// to, and the timeline has to keep saying what the hull was taken for.</para>
    /// </summary>
    /// <returns>The outcome, and on success the berth the ship now sits in.</returns>
    public Task<(DrydockBerthResult Outcome, int? BerthId)> TryReleaseImpound(
        Guid shipGuid,
        int? berthId,
        Guid? actorUserId,
        int? roundId,
        string? reason,
        CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand<(DrydockBerthResult, int?)>(async (db, token) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);

            var ship = await db.DrydockShip.AsNoTracking()
                .Where(s => s.ShipGuid == shipGuid)
                .Select(s => new { s.OwnerUserId, s.State, s.SizeClass, s.LastBerthId, s.ShipName, s.CurrentRevision })
                .SingleOrDefaultAsync(token);

            if (ship == null)
                return (DrydockBerthResult.NotFound, null);

            if (ship.State != DrydockShipState.Impounded)
                return (DrydockBerthResult.WrongState, null);

            var (seated, pick) = await ResolveBerth(db, shipGuid, ship.OwnerUserId, ship.SizeClass, berthId, ship.LastBerthId, new HashSet<int>(), token);
            if (seated != DrydockBerthResult.Success)
                return (seated, null);

            // One conditional update on the state the row was read in; the unique index on the
            // berth column is the arbiter when the pick loses a race to another seating.
            var now = DateTime.UtcNow;
            int moved;
            try
            {
                moved = await db.DrydockShip
                    .Where(s => s.ShipGuid == shipGuid && s.State == DrydockShipState.Impounded)
                    .ExecuteUpdateAsync(set => set
                        .SetProperty(s => s.State, DrydockShipState.Stored)
                        .SetProperty(s => s.StateChangedAt, now)
                        .SetProperty(s => s.CheckedOutRoundId, (int?)null)
                        .SetProperty(s => s.LastBerthId, s => s.BerthId)
                        .SetProperty(s => s.BerthId, pick)
                        .SetProperty(s => s.UpdatedAt, now), token);
            }
            catch (Exception e) when (IsBerthUniqueViolation(e))
            {
                return (DrydockBerthResult.BerthOccupied, null);
            }

            if (moved == 0)
                return (DrydockBerthResult.WrongState, null);

            db.DrydockAudit.Add(new DrydockAudit
            {
                ShipGuid = shipGuid,
                BerthId = pick,
                ShipName = ship.ShipName,
                Action = DrydockAuditAction.ImpoundReleased,
                ActorUserId = actorUserId,
                SubjectUserId = ship.OwnerUserId,
                Revision = ship.CurrentRevision,
                RoundId = roundId,
                Reason = reason,
                CreatedAt = now,
            });

            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);

            return (DrydockBerthResult.Success, pick);
        }, ct);
    }

    /// <summary>
    /// The owner pays the fee and takes an impounded ship back into a berth of theirs. The money
    /// moved before this was called, so the fee it was charged against comes back in and the move
    /// refuses as <see cref="DrydockBerthResult.Conflict"/> when the row's fee is any different: an
    /// admin who re-impounded on new terms between the console's read and the press must not have
    /// the old price honoured, and the console refunds on every refusal. The berth gets the three
    /// checks a named berth gets, the impound has to be one the owner may act on, and the move is
    /// one conditional update from <see cref="DrydockShipState.Impounded"/>. A locked impound refuses
    /// whatever was paid.
    ///
    /// <para>The terms stay on the row on the way out, so a reversal has something to restore to.</para>
    /// </summary>
    public Task<DrydockBerthResult> TryRedeemImpound(
        Guid shipGuid,
        Guid ownerUserId,
        int berthId,
        int paidFee,
        int? roundId,
        CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);

            var ship = await db.DrydockShip.AsNoTracking()
                .Where(s => s.ShipGuid == shipGuid)
                .Select(s => new { s.OwnerUserId, s.State, s.SizeClass, s.ShipName, s.CurrentRevision, s.ImpoundFee, s.ImpoundRedeemable })
                .SingleOrDefaultAsync(token);

            if (ship == null || ship.OwnerUserId != ownerUserId)
                return DrydockBerthResult.NotFound;

            if (ship.State != DrydockShipState.Impounded || !ship.ImpoundRedeemable)
                return DrydockBerthResult.WrongState;

            if (ship.ImpoundFee != paidFee)
                return DrydockBerthResult.Conflict;

            var (fit, _) = await ResolveBerth(db, shipGuid, ownerUserId, ship.SizeClass, berthId, null, new HashSet<int>(), token);
            if (fit != DrydockBerthResult.Success)
                return fit;

            var now = DateTime.UtcNow;
            int moved;
            try
            {
                moved = await db.DrydockShip
                    .Where(s => s.ShipGuid == shipGuid
                        && s.State == DrydockShipState.Impounded
                        && s.OwnerUserId == ownerUserId
                        && s.ImpoundRedeemable
                        && s.ImpoundFee == paidFee)
                    .ExecuteUpdateAsync(set => set
                        .SetProperty(s => s.State, DrydockShipState.Stored)
                        .SetProperty(s => s.StateChangedAt, now)
                        .SetProperty(s => s.CheckedOutRoundId, (int?)null)
                        .SetProperty(s => s.LastBerthId, s => s.BerthId)
                        .SetProperty(s => s.BerthId, (int?)berthId)
                        .SetProperty(s => s.UpdatedAt, now), token);
            }
            catch (Exception e) when (IsBerthUniqueViolation(e))
            {
                return DrydockBerthResult.BerthOccupied;
            }

            if (moved == 0)
                return DrydockBerthResult.WrongState;

            db.DrydockAudit.Add(new DrydockAudit
            {
                ShipGuid = shipGuid,
                ShipName = ship.ShipName,
                BerthId = berthId,
                Action = DrydockAuditAction.ImpoundRedeemed,
                ActorUserId = ownerUserId,
                SubjectUserId = ownerUserId,
                Revision = ship.CurrentRevision,
                RoundId = roundId,
                Reason = paidFee > 0 ? $"paid {paidFee}" : "no fee",
                CreatedAt = now,
            });

            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);

            return DrydockBerthResult.Success;
        }, ct);
    }

    /// <summary>
    /// The owner walks away from an impounded ship rather than pay for it. No money moves in
    /// either direction, which is the whole difference from a sale, and the row goes to a terminal
    /// state of its own so an admin reads "the owner chose this" rather than inferring it. A locked
    /// impound refuses: an owner must not be able to end an adjudication from their side. The
    /// typed-name check is the console's; this is the row, one conditional update from
    /// <see cref="DrydockShipState.Impounded"/>. Revisions stay, so an admin restore can undo it.
    /// </summary>
    /// <returns>The outcome, and on success the name the ship was given up under.</returns>
    public Task<(DrydockBerthResult Outcome, string? ShipName)> TryAbandonShip(
        Guid shipGuid,
        Guid ownerUserId,
        int? roundId,
        CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand<(DrydockBerthResult, string?)>(async (db, token) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);

            var ship = await db.DrydockShip.AsNoTracking()
                .Where(s => s.ShipGuid == shipGuid)
                .Select(s => new { s.OwnerUserId, s.State, s.ShipName, s.CurrentRevision, s.ImpoundFee, s.ImpoundRedeemable })
                .SingleOrDefaultAsync(token);

            if (ship == null || ship.OwnerUserId != ownerUserId)
                return (DrydockBerthResult.NotFound, null);

            if (ship.State != DrydockShipState.Impounded || !ship.ImpoundRedeemable)
                return (DrydockBerthResult.WrongState, null);

            var now = DateTime.UtcNow;
            var moved = await db.DrydockShip
                .Where(s => s.ShipGuid == shipGuid
                    && s.State == DrydockShipState.Impounded
                    && s.OwnerUserId == ownerUserId
                    && s.ImpoundRedeemable)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(s => s.State, DrydockShipState.Abandoned)
                    .SetProperty(s => s.StateChangedAt, now)
                    .SetProperty(s => s.CheckedOutRoundId, (int?)null)
                    .SetProperty(s => s.UpdatedAt, now), token);

            if (moved == 0)
                return (DrydockBerthResult.WrongState, null);

            db.DrydockAudit.Add(new DrydockAudit
            {
                ShipGuid = shipGuid,
                ShipName = ship.ShipName,
                Action = DrydockAuditAction.ShipAbandoned,
                ActorUserId = ownerUserId,
                SubjectUserId = ownerUserId,
                Revision = ship.CurrentRevision,
                RoundId = roundId,
                Reason = ship.ImpoundFee > 0 ? $"gave up rather than pay {ship.ImpoundFee}" : "gave up",
                CreatedAt = now,
            });

            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);

            return (DrydockBerthResult.Success, ship.ShipName);
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
        //
        // An impound is the third case: it has somewhere to go that is not a berth, so it vacates
        // instead of seating. LastBerthId keeps where the hull came from, which is what a release
        // offers as its default and what makes the redemption gate mean something, since a hull
        // still holding its own berth would satisfy "find a free berth" for nothing.
        int? impoundVacated = null;
        if (request.Impound is { } impound)
        {
            if (ship.BerthId is { } vacated)
            {
                ship.LastBerthId = vacated;
                impoundVacated = vacated;
            }

            ship.BerthId = null;
            ship.ImpoundFee = impound.FeeAgainst(request.AppraisedValue ?? 0);
            ship.ImpoundReason = impound.Reason;
            ship.ImpoundRedeemable = impound.Redeemable;
        }
        else if (request.Kind is DrydockRevisionKind.PlayerStore or DrydockRevisionKind.LegacyImport)
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

        // An impound's row says which berth was vacated, who took the hull and from whom, and what
        // it cost; a store's says where the hull was seated and who put it there.
        db.DrydockAudit.Add(new DrydockAudit
        {
            ShipGuid = request.ShipGuid,
            BerthId = request.Impound != null ? impoundVacated : ship.BerthId,
            ShipName = request.ShipName,
            Action = request.Impound != null
                ? DrydockAuditAction.Impound
                : request.Kind == DrydockRevisionKind.SystemRebake
                    ? DrydockAuditAction.Rebake
                    : DrydockAuditAction.Store,
            ActorUserId = request.ActorUserId,
            SubjectUserId = request.Impound != null ? ship.OwnerUserId : null,
            Revision = revision,
            RoundId = request.CreatedRoundId,
            Reason = request.Impound?.AuditReason(ship.ImpoundFee, request.AppraisedValue ?? 0, request.Evicted),
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

        var (outcome, pick) = await ResolveBerth(db, ship.ShipGuid, ship.OwnerUserId, hullClass, requestedBerth, ship.LastBerthId, excludedBerths, token);
        if (outcome != DrydockBerthResult.Success)
            return outcome;

        ship.LastBerthId = ship.BerthId;
        ship.BerthId = pick;
        berthPicked(pick!.Value);
        return DrydockBerthResult.Success;
    }

    /// <summary>
    /// The berth a hull would be seated in, without seating it: the named berth after the three
    /// checks a named berth gets (the owner's, large enough, empty), else the pick. Shared by the
    /// tracked seat a filing does and by the verbs that seat a hull inside one conditional update,
    /// so both answer the same question the same way.
    /// </summary>
    private static async Task<(DrydockBerthResult Outcome, int? BerthId)> ResolveBerth(
        ServerDbContext db,
        Guid shipGuid,
        Guid ownerUserId,
        string? hullClass,
        int? requestedBerth,
        int? preferredBerth,
        HashSet<int> excludedBerths,
        CancellationToken token)
    {
        if (requestedBerth is not { } wanted)
            return await PickFreeBerth(db, ownerUserId, hullClass, preferredBerth, excludedBerths, token);

        var named = await db.DrydockBerth.AsNoTracking()
            .SingleOrDefaultAsync(b => b.BerthId == wanted && b.OwnerUserId == ownerUserId, token);

        if (named == null)
            return (DrydockBerthResult.NotFound, null);

        if (!Fits(hullClass, named.MaxSizeClass))
            return (DrydockBerthResult.BerthTooSmall, null);

        if (await db.DrydockShip.AnyAsync(s => s.BerthId == wanted && s.ShipGuid != shipGuid, token))
            return (DrydockBerthResult.BerthOccupied, null);

        return (DrydockBerthResult.Success, wanted);
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

            // Only a checkout records a round. Every other state clears it, so "checked out in round
            // N and never came back" stays answerable from the row rather than by reading the
            // timeline. An impound clears it too: the hull is always saved before the row can say
            // impounded, so there is no impounded-while-flying for the round to be evidence of.
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
    ///
    /// <para>Vacating is for a hull that is out: a crash between a retrieve's confirm and its vacate
    /// leaves a flying ship in its slot, and this is the repair. A stored ship lives in its berth
    /// and is moved, never vacated: a stored ship with nowhere to be is one the console cannot draw
    /// and the free-berth anti-join counts as room. A ship in escrow keeps its berth for the same
    /// reason: every resolution but an accept sets it Stored without touching the berth, so a
    /// vacate under a standing offer manufactures exactly that row.</para>
    ///
    /// <para>Both moves are one conditional update on the state the row was read in, so a retrieve
    /// claiming the ship in the same instant is not overwritten by a move that read it as stored.</para>
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

            var ship = await db.DrydockShip.AsNoTracking()
                .Where(s => s.ShipGuid == shipGuid)
                .Select(s => new { s.OwnerUserId, s.State, s.SizeClass, s.BerthId, s.ShipName, s.CurrentRevision })
                .SingleOrDefaultAsync(token);

            if (ship == null)
                return DrydockBerthResult.NotFound;

            var now = DateTime.UtcNow;
            int moved;

            if (targetBerthId is { } target)
            {
                // Seating a ship that is out is a restore, which is a different decision.
                if (ship.State != DrydockShipState.Stored)
                    return DrydockBerthResult.WrongState;

                var (fit, _) = await ResolveBerth(db, shipGuid, ship.OwnerUserId, ship.SizeClass, target, null, new HashSet<int>(), token);
                if (fit != DrydockBerthResult.Success)
                    return fit;

                try
                {
                    moved = await db.DrydockShip
                        .Where(s => s.ShipGuid == shipGuid && s.State == DrydockShipState.Stored)
                        .ExecuteUpdateAsync(set => set
                            .SetProperty(s => s.LastBerthId, s => s.BerthId)
                            .SetProperty(s => s.BerthId, (int?)target)
                            .SetProperty(s => s.UpdatedAt, now), token);
                }
                catch (Exception e) when (IsBerthUniqueViolation(e))
                {
                    return DrydockBerthResult.BerthOccupied;
                }
            }
            else
            {
                if (ship.State is DrydockShipState.Stored or DrydockShipState.InEscrow || ship.BerthId == null)
                    return DrydockBerthResult.WrongState;

                moved = await db.DrydockShip
                    .Where(s => s.ShipGuid == shipGuid
                        && s.State != DrydockShipState.Stored
                        && s.State != DrydockShipState.InEscrow
                        && s.BerthId != null)
                    .ExecuteUpdateAsync(set => set
                        .SetProperty(s => s.LastBerthId, s => s.BerthId)
                        .SetProperty(s => s.BerthId, (int?)null)
                        .SetProperty(s => s.UpdatedAt, now), token);
            }

            if (moved == 0)
                return DrydockBerthResult.WrongState;

            db.DrydockAudit.Add(new DrydockAudit
            {
                ShipGuid = shipGuid,
                ShipName = ship.ShipName,
                BerthId = targetBerthId ?? ship.BerthId,
                Action = DrydockAuditAction.BerthMove,
                ActorUserId = actorUserId,
                SubjectUserId = ship.OwnerUserId,
                Revision = ship.CurrentRevision,
                RoundId = roundId,
                Reason = reason,
                CreatedAt = now,
            });

            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);

            return DrydockBerthResult.Success;
        }, ct);
    }

    // ---------------------------------------------------------------- Transfers

    /// <summary>
    /// Opens an offer: the ship goes into escrow, keeping its berth, and one pending transfer row
    /// says to whom and until when. The recipient must have a free berth the hull fits right now,
    /// so an offer that could never be accepted is refused at the start rather than after thirty
    /// minutes. The filtered unique index makes a second pending offer on the same ship fail at
    /// the database, which reads back as a conflict.
    ///
    /// <para>The move into escrow is one conditional update on <see cref="DrydockShipState.Stored"/>,
    /// so an offer cannot land on a row a retrieve or a sale claimed in the same instant.</para>
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

            var ship = await db.DrydockShip.AsNoTracking()
                .Where(s => s.ShipGuid == shipGuid)
                .Select(s => new { s.OwnerUserId, s.State, s.SizeClass, s.ShipName, s.BerthId, s.CurrentRevision })
                .SingleOrDefaultAsync(token);

            if (ship == null || ship.OwnerUserId != fromUserId)
                return (DrydockBerthResult.NotFound, null);

            if (fromUserId == toUserId || ship.State != DrydockShipState.Stored)
                return (DrydockBerthResult.WrongState, null);

            var (fit, _) = await PickFreeBerth(db, toUserId, ship.SizeClass, null, new HashSet<int>(), token);
            if (fit != DrydockBerthResult.Success)
                return (fit, null);

            var now = DateTime.UtcNow;
            var moved = await db.DrydockShip
                .Where(s => s.ShipGuid == shipGuid && s.State == DrydockShipState.Stored && s.OwnerUserId == fromUserId)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(s => s.State, DrydockShipState.InEscrow)
                    .SetProperty(s => s.StateChangedAt, now)
                    .SetProperty(s => s.UpdatedAt, now), token);

            if (moved == 0)
                return (DrydockBerthResult.WrongState, null);

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
    ///
    /// <para>Both writes are conditional. The offer is resolved only while it is still pending, so a
    /// decline and an accept landing in the same instant cannot both succeed, and the ship goes back
    /// to stored only while it is still in escrow, so an accept that already moved it to its new
    /// owner is never undone by a late expiry.</para>
    /// </summary>
    /// <returns>The offer as resolved, or null when it was not pending or the actor was the wrong party.</returns>
    public Task<DrydockTransfer?> TryResolveTransfer(
        long transferId,
        DrydockTransferResolution resolution,
        Guid? actorUserId,
        int? roundId,
        CancellationToken ct = default,
        bool adminOverride = false,
        string? reason = null)
    {
        return _db.RunTriadDbCommand<DrydockTransfer?>(async (db, token) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);

            var transfer = await db.DrydockTransfer.AsNoTracking()
                .SingleOrDefaultAsync(t => t.Id == transferId && t.Resolution == DrydockTransferResolution.Pending, token);
            if (transfer == null)
                return null;

            if (!adminOverride)
            {
                var allowed = resolution switch
                {
                    DrydockTransferResolution.Declined => actorUserId == transfer.ToUserId,
                    DrydockTransferResolution.Cancelled => actorUserId == transfer.FromUserId,
                    DrydockTransferResolution.Expired => actorUserId == null,
                    _ => false,
                };
                if (!allowed)
                    return null;
            }

            var now = DateTime.UtcNow;
            var resolved = await db.DrydockTransfer
                .Where(t => t.Id == transferId && t.Resolution == DrydockTransferResolution.Pending)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(t => t.Resolution, resolution)
                    .SetProperty(t => t.ResolvedAt, (DateTime?)now), token);

            if (resolved == 0)
                return null;

            await db.DrydockShip
                .Where(s => s.ShipGuid == transfer.ShipGuid && s.State == DrydockShipState.InEscrow)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(s => s.State, DrydockShipState.Stored)
                    .SetProperty(s => s.StateChangedAt, now)
                    .SetProperty(s => s.UpdatedAt, now), token);

            var ship = await db.DrydockShip.AsNoTracking()
                .Where(s => s.ShipGuid == transfer.ShipGuid)
                .Select(s => new { s.ShipName, s.BerthId, s.CurrentRevision })
                .SingleOrDefaultAsync(token);

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
                Reason = reason,
                CreatedAt = now,
            });

            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);

            transfer.Resolution = resolution;
            transfer.ResolvedAt = now;
            return transfer;
        }, ct);
    }

    /// <summary>The standing offer on one ship, or null when it has none.</summary>
    public Task<DrydockTransfer?> GetPendingOfferForShip(Guid shipGuid, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) => await db.DrydockTransfer.AsNoTracking()
            .SingleOrDefaultAsync(t => t.ShipGuid == shipGuid && t.Resolution == DrydockTransferResolution.Pending, token), ct);
    }

    /// <summary>The standing offers on a set of ships, keyed by ship: the clock on the admin panel's rows.</summary>
    public Task<Dictionary<Guid, DrydockTransfer>> GetPendingOffersForShips(IEnumerable<Guid> shipGuids, CancellationToken ct = default)
    {
        var ids = shipGuids.Distinct().ToList();
        if (ids.Count == 0)
            return Task.FromResult(new Dictionary<Guid, DrydockTransfer>());

        return _db.RunTriadDbCommand(async (db, token) => await db.DrydockTransfer.AsNoTracking()
            .Where(t => ids.Contains(t.ShipGuid) && t.Resolution == DrydockTransferResolution.Pending)
            .ToDictionaryAsync(t => t.ShipGuid, token), ct);
    }

    /// <summary>
    /// The most recent sale of a ship, read back from its timeline row, which is where the price
    /// was written. Null when the ship has never been sold.
    /// </summary>
    public Task<(int Price, DateTime At)?> GetLastSale(Guid shipGuid, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand<(int, DateTime)?>(async (db, token) =>
        {
            var row = await db.DrydockAudit.AsNoTracking()
                .Where(a => a.ShipGuid == shipGuid && a.Action == DrydockAuditAction.ShipSold)
                .OrderByDescending(a => a.CreatedAt)
                .FirstOrDefaultAsync(token);

            if (row?.Reason == null)
                return null;

            var match = SoldForPattern.Match(row.Reason);
            return match.Success && int.TryParse(match.Groups[1].Value, out var price) ? (price, row.CreatedAt) : null;
        }, ct);
    }

    /// <summary>
    /// The recipient takes the ship: owner and berth move in one transaction, the offer resolves,
    /// and the ship is stored again under its new owner. The berth is picked now, not when the
    /// offer was made, because the recipient's garage may have changed in the meantime; a
    /// recipient with nowhere left to put it is told so and the offer stands.
    ///
    /// <para>The offer is resolved first and conditionally, so a decline, a withdrawal or the expiry
    /// sweep landing in the same instant either wins outright or loses outright; then the ship moves
    /// conditionally on still being in escrow under the giver. A refusal after the first write rolls
    /// both back. The timeline row names the giver as actor and the recipient as subject, which is
    /// the direction the transfer went, whoever pressed the button.</para>
    /// </summary>
    /// <returns>The outcome, and on success the berth the ship landed in and its name.</returns>
    public Task<(DrydockBerthResult Outcome, int? BerthId, string? ShipName)> TryAcceptTransfer(
        long transferId,
        Guid toUserId,
        int? roundId,
        CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand<(DrydockBerthResult, int?, string?)>(async (db, token) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);

            var transfer = await db.DrydockTransfer.AsNoTracking()
                .SingleOrDefaultAsync(t => t.Id == transferId && t.Resolution == DrydockTransferResolution.Pending, token);
            if (transfer == null || transfer.ToUserId != toUserId)
                return (DrydockBerthResult.NotFound, null, null);

            var now = DateTime.UtcNow;
            if (transfer.ExpiresAt <= now)
                return (DrydockBerthResult.WrongState, null, null);

            var ship = await db.DrydockShip.AsNoTracking()
                .Where(s => s.ShipGuid == transfer.ShipGuid)
                .Select(s => new { s.OwnerUserId, s.State, s.SizeClass, s.ShipName, s.CurrentRevision })
                .SingleOrDefaultAsync(token);

            if (ship == null || ship.OwnerUserId != transfer.FromUserId || ship.State != DrydockShipState.InEscrow)
                return (DrydockBerthResult.WrongState, null, null);

            var (outcome, pick) = await PickFreeBerth(db, toUserId, ship.SizeClass, null, new HashSet<int>(), token);
            if (outcome != DrydockBerthResult.Success)
                return (outcome, null, null);

            var resolved = await db.DrydockTransfer
                .Where(t => t.Id == transferId && t.Resolution == DrydockTransferResolution.Pending)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(t => t.Resolution, DrydockTransferResolution.Accepted)
                    .SetProperty(t => t.ResolvedAt, (DateTime?)now), token);

            if (resolved == 0)
                return (DrydockBerthResult.WrongState, null, null);

            int moved;
            try
            {
                moved = await db.DrydockShip
                    .Where(s => s.ShipGuid == transfer.ShipGuid && s.State == DrydockShipState.InEscrow && s.OwnerUserId == transfer.FromUserId)
                    .ExecuteUpdateAsync(set => set
                        .SetProperty(s => s.OwnerUserId, toUserId)
                        .SetProperty(s => s.BerthId, pick)
                        .SetProperty(s => s.LastBerthId, (int?)null)
                        .SetProperty(s => s.State, DrydockShipState.Stored)
                        .SetProperty(s => s.StateChangedAt, now)
                        .SetProperty(s => s.UpdatedAt, now), token);
            }
            catch (Exception e) when (IsBerthUniqueViolation(e))
            {
                return (DrydockBerthResult.Conflict, null, null);
            }

            if (moved == 0)
                return (DrydockBerthResult.WrongState, null, null);

            db.DrydockAudit.Add(new DrydockAudit
            {
                ShipGuid = transfer.ShipGuid,
                ShipName = ship.ShipName,
                BerthId = pick,
                Action = DrydockAuditAction.Transfer,
                ActorUserId = transfer.FromUserId,
                SubjectUserId = toUserId,
                Revision = ship.CurrentRevision,
                RoundId = roundId,
                Reason = "offer accepted",
                CreatedAt = now,
            });

            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);

            return (DrydockBerthResult.Success, pick, ship.ShipName);
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
    /// <returns>The outcome, and on success the name the ship was sold under.</returns>
    public Task<(DrydockBerthResult Outcome, string? ShipName)> TrySellShip(
        Guid shipGuid,
        Guid ownerUserId,
        int price,
        int appraisal,
        int? roundId,
        CancellationToken ct = default)
    {
        return MarkSold(shipGuid, DrydockShipState.Stored, ownerUserId, ownerUserId, price, appraisal, roundId, live: false, ct);
    }

    /// <summary>
    /// The shipyard scrapped a hull that was out on a retrieve. The credits moved at the console
    /// the way any live sale's do; this is the row catching up, so the drydock does not go on
    /// listing as checked out a ship that no longer exists, offering to restore it, and finding no
    /// sale on file when the restore is from a sale. The seller is whoever held the deed card,
    /// which need not be the owner: the timeline names both.
    /// </summary>
    public Task<(DrydockBerthResult Outcome, string? ShipName)> TrySellLiveShip(
        Guid shipGuid,
        Guid sellerUserId,
        int price,
        int appraisal,
        int? roundId,
        CancellationToken ct = default)
    {
        return MarkSold(shipGuid, DrydockShipState.CheckedOut, requiredOwner: null, sellerUserId, price, appraisal, roundId, live: true, ct);
    }

    /// <summary>
    /// One conditional update from the state the sale is legal in to <see cref="DrydockShipState.Sold"/>,
    /// vacating the berth and clearing the round, so a retrieve claiming the row in the same instant
    /// is not overwritten by a sale that read it as stored. The reason keeps the price in the form
    /// <see cref="SoldForPattern"/> reads back.
    /// </summary>
    private Task<(DrydockBerthResult Outcome, string? ShipName)> MarkSold(
        Guid shipGuid,
        DrydockShipState from,
        Guid? requiredOwner,
        Guid actorUserId,
        int price,
        int appraisal,
        int? roundId,
        bool live,
        CancellationToken ct)
    {
        return _db.RunTriadDbCommand<(DrydockBerthResult, string?)>(async (db, token) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);

            var ship = await db.DrydockShip.AsNoTracking()
                .Where(s => s.ShipGuid == shipGuid)
                .Select(s => new { s.OwnerUserId, s.State, s.ShipName, s.BerthId, s.CurrentRevision })
                .SingleOrDefaultAsync(token);

            if (ship == null || (requiredOwner is { } owner && ship.OwnerUserId != owner))
                return (DrydockBerthResult.NotFound, null);

            if (ship.State != from)
                return (DrydockBerthResult.WrongState, null);

            var now = DateTime.UtcNow;
            var moved = await db.DrydockShip
                .Where(s => s.ShipGuid == shipGuid && s.State == from)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(s => s.State, DrydockShipState.Sold)
                    .SetProperty(s => s.StateChangedAt, now)
                    .SetProperty(s => s.UpdatedAt, now)
                    .SetProperty(s => s.CheckedOutRoundId, (int?)null)
                    .SetProperty(s => s.LastBerthId, s => s.BerthId ?? s.LastBerthId)
                    .SetProperty(s => s.BerthId, (int?)null), token);

            if (moved == 0)
                return (DrydockBerthResult.WrongState, null);

            db.DrydockAudit.Add(new DrydockAudit
            {
                ShipGuid = shipGuid,
                ShipName = ship.ShipName,
                BerthId = ship.BerthId,
                Action = DrydockAuditAction.ShipSold,
                ActorUserId = actorUserId,
                SubjectUserId = ship.OwnerUserId,
                Revision = ship.CurrentRevision,
                RoundId = roundId,
                Reason = live
                    ? $"sold for {price} (appraisal {appraisal}), live at the shipyard"
                    : $"sold for {price} (appraisal {appraisal})",
                CreatedAt = now,
            });

            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);
            return (DrydockBerthResult.Success, ship.ShipName);
        }, ct);
    }

    /// <summary>
    /// The owner renames a stored ship. A row update only: the hull and its deed take the name
    /// the next time the ship is retrieved. The old and new names go on the timeline, and the
    /// old one stays searchable there. Conditional on the row still being stored, so a rename
    /// cannot land on a hull a retrieve is stamping with the name it read a moment earlier.
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

            var ship = await db.DrydockShip.AsNoTracking()
                .Where(s => s.ShipGuid == shipGuid)
                .Select(s => new { s.OwnerUserId, s.State, s.ShipName, s.BerthId, s.CurrentRevision })
                .SingleOrDefaultAsync(token);

            if (ship == null || ship.OwnerUserId != ownerUserId)
                return DrydockBerthResult.NotFound;

            if (ship.State != DrydockShipState.Stored)
                return DrydockBerthResult.WrongState;

            var now = DateTime.UtcNow;
            var moved = await db.DrydockShip
                .Where(s => s.ShipGuid == shipGuid && s.State == DrydockShipState.Stored && s.OwnerUserId == ownerUserId)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(s => s.ShipName, newName)
                    .SetProperty(s => s.UpdatedAt, now), token);

            if (moved == 0)
                return DrydockBerthResult.WrongState;

            var oldName = ship.ShipName;
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

            if (!string.IsNullOrWhiteSpace(filter.Search))
            {
                var search = filter.Search.Trim();
                if (Guid.TryParse(search, out var id))
                {
                    query = query.Where(s => s.ShipGuid == id || s.OwnerUserId == id);
                }
                else
                {
                    // Past names live on the audit rows as snapshots, so a ship renamed to hide
                    // is still found under the name the complaint was filed with.
                    var needle = search.ToLowerInvariant();
                    query = query.Where(s => s.ShipName.ToLower().Contains(needle)
                        || s.Owner.LastSeenUserName.ToLower().Contains(needle)
                        || db.DrydockAudit.Any(a => a.ShipGuid == s.ShipGuid && a.ShipName != null && a.ShipName.ToLower().Contains(needle)));
                }
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
                Action = DrydockAuditAction.RevisionPromoted,
                ActorUserId = actorUserId,
                SubjectUserId = ship.OwnerUserId,
                Revision = next,
                RoundId = roundId,
                Reason = string.IsNullOrWhiteSpace(reason) ? $"promoted revision {revision}" : $"promoted revision {revision}: {reason}",
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
    /// Admin: returns a ship that is out, impounded or terminal to the drydock, into a named berth.
    /// Whether the ship is really lost is the admin's call. The one thing this cannot know is
    /// whether a live grid still carries the id, and the system checks that before calling.
    ///
    /// <para>Out of the impound lot this is a release into a chosen berth, and the timeline says
    /// so: the row is <see cref="DrydockAuditAction.ImpoundReleased"/>, the same as the release that
    /// picks the berth itself, and never "restored", which is the word for a hull judged lost. Out
    /// of an abandon it is the admin's undo of the owner's decision, and the row is
    /// <see cref="DrydockAuditAction.AbandonReversed"/> for the same reason.</para>
    ///
    /// <para>A sold hull comes back only through the sale reversal, which decides about the money
    /// before anything else; a plain restore would hand it back with the price left with the
    /// owner. A hull in escrow is spoken for: seating it under a standing offer leaves the offer
    /// resolving against a row that already moved. The move is one conditional update on the state
    /// the row was read in.</para>
    /// </summary>
    /// <param name="fromSale">Set by the sale reversal, which is the one caller allowed to restore a sold hull.</param>
    public Task<DrydockBerthResult> TryRestoreShip(
        Guid shipGuid,
        int berthId,
        Guid? actorUserId,
        int? roundId,
        string reason,
        bool fromSale = false,
        CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);

            var ship = await db.DrydockShip.AsNoTracking()
                .Where(s => s.ShipGuid == shipGuid)
                .Select(s => new { s.OwnerUserId, s.State, s.SizeClass, s.ShipName, s.CurrentRevision })
                .SingleOrDefaultAsync(token);

            if (ship == null)
                return DrydockBerthResult.NotFound;

            // The panel hides the verb on both; this is what stops a forged message.
            if (ship.State is DrydockShipState.Stored or DrydockShipState.InEscrow)
                return DrydockBerthResult.WrongState;

            if (ship.State == DrydockShipState.Sold && !fromSale)
                return DrydockBerthResult.WrongState;

            var (fit, _) = await ResolveBerth(db, shipGuid, ship.OwnerUserId, ship.SizeClass, berthId, null, new HashSet<int>(), token);
            if (fit != DrydockBerthResult.Success)
                return fit;

            var now = DateTime.UtcNow;
            var from = ship.State;
            var action = from switch
            {
                DrydockShipState.Impounded => DrydockAuditAction.ImpoundReleased,
                DrydockShipState.Abandoned => DrydockAuditAction.AbandonReversed,
                _ => DrydockAuditAction.Restore,
            };

            int moved;
            try
            {
                moved = await db.DrydockShip
                    .Where(s => s.ShipGuid == shipGuid && s.State == from)
                    .ExecuteUpdateAsync(set => set
                        .SetProperty(s => s.State, DrydockShipState.Stored)
                        .SetProperty(s => s.StateChangedAt, now)
                        .SetProperty(s => s.CheckedOutRoundId, (int?)null)
                        .SetProperty(s => s.LastBerthId, s => s.BerthId)
                        .SetProperty(s => s.BerthId, (int?)berthId)
                        .SetProperty(s => s.UpdatedAt, now), token);
            }
            catch (Exception e) when (IsBerthUniqueViolation(e))
            {
                return DrydockBerthResult.BerthOccupied;
            }

            if (moved == 0)
                return DrydockBerthResult.WrongState;

            db.DrydockAudit.Add(new DrydockAudit
            {
                ShipGuid = shipGuid,
                ShipName = ship.ShipName,
                BerthId = berthId,
                Action = action,
                ActorUserId = actorUserId,
                SubjectUserId = ship.OwnerUserId,
                Revision = ship.CurrentRevision,
                RoundId = roundId,
                Reason = reason,
                CreatedAt = now,
            });

            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);

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

    /// <summary>
    /// Set to file the hull into the impound lot rather than into a berth. The berth is left empty
    /// and <see cref="DrydockShip.LastBerthId"/> keeps whichever one the hull came from, because
    /// redemption needs a free berth and a hull still sitting in its own would satisfy that for
    /// nothing. Null files the ordinary way.
    /// </summary>
    public DrydockImpound? Impound { get; init; }

    /// <summary>
    /// Impound only: how many occupants the pipeline moved off the hull before filing it. Not a
    /// column; it goes into the audit row's reason, so the timeline says the hull was taken with
    /// people on it.
    /// </summary>
    public int Evicted { get; init; }
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
    int? CurrentRoundId,
    // One box from the admin panel: a ship id or account id when it parses as one, else text
    // matched against the owner's name, the ship's name, and every name the ship has had.
    string? Search = null);

/// <summary>One hull with its history and timeline, newest first, and which revisions still have a document.</summary>
public sealed record DrydockShipDetail(
    DrydockShip Ship,
    List<DrydockRevision> Revisions,
    HashSet<int> RevisionsWithBlob,
    List<DrydockAudit> Timeline);
