using Content.Server._Triad.Worldgen.Cells; // Triad: seeded interior rolls
using Content.Server.Worldgen.Components.Debris;
using Content.Shared.Maps;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes; // Triad: spawner-marker seed stamping
using Robust.Shared.Random;

namespace Content.Server.Worldgen.Systems.Debris;

/// <summary>
///     This handles populating simple structures, simply using a loot table for each tile.
/// </summary>
public sealed class SimpleFloorPlanPopulatorSystem : BaseWorldSystem
{
    [Dependency] private readonly IPrototypeManager _protoManager = default!; // Triad
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly ITileDefinitionManager _tileDefinition = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;

    // Triad: whether a prototype carries one of the spawner-marker components whose MapInit rolls
    // should be seeded on pre-determined debris. Cached because the populator asks per spawn and
    // the answer is static for the round.
    private readonly Dictionary<string, bool> _spawnerProtoCache = new();

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
        TryComp<PredeterminedShapeComponent>(uid, out var shape); // Triad: loot-marker seeds

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

            var markerIndex = 0; // Triad: de-correlates multiple markers on one tile
            foreach (var proto in placeables)
            {
                if (proto is null)
                    continue;

                // Triad: spawner markers on pre-determined debris get a salted seed of their own,
                // so the rolls they make at MapInit (which crate, how many, scatter) come out
                // identical when the rock re-materializes. Stamped via spawn override because
                // MapInit runs synchronously inside Spawn: there is no after.
                if (shape is not null && HasSpawnerComponent(proto))
                {
                    var spawnSeed = SeededRandom.SaltFor(shape.Seed, SeededRandom.LootStage,
                        tile.Value.GridIndices) + markerIndex++;
                    var overrides = new ComponentRegistry();
                    overrides[EntityManager.ComponentFactory.GetComponentName(typeof(PredeterminedSpawnComponent))] =
                        new EntityPrototype.ComponentRegistryEntry(
                            new PredeterminedSpawnComponent { Seed = spawnSeed }, new());
                    EntityManager.SpawnAttachedTo(proto, coords, overrides);
                    continue;
                }

                Spawn(proto, coords);
            }
        }
    }

    // Triad: see _spawnerProtoCache.
    private bool HasSpawnerComponent(string protoId)
    {
        if (_spawnerProtoCache.TryGetValue(protoId, out var has))
            return has;

        has = _protoManager.TryIndex<EntityPrototype>(protoId, out var proto)
              && (proto.Components.ContainsKey("RandomSpawner")
                  || proto.Components.ContainsKey("ConditionalSpawner")
                  || proto.Components.ContainsKey("EntityTableSpawner"));

        _spawnerProtoCache[protoId] = has;
        return has;
    }
}

