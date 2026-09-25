using System.Collections.Generic;
using Content.Server.Pinpointer;
using Content.Server.Power.EntitySystems;
using Content.Shared.Containers.ItemSlots;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Reflection;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Timing;

namespace Content.Server._Triad.Drydock.Loader;

/// <summary>
/// The store's walk: every savable entity from the grid down in walk order, and the stable id each is given.
/// <paramref name="UnsavedByPrototype"/> counts the entities the walk stopped at because their prototype is not savable, by
/// prototype, and <paramref name="DroppedByPrototype"/> counts everything under them, which is left out with them.
/// </summary>
public readonly record struct DrydockWalk(
    List<EntityUid> Aboard,
    Dictionary<EntityUid, long> Ids,
    int Unsaved,
    Dictionary<string, int> UnsavedByPrototype,
    Dictionary<string, int> DroppedByPrototype);

/// <summary>
/// A ship as rows and back: the store walks a grid into a <see cref="DrydockImage"/>, and the load builds one back onto a
/// map through the engine's own deserializer, driven one phase at a time with the image's rows applied between its
/// component pass and its startup. The engine owns allocation and lifecycle; the codec owns every value.
///
/// <para>Both halves are sessions of separate phase calls (<see cref="BeginStore"/>, <see cref="BeginLoad"/>), so a caller
/// can time them or, later, slice between them. Nothing here reads the environment or measures anything: a caller that
/// wants a time wraps a call, and a caller that wants a write's cost passes an <see cref="IDrydockStoreProbe"/>. A
/// manifest member is always applied unless the caller's <see cref="DrydockLoadOptions.HoldOff"/> says otherwise.</para>
/// </summary>
public sealed partial class DrydockImageSystem : EntitySystem
{
    /// <summary>The appearance row's name. The live appearance dictionary is not a data field, so its entries travel in a row of their own.</summary>
    public const string AppearanceRow = "~appearance";

    /// <summary>The carried row's name: values <see cref="GridStoringEvent"/> subscribers carried, keyed by their own keys.</summary>
    public const string CarriedRow = "~carried";

    [Dependency] internal ISerializationManager Serialization = default!;
    [Dependency] internal IGameTiming Timing = default!;
    [Dependency] internal IComponentFactory ComponentFactory = default!;
    [Dependency] internal ITileDefinitionManager TileDefinitions = default!;
    [Dependency] internal IReflectionManager Reflection = default!;
    [Dependency] internal IPrototypeManager Prototypes = default!;
    [Dependency] internal SharedMapSystem Maps = default!;
    [Dependency] internal SharedTransformSystem Xforms = default!;
    [Dependency] internal MetaDataSystem Meta = default!;
    [Dependency] internal SharedAppearanceSystem Appearances = default!;
    [Dependency] internal ItemSlotsSystem ItemSlots = default!;
    [Dependency] internal ExtensionCableSystem Cables = default!;
    [Dependency] internal PinpointerSystem Pinpointers = default!;

    internal IEntityManager Entities => EntityManager;

    /// <summary>
    /// The store's walk and its id pass: every savable entity from the grid down, each given its stable id in walk order,
    /// parents before children. An entity that is not <see cref="IsStored"/> is left out with everything under it.
    /// </summary>
    public DrydockWalk Walk(EntityUid grid)
    {
        var aboard = new List<EntityUid>();
        var unsaved = 0;
        var unsavedBy = new Dictionary<string, int>(StringComparer.Ordinal);
        var droppedBy = new Dictionary<string, int>(StringComparer.Ordinal);
        var stack = new Stack<EntityUid>();
        stack.Push(grid);
        while (stack.TryPop(out var uid))
        {
            if (!IsStored(uid))
            {
                unsaved++;
                var name = PrototypeName(uid);
                unsavedBy[name] = unsavedBy.GetValueOrDefault(name) + 1;

                // Everything under it goes with it: counted, not stored.
                var under = new Stack<EntityUid>();
                PushChildren(uid, under);
                while (under.TryPop(out var dropped))
                {
                    var droppedName = PrototypeName(dropped);
                    droppedBy[droppedName] = droppedBy.GetValueOrDefault(droppedName) + 1;
                    PushChildren(dropped, under);
                }

                continue;
            }

            aboard.Add(uid);
            var children = Transform(uid).ChildEnumerator;
            while (children.MoveNext(out var child))
                stack.Push(child);
        }

        var ids = new Dictionary<EntityUid, long>();
        for (var i = 0; i < aboard.Count; i++)
            ids[aboard[i]] = i + 1;

        return new DrydockWalk(aboard, ids, unsaved, unsavedBy, droppedBy);
    }

