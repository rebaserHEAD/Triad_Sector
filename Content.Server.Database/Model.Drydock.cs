using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;

// ReSharper disable EntityFramework.ModelValidation.UnlimitedStringLength

namespace Content.Server.Database;

//
// Triad: the drydock, a database-backed replacement for client-held ship save files.
// Kept in its own file rather than in Model.cs so a merge from upstream conflicts on two lines
// there instead of on a hundred here. Schema is locked; see the Drydock System Design wiki page
// before changing a column, because a persisted schema is the one artifact that cannot be cheaply
// changed once real ships exist.
//

internal static class ModelDrydock
{
    public static void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Explicit: the key is named for what it is rather than "Id", so convention will not find it.
        modelBuilder.Entity<DrydockShip>()
            .HasKey(s => s.ShipGuid);

        modelBuilder.Entity<DrydockShip>()
            .HasOne(s => s.Owner)
            .WithMany()
            .HasForeignKey(s => s.OwnerUserId)
            .HasPrincipalKey(p => p.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        // The stored-ship list and every admin query start from an owner.
        modelBuilder.Entity<DrydockShip>()
            .HasIndex(s => s.OwnerUserId);

        // "Which ships are checked out and never came back" is the adjudication question, and it
        // wants to be an index scan rather than a table scan once the fleet is large.
        modelBuilder.Entity<DrydockShip>()
            .HasIndex(s => new { s.State, s.StateChangedAt });

        modelBuilder.Entity<DrydockRevision>()
            .HasKey(r => new { r.ShipGuid, r.Revision });

        modelBuilder.Entity<DrydockRevision>()
            .HasOne(r => r.Ship)
            .WithMany(s => s.Revisions)
            .HasForeignKey(r => r.ShipGuid)
            .OnDelete(DeleteBehavior.Cascade);

        // Null on system re-bakes, and set null rather than cascade if the player row ever goes:
        // losing who stored a revision must never take the revision with it.
        modelBuilder.Entity<DrydockRevision>()
            .HasOne(r => r.Actor)
            .WithMany()
            .HasForeignKey(r => r.ActorUserId)
            .HasPrincipalKey(p => p.UserId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<DrydockBlob>()
            .HasKey(b => new { b.ShipGuid, b.Revision });

        modelBuilder.Entity<DrydockBlob>()
            .HasOne(b => b.RevisionRow)
            .WithOne(r => r.Blob)
            .HasForeignKey<DrydockBlob>(b => new { b.ShipGuid, b.Revision })
            .OnDelete(DeleteBehavior.Cascade);

        // The timeline read: one ship, oldest to newest.
        modelBuilder.Entity<DrydockAudit>()
            .HasIndex(a => new { a.ShipGuid, a.CreatedAt });

        modelBuilder.Entity<DrydockAudit>()
            .HasIndex(a => a.ActorUserId);

        // "Every ship this player has ever owned" walks transfer rows by recipient.
        modelBuilder.Entity<DrydockAudit>()
            .HasIndex(a => a.SubjectUserId);

        // Berths. Explicit key for the same reason as the ship.
        modelBuilder.Entity<DrydockBerth>()
            .HasKey(b => b.BerthId);

        modelBuilder.Entity<DrydockBerth>()
            .HasOne(b => b.Owner)
            .WithMany()
            .HasForeignKey(b => b.OwnerUserId)
            .HasPrincipalKey(p => p.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        // Every player-facing berth query starts from the owner.
        modelBuilder.Entity<DrydockBerth>()
            .HasIndex(b => b.OwnerUserId);

        // The principal for the ship's composite foreign key below. Redundant with the primary
        // key as a uniqueness fact, but a foreign key needs a key to point at, and pointing at
        // (berth, owner) rather than (berth) is what makes ownership a database invariant.
        modelBuilder.Entity<DrydockBerth>()
            .HasAlternateKey(b => new { b.BerthId, b.OwnerUserId });

        // A ship can sit only in a berth its own owner owns. A transfer that moves the owner but
        // not the berth, or a move into another player's berth, fails here rather than surfacing a
        // round trip later. Restrict on delete: an occupied berth cannot be deleted, move first.
        modelBuilder.Entity<DrydockShip>()
            .HasOne(s => s.Berth)
            .WithMany()
            .HasForeignKey(s => new { s.BerthId, s.OwnerUserId })
            .HasPrincipalKey(b => new { b.BerthId, b.OwnerUserId })
            .OnDelete(DeleteBehavior.Restrict);

        // One hull per berth. Nulls are distinct on both providers, so every ship that is out in
        // the world coexists with every other. This is also the index the free-berth anti-join
        // reads, and the arbiter when two stores race for the last free berth.
        modelBuilder.Entity<DrydockShip>()
            .HasIndex(s => s.BerthId)
            .IsUnique();

        // A hint, never a claim, so it may point at a berth that has since been deleted.
        modelBuilder.Entity<DrydockShip>()
            .HasOne(s => s.LastBerth)
            .WithMany()
            .HasForeignKey(s => s.LastBerthId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

/// <summary>
/// One row per hull, never pruned. Identity, ownership and authority live here; everything that
/// varies between stores lives on <see cref="DrydockRevision"/>.
/// </summary>
public sealed class DrydockShip
{
    /// <summary>
    /// The ship id, also stamped on the grid-side deed. A <see cref="Guid"/> rather than a row id
    /// because it has to outlive the grid and survive store and retrieve cycles unchanged.
    /// </summary>
    public Guid ShipGuid { get; set; }

    /// <summary>Authoritative owner. A transfer is one update here plus an audit row.</summary>
    public Guid OwnerUserId { get; set; }

    public Player Owner { get; set; } = default!;

    /// <summary>
    /// Display cache for the stored-ship list, refreshed on every store so the list can be drawn
    /// without materializing a blob.
    /// </summary>
    public string ShipName { get; set; } = default!;

    /// <summary>
    /// Informational only, and deliberately not load-bearing: a vessel prototype can be renamed or
    /// removed upstream, and a stored ship must not stop being retrievable because of it.
    /// </summary>
    public string? VesselProto { get; set; }

    /// <summary>
    /// Display cache, stored as text rather than an enum so a content-side size taxonomy change
    /// cannot invalidate rows.
    /// </summary>
    public string? SizeClass { get; set; }

    public DrydockShipState State { get; set; }

    public DateTime StateChangedAt { get; set; }

    /// <summary>
    /// The round the ship was checked out in. Together with <see cref="State"/> this answers
    /// "checked out in round N and never came back" from a single row.
    /// </summary>
    public int? CheckedOutRoundId { get; set; }

    public Round? CheckedOutRound { get; set; }

    /// <summary>
    /// Admin flag, independent of <see cref="State"/>: a ship can be under investigation while
    /// still being retrievable, and can be frozen without anyone investigating anything.
    /// </summary>
    public bool Investigating { get; set; }

    public string? AdminNotes { get; set; }

    /// <summary>Pointer into <see cref="DrydockRevision"/>. Zero means nothing filed yet.</summary>
    public int CurrentRevision { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// The berth the hull sits in while stored, null while it is out in the world. A parking
    /// spot, not a home: retrieve vacates it as its last step, and store finds one again.
    /// </summary>
    public int? BerthId { get; set; }

    public DrydockBerth? Berth { get; set; }

    /// <summary>
    /// A hint, never a claim: the berth this hull last sat in, so a store can put it back where it
    /// was when that spot is still free, and an admin restore has a sensible default.
    /// </summary>
    public int? LastBerthId { get; set; }

    public DrydockBerth? LastBerth { get; set; }

    public List<DrydockRevision> Revisions { get; set; } = default!;
}

/// <summary>
/// One slot in a player's drydock, holding at most one hull. A player's drydock is the set of
/// berths they own; there is no garage row because it would only ever be a count.
/// </summary>
public sealed class DrydockBerth
{
    /// <summary>An identity integer: nothing outside the database ever names a berth.</summary>
    public int BerthId { get; set; }

    public Guid OwnerUserId { get; set; }

    public Player Owner { get; set; } = default!;

    /// <summary>
    /// The largest size class this berth accepts, stored as text for the same reason the ship's
    /// class is: a content-side taxonomy change cannot invalidate rows. Compared in code after
    /// parsing, never in SQL; an owner has a handful of berths.
    /// </summary>
    public string MaxSizeClass { get; set; } = default!;

    public DrydockBerthKind Kind { get; set; }

    /// <summary>What makes sell-back computable. Zero for a grant, so a grant refunds nothing.</summary>
    public int PricePaid { get; set; }

    public DateTime PurchasedAt { get; set; }

    public int? PurchasedRoundId { get; set; }

    public Round? PurchasedRound { get; set; }
}

public enum DrydockBerthKind
{
    /// <summary>Bought by the player at a terminal.</summary>
    Purchased = 0,

    /// <summary>
    /// Handed out by the import bridge, a starter grant, or an admin. Refunds nothing on sale,
    /// so a grant can never be turned into credits.
    /// </summary>
    Granted = 1,
}

public enum DrydockShipState
{
    /// <summary>In the drydock and retrievable.</summary>
    Stored = 0,

    /// <summary>Materialized into a round. A second retrieve must refuse rather than hand out a copy.</summary>
    CheckedOut = 1,

    /// <summary>
    /// An administrative freeze pending adjudication. Refuses retrieve without any machine having
    /// decided anything, which is the point: the system records and a person adjudicates.
    /// </summary>
    Held = 2,
}

/// <summary>
/// One row per revision, kept indefinitely. This is the hull's history, and it deliberately does
/// not require a blob to exist: pruning takes blobs, never history.
/// </summary>
public sealed class DrydockRevision
{
    public Guid ShipGuid { get; set; }

    public DrydockShip Ship { get; set; } = default!;

    /// <summary>Monotonic per ship, starting at 1.</summary>
    public int Revision { get; set; }

    public DrydockRevisionKind Kind { get; set; }

    /// <summary>Which revision a re-bake was derived from. Null for a player store.</summary>
    public int? DerivedFromRevision { get; set; }

    /// <summary>Which generation of the re-bake ladder produced this. Zero for a player store.</summary>
    public int RebakeVersion { get; set; }

    /// <summary>
    /// Who stored it, null for the system. Owners change hands, so this is what makes a year-old
    /// history still read correctly after a transfer.
    /// </summary>
    public Guid? ActorUserId { get; set; }

    public Player? Actor { get; set; }

    /// <summary>
    /// Nullable because the re-bake ladder runs between rounds, when there is no round to point at.
    /// </summary>
    public int? CreatedRoundId { get; set; }

    public Round? CreatedRound { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>The engine's map document format version, so the ladder knows what it is holding.</summary>
    public int EngineFormatVer { get; set; }

    /// <summary>Our own sidecar and manifest encoding version, migrated the same way.</summary>
    public int DrydockFormatVer { get; set; }

    /// <summary>
    /// Hash over the set of prototype ids the blob references. One of the two drift classes, and
    /// the one the ladder heals.
    /// </summary>
    public byte[] ProtoFingerprint { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Hash over the captured-state keys the fidelity layer wrote. The other drift class: a C#
    /// rename silently orphans a key, and comparing this is how that stops being silent.
    /// </summary>
    public byte[] CapturedKeyHash { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Over the uncompressed document, so a mismatch on retrieve distinguishes a storage fault
    /// from a serializer fault.
    /// </summary>
    public byte[] Checksum { get; set; } = Array.Empty<byte>();

    /// <summary>Uncompressed document size. The only source of a real blob-size distribution.</summary>
    public int SizeBytes { get; set; }

    /// <summary>
    /// What the drift sweep and the admin diff query. JSON: jsonb on Postgres with a GIN index,
    /// text on SQLite, which means a dev server full-scans the sweep and that is fine.
    /// </summary>
    public string Manifest { get; set; } = default!;

    public DrydockBlob? Blob { get; set; }
}

public enum DrydockRevisionKind
{
    /// <summary>A player put their ship away.</summary>
    PlayerStore = 0,

    /// <summary>The re-bake ladder rewrote an older revision to current content.</summary>
    SystemRebake = 1,

    /// <summary>
    /// Read out of a legacy ship save file by the import bridge. Present from day one because enum
    /// values persist, and adding it later would mean a migration to reinterpret existing rows.
    /// </summary>
    LegacyImport = 2,

    /// <summary>
    /// An admin promoted an older revision as the current one. Filed as a new revision derived
    /// from the old, so history stays append-only and nothing ever rewinds the pointer.
    /// </summary>
    AdminRestore = 3,
}

/// <summary>
/// The document itself, split from the revision so that pruning a blob leaves the history intact.
/// Nothing else belongs in this table.
/// </summary>
public sealed class DrydockBlob
{
    public Guid ShipGuid { get; set; }

    public int Revision { get; set; }

    public DrydockRevision RevisionRow { get; set; } = default!;

    /// <summary>
    /// Compressed by the game server, not by the database. The Postgres column is set to
    /// <c>STORAGE EXTERNAL</c> by migration so TOAST stores it out of line without trying to
    /// recompress data that arrives already compressed.
    /// </summary>
    public byte[] Blob { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// The timeline. Stores and retrieves are logged here alongside the exceptional events, which is
/// what makes it a timeline rather than an exceptions log, and what lets a diff that crosses a
/// change of hands say so.
/// </summary>
public sealed class DrydockAudit
{
    public long Id { get; set; }

    /// <summary>
    /// Deliberately NOT a foreign key: audit outlives the ship record, so a deletion through admin
    /// tooling must not cascade away the evidence of it. Null on berth events that involve no
    /// ship, which is why there is one timeline rather than two.
    /// </summary>
    public Guid? ShipGuid { get; set; }

    /// <summary>
    /// The berth taken, vacated, bought, sold, or moved. No foreign key, for the same reason as
    /// the ship. Store and retrieve rows carry it too, so the timeline says where as well as when.
    /// </summary>
    public int? BerthId { get; set; }

    /// <summary>Name snapshot, because the ship row it came from may no longer exist.</summary>
    public string? ShipName { get; set; }

    public DrydockAuditAction Action { get; set; }

    /// <summary>Who acted. Null for the system. Not a foreign key, for the same reason as above.</summary>
    public Guid? ActorUserId { get; set; }

    /// <summary>Transfer recipient or restore beneficiary.</summary>
    public Guid? SubjectUserId { get; set; }

    public int? Revision { get; set; }

    public int? RoundId { get; set; }

    /// <summary>Free text. On an adjudication this is the reasoning, which is the whole point.</summary>
    public string? Reason { get; set; }

    /// <summary>Stored as UTC. Always write <c>DateTime.UtcNow</c>; a non-UTC value throws on Postgres.</summary>
    public DateTime CreatedAt { get; set; }
}

public enum DrydockAuditAction
{
    Store = 0,
    Retrieve = 1,

    /// <summary>An admin rebuilt a ship judged lost. Always a human decision, never a rule.</summary>
    Restore = 2,

    Transfer = 3,
    Delete = 4,
    Rebake = 5,

    /// <summary>Froze the ship pending adjudication.</summary>
    Hold = 6,

    /// <summary>Released a hold.</summary>
    Release = 7,

    BerthPurchase = 8,
    BerthSale = 9,
    BerthGrant = 10,
    BerthUpgrade = 11,

    /// <summary>A ship moved between two berths of the same owner, by an admin.</summary>
    BerthMove = 12,

    BerthDelete = 13,

    /// <summary>
    /// A retrieve could not use the current revision and materialized an older one. The newer
    /// document still exists until pruning reaches it, which is why this is on the timeline.
    /// </summary>
    Fallback = 14,
}
