using System;
using System.Collections.Generic;
using Content.Server._Triad.Drydock.Codec;
using Robust.Shared.GameObjects;

namespace Content.Server._Triad.Drydock.Loader;

/// <summary>
/// Called around each row the store writes, so a caller can measure a write without the store timing anything itself.
/// <paramref name="row"/> is a component's registered name, <see cref="DrydockImageSystem.AppearanceRow"/> or
/// <see cref="DrydockCodec.ManifestRow"/>. Optional: a null probe costs the store a null check.
/// </summary>
public interface IDrydockStoreProbe
{
    void Before(EntityUid uid, string row);

    void After(EntityUid uid, string row);
}

/// <summary>What a store leaves besides the image: the manifest members no serializer could write, and what was counted apart.</summary>
/// <param name="Unwritable">Manifest members that could not be written. Each one is a member the image lost, and the store is to be refused for it.</param>
/// <param name="Stripped">Components the manifest strips (a round, a crew member, a live link), by name.</param>
/// <param name="AppearanceSkipped">Appearance values with no serializer, by type name.</param>
/// <param name="Writes">Component rows written.</param>
public sealed record DrydockImageStoreResult(
    DrydockImage Image,
    IReadOnlyList<DrydockUnwritableMember> Unwritable,
    IReadOnlyDictionary<string, int> Stripped,
    IReadOnlyDictionary<string, int> AppearanceSkipped,
    int Writes);

/// <summary>What the caller may change about a load.</summary>
public sealed class DrydockLoadOptions
{
    /// <summary>
    /// A manifest member this returns true for is decoded and not set, so its loss shows. Null holds nothing off, which is
    /// what a load does.
    /// </summary>
    public Func<DrydockManifestMember, bool>? HoldOff { get; init; }
}

/// <summary>A manifest member decoded at the rows, or taken off its component there, held for its moment.</summary>
public sealed record DrydockHeldMember(EntityUid Uid, DrydockManifestMember Member, object? Value);

/// <summary>One load's manifest: what waits for a later moment, and what each moment set, missed or refused.</summary>
public sealed class DrydockManifestApply
{
    internal readonly List<DrydockHeldMember> Held = new();
    private readonly Dictionary<DrydockApplyMoment, int> _set = new();

    /// <summary>Members set per moment other than before init, by member key.</summary>
    public readonly Dictionary<string, int> LaterByMember = new(StringComparer.Ordinal);

    /// <summary>A member whose component the entity no longer had at its moment, by member and prototype.</summary>
    public readonly Dictionary<string, int> Missing = new(StringComparer.Ordinal);

    /// <summary>A member the load would not set, by member, prototype and why.</summary>
    public readonly Dictionary<string, int> Refused = new(StringComparer.Ordinal);

    /// <summary>Members <see cref="DrydockLoadOptions.HoldOff"/> held off, by key.</summary>
    public readonly Dictionary<string, int> OffByKey = new(StringComparer.Ordinal);

    /// <summary>Each seam member by the prototype it was set on, since a seam set is rare and each one is a case.</summary>
    public readonly Dictionary<string, int> SeamByPrototype = new(StringComparer.Ordinal);

    public int Repaired;
    public int AlreadyPaired;
    public int StoredUnpaired;

    public int Set(DrydockApplyMoment moment) => _set.GetValueOrDefault(moment);

    internal void Count(DrydockManifestMember member)
    {
        _set[member.Moment] = _set.GetValueOrDefault(member.Moment) + 1;
        if (member.Moment != DrydockApplyMoment.BeforeInit)
            LaterByMember[member.Key] = LaterByMember.GetValueOrDefault(member.Key) + 1;
    }

    internal void Miss(DrydockManifestMember member, string prototype)
    {
        var key = $"{member.Key} on {prototype}";
        Missing[key] = Missing.GetValueOrDefault(key) + 1;
    }

    internal void Refuse(DrydockManifestMember member, string prototype, string why)
    {
        var key = $"{member.Key} on {prototype}: {why}";
        Refused[key] = Refused.GetValueOrDefault(key) + 1;
    }

    internal void SeamOn(string key) => SeamByPrototype[key] = SeamByPrototype.GetValueOrDefault(key) + 1;
}

/// <summary>What a load did: the grid, every entity by the stable id it was loaded under, and counts of what it changed, missed or refused.</summary>
public sealed class DrydockLoadResult
{
    public required EntityUid Grid { get; init; }

    /// <summary>The loaded entities by the stable id each was loaded under.</summary>
    public required IReadOnlyDictionary<EntityUid, long> Ids { get; init; }

    public required DrydockManifestApply Manifest { get; init; }

    /// <summary>Rows copied into a component the prototype had added, in total and by component.</summary>
    public int Overwrote { get; init; }

    public IReadOnlyDictionary<string, int> OverwroteByType { get; init; } = new Dictionary<string, int>();

    /// <summary>Rows added as read.</summary>
    public int Added { get; init; }

    /// <summary>Prototype components with no row, removed before init, in total and by component.</summary>
    public int Removed { get; init; }

    public IReadOnlyDictionary<string, int> RemovedByType { get; init; } = new Dictionary<string, int>();

    /// <summary>Item slots held back from init, copied into the slot init re-added at the seam, copied into the slot startup re-added, and added whole.</summary>
    public int HeldBackSlots { get; init; }

    public int CopiedAtSeam { get; init; }
    public int CopiedAfterStartup { get; init; }
    public int AddedWhole { get; init; }

    /// <summary>Appearance entries set before init, and those the gate refused, by stored type name.</summary>
    public int AppearanceApplied { get; init; }

    public IReadOnlyDictionary<string, int> AppearanceRefused { get; init; } = new Dictionary<string, int>();

    /// <summary>Appearance components on the load, and how many are still marked modified in the load's tick.</summary>
    public int AppearanceComponents { get; init; }

    public int AppearanceStillDirty { get; init; }

    /// <summary>Prototype ids a manifest member read that no longer resolve, set as null.</summary>
    public IReadOnlyList<(DrydockManifestMember Member, string Id)> UnresolvedPrototypes { get; init; } = Array.Empty<(DrydockManifestMember, string)>();

    /// <summary>Queued lathe batches left out for a recipe that no longer resolves, by recipe id.</summary>
    public IReadOnlyList<string> DroppedBatches { get; init; } = Array.Empty<string>();

    public int TilesStored { get; init; }
    public int TilesRestored { get; init; }
    public int TilesMissing { get; init; }
    public int TilesExtra { get; init; }
}
