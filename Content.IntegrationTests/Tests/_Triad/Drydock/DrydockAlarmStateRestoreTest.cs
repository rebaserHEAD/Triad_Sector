#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Server._Triad.Drydock;
using Content.Server._Triad.Drydock.Codec;
using Content.Server._Triad.Drydock.Loader;
using Content.Server.Atmos.Monitor.Components;
using Content.Shared.Atmos.Monitor;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// Row 104, and the <see cref="DrydockMemberKind.Fill"/> kind it is the first of: an alarmable's state per sender is a
    /// readonly dictionary, so the manifest pours the stored pairs into the dictionary the component already holds instead
    /// of assigning the member.
    ///
    /// <para>It travels with row 33 or not at all. LastAlarmState is the highest of the states in this map, and the first
    /// power-on edge after a load recomputes it: carrying the state without the map leaves Danger to be checked against an
    /// empty map, so the check disagrees, writes Normal and raises it, and every firelock that was holding a fire shut
    /// opens. This proves the pair arrives; the alarm control on rungs 47 and 81 proves the doors stay shut.</para>
    /// </summary>
    [TestFixture]
    [TestOf(typeof(DrydockCodec))]
    public sealed class DrydockAlarmStateRestoreTest
    {
        private const string DangerSender = "the-danger-sender";
        private const string NormalSender = "the-normal-sender";

        [Test]
        public async Task AnAlarmableKeepsItsStatePerSenderAndAnEmptyMapComesBackEmpty()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var map = await pair.CreateTestMap();
            var image = server.System<DrydockImageSystem>();
            var fidelity = server.System<DrydockFidelitySystem>();

            var grid = map.Grid.Owner;
            var carryingBefore = 0;
            await server.WaitPost(() =>
            {
                server.System<SharedMapSystem>().SetTile(grid, entMan.GetComponent<MapGridComponent>(grid), new Vector2i(1, 0), map.Tile.Tile);

                var carrying = entMan.SpawnEntity("AirAlarm", new EntityCoordinates(grid, 0.5f, 0.5f));
                var empty = entMan.SpawnEntity("AirAlarm", new EntityCoordinates(grid, 1.5f, 0.5f));

                // An alarmable's map is written by its own system from the events its senders raise; the test writes it
                // by hand, as no sender exists here to raise one.
                var comp = entMan.GetComponent<AtmosAlarmableComponent>(carrying);
                comp.NetworkAlarmStates[DangerSender] = AtmosAlarmType.Danger;
                comp.NetworkAlarmStates[NormalSender] = AtmosAlarmType.Normal;
                comp.LastAlarmState = AtmosAlarmType.Danger;
                carryingBefore = comp.NetworkAlarmStates.Count;

                entMan.GetComponent<AtmosAlarmableComponent>(empty).NetworkAlarmStates.Clear();
            });

            Assert.That(carryingBefore, Is.EqualTo(2), "The control: the alarmable holds two senders before the store.");

            DrydockLoadResult result = default!;
            await server.WaitPost(() =>
            {
                var stored = image.Store(grid);
                image.Despawn(grid);
                result = image.Load(stored.Image, map.MapUid);
            });

            await server.WaitAssertion(() =>
            {
                var alarmables = fidelity.GridTreeList(result.Grid)
                    .Where(entMan.HasComponent<AtmosAlarmableComponent>)
                    .ToList();

                Assert.That(alarmables, Has.Count.EqualTo(2), "Both alarmables come back.");

                // The one that carried the pairs was the westerly of the two, and a load keeps where a thing stood.
                var carrying = alarmables
                    .OrderBy(uid => entMan.GetComponent<TransformComponent>(uid).LocalPosition.X)
                    .First();
                var empty = alarmables
                    .OrderByDescending(uid => entMan.GetComponent<TransformComponent>(uid).LocalPosition.X)
                    .First();

                var carried = entMan.GetComponent<AtmosAlarmableComponent>(carrying);
                var blank = entMan.GetComponent<AtmosAlarmableComponent>(empty);

                Assert.Multiple(() =>
                {
                    Assert.That(carried.NetworkAlarmStates, Is.Not.Null, "The map is never null after a load.");
                    Assert.That(carried.NetworkAlarmStates, Has.Count.EqualTo(2), "Both senders come back, and no third.");
                    Assert.That(carried.NetworkAlarmStates.GetValueOrDefault(DangerSender), Is.EqualTo(AtmosAlarmType.Danger),
                        "The sender that was in danger still is.");
                    Assert.That(carried.NetworkAlarmStates.GetValueOrDefault(NormalSender), Is.EqualTo(AtmosAlarmType.Normal),
                        "The sender that was normal still is.");
                    Assert.That(carried.LastAlarmState, Is.EqualTo(AtmosAlarmType.Danger),
                        "Row 33 travels with row 104: the state the map adds up to comes back with it.");

                    // A Fill that assigned rather than poured would leave this one null, and a Fill that merged rather
                    // than cleared would leave it holding whatever the prototype's instance had.
                    Assert.That(blank.NetworkAlarmStates, Is.Not.Null, "An alarmable stored with an empty map comes back with an empty map, not a null one.");
                    Assert.That(blank.NetworkAlarmStates, Is.Empty, "An alarmable stored with an empty map comes back holding nobody.");
                });
            });

            await pair.CleanReturnAsync();
        }
    }
}
