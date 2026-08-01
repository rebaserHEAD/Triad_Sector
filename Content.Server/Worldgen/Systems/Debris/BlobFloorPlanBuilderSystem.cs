// using System.Linq; // Triad: removed - only the original body below needed it, for the
// .Select(...).ToList() at its tail. The replacement builds its tile list directly.
using Content.Server._Triad.Worldgen.Cells; // Triad: PredeterminedShapeComponent
using Content.Shared._Triad.Worldgen; // Triad: extracted seed-deterministic shape walk
using Content.Server.Worldgen.Components.Debris;
using Content.Shared.Maps;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Random;

namespace Content.Server.Worldgen.Systems.Debris;

/// <summary>
///     This handles building the floor plans for "blobby" debris.
/// </summary>
public sealed class BlobFloorPlanBuilderSystem : BaseWorldSystem
{
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly ITileDefinitionManager _tileDefinition = default!;
    [Dependency] private readonly TileSystem _tiles = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;

    /// <inheritdoc />
    public override void Initialize()
    {
        SubscribeLocalEvent<BlobFloorPlanBuilderComponent, ComponentStartup>(OnBlobFloorPlanBuilderStartup);
    }

    private void OnBlobFloorPlanBuilderStartup(EntityUid uid, BlobFloorPlanBuilderComponent component,
        ComponentStartup args)
    {
        var grid = Comp<MapGridComponent>(uid);

        // Triad: startup fires for deserialized entities too, and a deserialized debris grid (a
        // captured blob restoring, a mapper-saved rock) arrives with its chunk data already
        // loaded. Re-rolling here would lay the seed shape straight over that state, which is
        // how a restored rock got its mined-out tiles back. The builder's job is to put a floor
        // plan where none exists; a grid that already has tiles is built, whoever built it.
        if (grid.ChunkCount > 0)
            return;

        PlaceFloorplanTiles(uid, component, grid);
    }

    private void PlaceFloorplanTiles(EntityUid gridUid, BlobFloorPlanBuilderComponent comp, MapGridComponent grid)
    {
        // Triad: walk extracted to BlobShapeGen.Roll (seed-deterministic, so a radar-painted shape
        // preview rolled ahead of materialization matches what actually gets built here). Original
        // body kept below, commented, so future upstream merges surface the conflict. Everything
        // from here down to the blank line above the replacement is upstream's, comments and all,
        // which is why its own comments are double-prefixed: they describe the dead body, not this
        // method.
        //
        // // NO MORE THAN TWO ALLOCATIONS THANK YOU VERY MUCH.
        // // TODO: Just put these on a field instead then?
        // // Also the end of the method has a big LINQ which is gonna blow this out the water.
        // var spawnPoints = new HashSet<Vector2i>(comp.FloorPlacements * 6);
        // var taken = new Dictionary<Vector2i, Tile>(comp.FloorPlacements * 5);
        //
        // void PlaceTile(Vector2i point)
        // {
        //     // Assume we already know that the spawn point is safe.
        //     spawnPoints.Remove(point);
        //     var north = point.Offset(Direction.North);
        //     var south = point.Offset(Direction.South);
        //     var east = point.Offset(Direction.East);
        //     var west = point.Offset(Direction.West);
        //     var radsq = Math.Pow(comp.Radius,
        //         2); // I'd put this outside but i'm not 100% certain caching it between calls is a gain.
        //
        //     // The math done is essentially a fancy way of comparing the distance from 0,0 to the radius,
        //     // and skipping the sqrt normally needed for dist.
        //     if (!taken.ContainsKey(north) && Math.Pow(north.X, 2) + Math.Pow(north.Y, 2) <= radsq)
        //         spawnPoints.Add(north);
        //     if (!taken.ContainsKey(south) && Math.Pow(south.X, 2) + Math.Pow(south.Y, 2) <= radsq)
        //         spawnPoints.Add(south);
        //     if (!taken.ContainsKey(east) && Math.Pow(east.X, 2) + Math.Pow(east.Y, 2) <= radsq)
        //         spawnPoints.Add(east);
        //     if (!taken.ContainsKey(west) && Math.Pow(west.X, 2) + Math.Pow(west.Y, 2) <= radsq)
        //         spawnPoints.Add(west);
        //
        //     var tileDef = _tileDefinition[_random.Pick(comp.FloorTileset)];
        //     taken.Add(point, new Tile(tileDef.TileId, 0, _tiles.PickVariant((ContentTileDefinition) tileDef)));
        // }
        //
        // PlaceTile(Vector2i.Zero);
        //
        // for (var i = 0; i < comp.FloorPlacements; i++)
        // {
        //     var point = _random.Pick(spawnPoints);
        //     PlaceTile(point);
        //
        //     if (comp.BlobDrawProb > 0.0f)
        //     {
        //         if (!taken.ContainsKey(point.Offset(Direction.North)) && _random.Prob(comp.BlobDrawProb))
        //             PlaceTile(point.Offset(Direction.North));
        //         if (!taken.ContainsKey(point.Offset(Direction.South)) && _random.Prob(comp.BlobDrawProb))
        //             PlaceTile(point.Offset(Direction.South));
        //         if (!taken.ContainsKey(point.Offset(Direction.East)) && _random.Prob(comp.BlobDrawProb))
        //             PlaceTile(point.Offset(Direction.East));
        //         if (!taken.ContainsKey(point.Offset(Direction.West)) && _random.Prob(comp.BlobDrawProb))
        //             PlaceTile(point.Offset(Direction.West));
        //     }
        // }
        //
        // _map.SetTiles(gridUid, grid, taken.Select(x => (x.Key, x.Value)).ToList());

        var seed = TryComp<PredeterminedShapeComponent>(gridUid, out var shape) ? shape.Seed : _random.Next();
        var rng = new System.Random(seed);
        var tiles = BlobShapeGen.Roll(rng, comp.Radius, comp.FloorPlacements, comp.BlobDrawProb,
            comp.FloorTileset.Count);

        var taken = new List<(Vector2i, Tile)>(tiles.Count);
        foreach (var tile in tiles)
        {
            var tileDef = _tileDefinition[comp.FloorTileset[tile.TilesetIndex]];
            // Triad: variant picked by continuing the walk's own deterministic stream, not the
            // shared RNG. The sprite variant is part of "the same rock comes back". This cannot
            // disturb describe/client shape parity: those only consume Roll's returned tiles,
            // and Roll's draw sequence is untouched; the variant draws happen after it returns.
            taken.Add((tile.Pos, new Tile(tileDef.TileId, 0, _tiles.PickVariant((ContentTileDefinition) tileDef, rng))));
        }

        _map.SetTiles(gridUid, grid, taken);
    }
}

