using System.Linq;
using Content.Server._Triad.Worldgen.Cells; // Triad: seeded deposit rolls
using Content.Server.Atmos.EntitySystems;
using Content.Server.Worldgen.Components.Debris;
using Robust.Server.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Random;

namespace Content.Server.Worldgen.Systems.Debris;

/// <summary>
///     This is for placing a finite, random number of entities on separate tiles on a structure.
/// </summary>
public sealed class RandomEntityPopulatorSystem : BaseWorldSystem
{
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly MapSystem _map = default!;
    [Dependency] private readonly AtmosphereSystem _atmosphere = default!;

    /// <inheritdoc />
    public override void Initialize()
    {
        SubscribeLocalEvent<RandomEntityPopulatorComponent, LocalStructureLoadedEvent>(OnFloorPlanBuilt);
    }

    private void OnFloorPlanBuilt(Entity<RandomEntityPopulatorComponent> ent, ref LocalStructureLoadedEvent args)
    {
        if (!TryComp<MapGridComponent>(ent, out var mapGrid))
            return;

        // Triad: pre-determined debris derives its deposits from the record seed so they survive
        // an unload/reload cycle. Debris spawned outside the records system rolls as before.
        var rand = SeededRandom.ForStage(EntityManager, ent.Owner, SeededRandom.DepositStage) ?? _random;

        var placeables = new List<string?>(4);
        List<Vector2i>? validTileIndices = null;
        // For each entity populator in the set, select a number between min and max
        foreach (var (paramSet, cache) in ent.Comp.Caches)
        {
            if (!rand.Prob(paramSet.Prob)) // Triad: seeded when pre-determined
                continue;

            var numToGenerate = rand.Next(paramSet.Min, paramSet.Max + 1); // Triad: seeded when pre-determined
            for (var i = 0; i < numToGenerate; i++)
            {
                // Then find a spot (if we can) - on any failure, assume the asteroid is full and move onto the next one, which may have different parameters
                if (!SelectRandomTile(ent, mapGrid, paramSet.CanBeAirSealed, rand, ref validTileIndices, out var coords)) // Triad: seeded when pre-determined
                    break;

                cache.GetSpawns(rand, ref placeables); // Triad: seeded when pre-determined

                foreach (var proto in placeables)
                {
                    if (proto is null)
                        continue;

                    Spawn(proto, coords);
                }
                placeables.Clear();
            }
        }
    }

    private bool SelectRandomTile(EntityUid gridUid,
        MapGridComponent mapComp,
        bool canBeAirSealed,
        IRobustRandom rand, // Triad: seeded when pre-determined
        ref List<Vector2i>? tileIndices,
        out EntityCoordinates targetCoords)
    {
        targetCoords = default;

        if (tileIndices == null)
        {
            var tileIterator = _map.GetAllTiles(gridUid, mapComp, true);
            tileIndices = tileIterator.Select(tile => tile.GridIndices).ToList();
        }

        var found = false;
        for (var i = 0; i < 10; i++)
        {
            if (tileIndices.Count <= 0)
                return false;

            var idx = rand.Next(tileIndices.Count); // Triad: seeded when pre-determined
            if (!canBeAirSealed && _atmosphere.IsTileAirBlocked(gridUid, tileIndices[idx], mapGridComp: mapComp))
                continue;

            found = true;
            targetCoords = _map.GridTileToLocal(gridUid, mapComp, tileIndices[idx]);
            tileIndices.RemoveAt(idx);
            break;
        }

        return found;
    }
}
