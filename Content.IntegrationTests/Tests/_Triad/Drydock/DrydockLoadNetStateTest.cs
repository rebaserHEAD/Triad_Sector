#nullable enable

using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.Server._Triad.Drydock.Loader;
using Content.Shared.Stacks;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// What a client is sent for a restored hull. The engine's startup reset marks each restored networked component as its
    /// prototype's own (<c>EntityDeserializer.ResetNetTicks</c>), and the loader dirties them all after start, so a client
    /// gets the image's values and not the prototype's, and later deltas are legal.
    /// </summary>
    [TestFixture]
    [TestOf(typeof(DrydockLoadSession))]
    public sealed class DrydockLoadNetStateTest
    {
        private const string SheetProtoId = "SheetSteel";
        private const int StoredCount = 7;

        /// <summary>
        /// With PVS on, a client that first sees the hull after it is moved into view, as a player first sees a retrieved
        /// ship at its dock, gets a stack's stored count. Controls: the prototype's own count differs, and the client does
        /// not see the hull while it sits on the other map.
        /// </summary>
        [Test]
        public async Task AClientFirstSeeingARestoredHullGetsItsStoredValues()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
            var server = pair.Server;
            var entMan = server.EntMan;
            var cfg = server.ResolveDependency<IConfigurationManager>();
            var playerMan = server.ResolveDependency<Robust.Server.Player.IPlayerManager>();
            var mapSys = server.System<SharedMapSystem>();
            var xforms = server.System<SharedTransformSystem>();
            var stacks = server.System<SharedStackSystem>();
            var images = server.System<DrydockImageSystem>();
            var session = playerMan.Sessions.First();

            var view = await pair.CreateTestMap();

            var prototypeCount = 0;
            EntityUid hidden = default;
            DrydockImage image = default!;
            var whole = false;
            await server.WaitPost(() =>
            {
                cfg.SetCVar(CVars.NetPVS, true);

                var observer = entMan.SpawnEntity(null, new EntityCoordinates(view.MapUid, Vector2.Zero));
                playerMan.SetAttachedEntity(session, observer);

                hidden = mapSys.CreateMap(out var hiddenId);
                var grid = mapSys.CreateGridEntity(hiddenId);
                mapSys.SetTile(grid.Owner, grid.Comp, Vector2i.Zero, view.Tile.Tile);

                var sheet = entMan.SpawnEntity(SheetProtoId, new EntityCoordinates(grid.Owner, 0.5f, 0.5f));
                prototypeCount = entMan.GetComponent<StackComponent>(sheet).Count;
                stacks.SetCount(sheet, StoredCount);

                var stored = images.Store(grid.Owner);
                whole = stored.Whole;
                image = stored.Image;
                entMan.DeleteEntity(grid.Owner);
            });

            Assert.That(whole, Is.True, "The control: the hull stores whole.");

            EntityUid loadedGrid = default;
            EntityUid loadedSheet = default;
            await server.WaitPost(() =>
            {
                var loaded = images.Load(image, hidden);
                loadedGrid = loaded.Grid;
                loadedSheet = loaded.Ids.Keys.Single(uid => entMan.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID == SheetProtoId);
            });

            var netSheet = entMan.GetNetEntity(loadedSheet);
            await pair.RunTicksSync(10);

            var seenEarly = pair.Client.EntMan.TryGetEntity(netSheet, out var early) && pair.Client.EntMan.EntityExists(early);

            await server.WaitPost(() => xforms.SetCoordinates(loadedGrid, new EntityCoordinates(view.MapUid, new Vector2(3f, 3f))));
            await pair.RunTicksSync(10);

            var clientCount = -1;
            await pair.Client.WaitPost(() =>
            {
                var clientEntMan = pair.Client.EntMan;
                if (clientEntMan.TryGetEntity(netSheet, out var clientSheet) && clientEntMan.TryGetComponent<StackComponent>(clientSheet, out var clientStack))
                    clientCount = clientStack.Count;
            });

            Assert.Multiple(() =>
            {
                Assert.That(prototypeCount, Is.Not.EqualTo(StoredCount), "The control: the prototype's own count differs from the stored one.");
                Assert.That(seenEarly, Is.False, "The control: the client does not see the hull on the other map.");
                Assert.That(clientCount, Is.EqualTo(StoredCount), "The client has to get the stored count, not the prototype's.");
            });

            await server.WaitPost(() =>
            {
                playerMan.SetAttachedEntity(session, null);
                cfg.SetCVar(CVars.NetPVS, false);
            });

            await pair.CleanReturnAsync();
        }
    }
}