    private string PrototypeName(EntityUid uid) => MetaData(uid).EntityPrototype?.ID ?? "(no prototype)";

    private void PushChildren(EntityUid uid, Stack<EntityUid> stack)
    {
        var children = Transform(uid).ChildEnumerator;
        while (children.MoveNext(out var child))
            stack.Push(child);
    }

    /// <summary>
    /// Whether the store keeps an entity: the engine's own rule, that a prototype which is not map-savable is not saved.
    /// The one place the rule lives.
    /// </summary>
    private bool IsStored(EntityUid uid) =>
        MetaData(uid).EntityPrototype is not { MapSavable: false };

    /// <summary>
    /// The store's despawn, as the drydock's does it: the hull moved onto a fresh paused staging map, then the grid and the
    /// map deleted, in the order the drydock queues them (DrydockSystem.cs:655-666). Whatever a terminate handler throws
    /// out of the hull, a disposal unit's contents among them, lands on the staging map and goes with it. Deleted here and
    /// not queued, because the caller loads in the same frame; <paramref name="stagingMap"/> false deletes the grid where
    /// it stands, which is the control a caller can compare the staging map against.
    /// </summary>
    public void Despawn(EntityUid grid, bool stagingMap = true)
    {
        if (!stagingMap)
        {
            Del(grid);
            return;
        }

        var staging = Maps.CreateMap(out _, runMapInit: true);
        Maps.SetPaused(staging, true);
        Xforms.SetCoordinates(grid, new EntityCoordinates(staging, System.Numerics.Vector2.Zero));
        Del(grid);
        Del(staging);
    }

    /// <summary>Starts a store of <paramref name="grid"/>: call <see cref="DrydockStoreSession.WriteEntity"/> for each entity in <see cref="DrydockStoreSession.Aboard"/>, then <see cref="DrydockStoreSession.WriteTiles"/> and <see cref="DrydockStoreSession.Complete"/>.</summary>
    public DrydockStoreSession BeginStore(EntityUid grid, IDrydockStoreProbe? probe = null) =>
        new(this, grid, probe);

    /// <summary>The whole store in one call, for a caller that does not slice or time it.</summary>
    public DrydockImageStoreResult Store(EntityUid grid, IDrydockStoreProbe? probe = null)
    {
        var session = BeginStore(grid, probe);
        foreach (var uid in session.Aboard)
            session.WriteEntity(uid);

        session.WriteTiles();
        return session.Complete();
    }

    /// <summary>
    /// Starts a load of <paramref name="image"/> onto <paramref name="mapUid"/>: call <see cref="DrydockLoadSession.CreateEntities"/>,
    /// <see cref="DrydockLoadSession.ApplyRows"/>, <see cref="DrydockLoadSession.Start"/> and <see cref="DrydockLoadSession.Complete"/> in that order.
    /// The rows are parsed here.
    /// </summary>
    public DrydockLoadSession BeginLoad(DrydockImage image, EntityUid mapUid, DrydockLoadOptions? options = null) =>
        new(this, image, mapUid, options ?? new DrydockLoadOptions());

    /// <summary>The whole load in one call, for a caller that does not slice or time it.</summary>
    public DrydockLoadResult Load(DrydockImage image, EntityUid mapUid, DrydockLoadOptions? options = null)
    {
        var session = BeginLoad(image, mapUid, options);
        session.CreateEntities();
        session.ApplyRows();
        session.Start();
        return session.Complete();
    }

    internal void RaiseStoring(EntityUid uid, ref GridStoringEvent ev) =>
        RaiseLocalEvent(uid, ref ev);

    internal void RaiseRestoring(ref GridRestoringEvent ev) =>
        RaiseLocalEvent(ref ev);

    internal void RaiseRestored(EntityUid uid, ref GridRestoredEvent ev) =>
        RaiseLocalEvent(uid, ref ev);

    internal void RaiseRestored(ref GridRestoredEvent ev) =>
        RaiseLocalEvent(ref ev);

    /// <summary>
    /// Where the loader's power edge hooks. Ruled: the off edge goes to receivers still unpowered after the power net's
    /// FIRST half-second batch, not the first tick. Not built; the load result names the grid and every restored entity,
    /// which is what a per-grid pending marker needs.
    /// </summary>
    internal void ArmPowerEdge(DrydockLoadResult result)
    {
    }
}
