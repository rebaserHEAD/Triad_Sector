using Content.Server._Triad.Worldgen.Cells; // Triad: seeded interior rolls
using Content.Server.Worldgen.Components.Debris;
using Content.Shared.Maps;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Random;

namespace Content.Server.Worldgen.Systems.Debris;

/// <summary>
///     This handles populating simple structures, simply using a loot table for each tile.
/// </summary>
public sealed class SimpleFloorPlanPopulatorSystem : BaseWorldSystem
{
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly ITileDefinitionManager _tileDefinition = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;

    /// <inheritdoc />
    public override void Initialize()
    {
        SubscribeLocalEvent<SimpleFloorPlanPopulatorComponent, LocalStructureLoadedEvent>(OnFloorPlanBuilt);
    }

    private void OnFloorPlanBuilt(EntityUid uid, SimpleFloorPlanPopulatorComponent component,
        LocalStructureLoadedEvent args)
    {
        // Triad: pre-determined debris derives its interior from the record seed, so a rock that
        // unloads and reloads comes back with the entities this pass spawned. A RoomFill marker
        // can be one of them, and the room it goes on to stamp is picked on the shared RNG, so
        // that much does re-roll. Anything spawned outside the sensed tier keeps rolling on the
        // shared RNG too.
        var rand = SeededRandom.ForStage(EntityManager, uid, SeededRandom.InteriorStage) ?? _random;

        var placeables = new List<string?>(4);
        var grid = Comp<MapGridComponent>(uid);
        var enumerator = _map.GetAllTilesEnumerator(uid, grid);
        while (enumerator.MoveNext(out var tile))
        {
            var coords = _map.GridTileToLocal(uid, grid, tile.Value.GridIndices);
            var selector = tile.Value.Tile.GetContentTileDefinition(_tileDefinition).ID;
            if (!component.Caches.TryGetValue(selector, out var cache))
                continue;

            placeables.Clear();
            cache.GetSpawns(rand, ref placeables); // Triad: seeded when pre-determined

            foreach (var proto in placeables)
            {
                if (proto is null)
                    continue;

                Spawn(proto, coords);
            }
        }
    }
}

