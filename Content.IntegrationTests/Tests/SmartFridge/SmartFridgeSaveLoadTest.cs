using System.Numerics;
using Content.Shared.SmartFridge;
using Robust.Shared.Containers;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests.SmartFridge;

/// <summary>
/// Regression test for the ship-save crash where a stocked SmartFridge aborted grid serialization
/// (record-struct dictionary key on <see cref="SmartFridgeComponent.ContainedEntries"/>). Saving a grid
/// with a filled fridge must round-trip, and the derived index must be rebuilt from the contents on load.
/// </summary>
[TestFixture]
public sealed class SmartFridgeSaveLoadTest
{
    private const string SmartFridgeProtoId = "SmartFridge";
    private const string ItemProtoId = "FoodAmbrosiaVulgaris";

    [Test]
    public async Task SaveLoadStockedFridgeRoundTrips()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entManager = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var mapLoader = entManager.System<MapLoaderSystem>();
        var mapSys = entManager.System<SharedMapSystem>();
        var container = entManager.System<SharedContainerSystem>();

        var rp = new ResPath("/smart fridge save load.yml");

        // Build a grid with a stocked smart fridge and save it.
        await server.WaitPost(() =>
        {
            mapSys.CreateMap(out var mapId);
            var grid = mapManager.CreateGridEntity(mapId);
            var gridUid = grid.Owner;
            mapSys.SetTile(gridUid, grid.Comp, Vector2i.Zero, new Tile(1));
            entManager.RunMapInit(gridUid, entManager.GetComponent<MetaDataComponent>(gridUid));

            var coords = new EntityCoordinates(gridUid, Vector2.One / 2f);
            var fridge = entManager.SpawnEntity(SmartFridgeProtoId, coords);
            var comp = entManager.GetComponent<SmartFridgeComponent>(fridge);

            // Insert an item through the container so the index is populated like in-game.
            var item = entManager.SpawnEntity(ItemProtoId, coords);
            Assert.That(container.TryGetContainer(fridge, comp.Container, out var cont));
            Assert.That(container.Insert(item, cont!));

            // Sanity: the runtime index is populated before the save.
            Assert.That(comp.Entries, Has.Count.EqualTo(1));
            Assert.That(comp.ContainedEntries[comp.Entries[0]], Has.Count.EqualTo(1));

            // Threw NotSupportedException before the fix, aborting the whole grid/ship save.
            Assert.That(mapLoader.TrySaveGrid(gridUid, rp));
        });

        await server.WaitIdleAsync();

        // Load it back and assert the fridge rebuilt its index from the persisted contents.
        await server.WaitPost(() =>
        {
            mapSys.CreateMap(out var mapId);
            Assert.That(mapLoader.TryLoadGrid(mapId, rp, out var grid));

            var found = false;
            var query = entManager.EntityQueryEnumerator<SmartFridgeComponent, TransformComponent>();
            while (query.MoveNext(out _, out var comp, out var xform))
            {
                if (xform.GridUid != grid!.Value.Owner)
                    continue;

                found = true;
                Assert.That(comp.Entries, Has.Count.EqualTo(1));
                Assert.That(comp.ContainedEntries.TryGetValue(comp.Entries[0], out var set));
                Assert.That(set, Has.Count.EqualTo(1));
            }

            Assert.That(found, "Loaded grid did not contain the saved smart fridge.");
        });

        await pair.CleanReturnAsync();
    }
}
