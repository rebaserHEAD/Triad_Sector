#nullable enable

using System;
using System.Threading.Tasks;
using Content.Server._Triad.Drydock;
using Content.Server.Advertise.Components;
using Content.Server.Database;
using Content.Shared._Triad.ShipSize;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>A retrieved advertiser goes back in the advert queue (<see cref="DrydockAdvertiseRestoreSystem"/>).</summary>
    [TestFixture]
    [TestOf(typeof(DrydockAdvertiseRestoreSystem))]
    public sealed class DrydockAdvertiseRestoreTest
    {
        private const string VendingId = "VendingMachineCigs";

        /// <summary>
        /// A vending machine retrieved with its next advertisement due within the minute advertises when the time comes,
        /// which it shows by rolling its next one minutes ahead (<c>AdvertiseSystem.cs:129-131</c>). Control: the deadline
        /// came back in the near future, so a roll is the advert firing and not the stored value.
        /// </summary>
        [Test]
        public async Task ARetrievedAdvertiserAdvertisesAgain()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var timing = server.ResolveDependency<IGameTiming>();
            var db = server.ResolveDependency<IServerDbManager>();
            var store = server.ResolveDependency<DrydockStore>();
            var drydock = server.System<DrydockSystem>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await store.AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, _) = await DrydockRoundTripTest.BuildShipAndStation(pair);
            await server.WaitPost(() =>
            {
                var machine = entMan.SpawnEntity(VendingId, new EntityCoordinates(shipGrid, 0.5f, 0.5f));
#pragma warning disable RA0002
                entMan.GetComponent<AdvertiseComponent>(machine).NextAdvertisementTime = timing.CurTime + TimeSpan.FromSeconds(30);
#pragma warning restore RA0002
            });

            var (stored, shipId) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(stored, Is.EqualTo(DrydockStoreResult.Success));
            await pair.RunTicksSync(5);

            var retrieved = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));

            AdvertiseComponent advert = default!;
            var dueIn = TimeSpan.Zero;
            await server.WaitPost(() =>
            {
                var children = entMan.GetComponent<TransformComponent>(retrieved.Grid!.Value).ChildEnumerator;
                while (children.MoveNext(out var child))
                {
                    if (entMan.GetComponent<MetaDataComponent>(child).EntityPrototype?.ID == VendingId)
                        advert = entMan.GetComponent<AdvertiseComponent>(child);
                }

                dueIn = advert.NextAdvertisementTime - timing.CurTime;
            });

            await pair.RunTicksSync((int) Math.Ceiling((dueIn + TimeSpan.FromSeconds(2)).TotalSeconds / timing.TickPeriod.TotalSeconds));

            var nextIn = TimeSpan.Zero;
            await server.WaitPost(() => nextIn = advert.NextAdvertisementTime - timing.CurTime);

            Assert.Multiple(() =>
            {
                Assert.That(dueIn, Is.GreaterThan(TimeSpan.Zero).And.LessThanOrEqualTo(TimeSpan.FromSeconds(30)),
                    "The control: the retrieved machine's advert was due within the minute.");
                Assert.That(nextIn, Is.GreaterThan(TimeSpan.FromMinutes(1)), "The advert fired and rolled the next one minutes ahead.");
            });

            await pair.CleanReturnAsync();
        }
    }
}
