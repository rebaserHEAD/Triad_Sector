using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Database;
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
    /// <returns>The revision number filed.</returns>
    public Task<int> FileRevision(DrydockRevisionRequest request, byte[] blob, int keepBlobs, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);

            var now = DateTime.UtcNow;

            var ship = await db.DrydockShip
                .SingleOrDefaultAsync(s => s.ShipGuid == request.ShipGuid, token);

            if (ship == null)
            {
                ship = new DrydockShip
                {
                    ShipGuid = request.ShipGuid,
                    OwnerUserId = request.OwnerUserId,
                    State = DrydockShipState.Stored,
                    StateChangedAt = now,
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
                Manifest = request.Manifest,
            });

            db.DrydockBlob.Add(new DrydockBlob
            {
                ShipGuid = request.ShipGuid,
                Revision = revision,
                Blob = blob,
            });

            ship.CurrentRevision = revision;

            // A player store puts the ship away. A system re-bake must leave the state alone: the
            // ship may be checked out and flying while the ladder rewrites an older revision.
            if (request.Kind == DrydockRevisionKind.PlayerStore || request.Kind == DrydockRevisionKind.LegacyImport)
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

            return revision;
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
    /// Moves a ship's state and records why. State changes and their audit rows are written together
    /// so the timeline can never disagree with the row it describes.
    /// </summary>
    /// <returns>False when the ship is unknown, or when it is already in the requested state.</returns>
    public Task<bool> SetState(
        Guid shipGuid,
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

            var ship = await db.DrydockShip.SingleOrDefaultAsync(s => s.ShipGuid == shipGuid, token);
            if (ship == null || ship.State == state)
                return false;

            var now = DateTime.UtcNow;

            ship.State = state;
            ship.StateChangedAt = now;
            ship.UpdatedAt = now;

            // Only a checkout records a round. Coming back clears it, so "checked out in round N and
            // never came back" stays answerable from the row rather than by reading the timeline.
            ship.CheckedOutRoundId = state == DrydockShipState.CheckedOut ? roundId : null;

            db.DrydockAudit.Add(new DrydockAudit
            {
                ShipGuid = shipGuid,
                ShipName = ship.ShipName,
                Action = action,
                ActorUserId = actorUserId,
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

    /// <summary>The ship's timeline, oldest first.</summary>
    public Task<List<DrydockAudit>> GetAudit(Guid shipGuid, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) => await db.DrydockAudit
            .AsNoTracking()
            .Where(a => a.ShipGuid == shipGuid)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(token), ct);
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

    public required string Manifest { get; init; }
}

/// <summary>What a retrieve reads: the hull row, the revision it is about to rebuild, and the document.</summary>
public sealed record DrydockLoad(DrydockShip Ship, DrydockRevision Revision, byte[] Blob);
