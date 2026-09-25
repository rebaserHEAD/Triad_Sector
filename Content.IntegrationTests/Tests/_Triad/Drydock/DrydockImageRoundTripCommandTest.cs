#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Server._Triad.Drydock;
using Content.Server._Triad.Drydock.Loader;
using Robust.Shared.Console;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Player;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// <c>drydock_image_roundtrip</c>, run the way an admin runs it: the command the console host registered, given a
    /// capturing shell, on a small grid (a wall, a powered APC, a locker with a crowbar in it). A run stores the grid to
    /// the command's in-memory images, despawns it and loads it back in place, so the counts it prints have to match on
    /// both sides, the new grid has to sit where the old one did, and the old uid has to be gone. A grid with a player
    /// aboard has to be refused and left alone.
    /// </summary>
    [TestFixture]
    [TestOf(typeof(DrydockImageSystem))]
    public sealed class DrydockImageRoundTripCommandTest
    {
        private const string CommandName = "drydock_image_roundtrip";

        private sealed class CaptureShell : IConsoleShell
        {
            public readonly List<string> Lines = new();

            public CaptureShell(IConsoleHost host) => ConsoleHost = host;

            public IConsoleHost ConsoleHost { get; }
            public bool IsLocal => true;
            public bool IsServer => true;
            public ICommonSession? Player => null;

            public void ExecuteCommand(string command) { }
            public void RemoteExecuteCommand(string command) { }
            public void WriteLine(string text) => Lines.Add(text);
            public void WriteLine(FormattedMessage message) => Lines.Add(message.ToString());
            public void WriteError(string text) => Lines.Add($"ERROR: {text}");
            public void Clear() => Lines.Clear();
        }

        private static readonly Regex StoredLine = new(@"^Stored (\d+) entities .*?, (\d+) component rows, .* as image ([0-9a-f-]{36})\.$");
        private static readonly Regex LoadedLine = new(@"^Loaded (\d+) entities as grid (\S+) in ");

        [Test]
        public async Task AGridRoundTripsInPlaceAndAGridWithAPlayerIsRefused()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
            var server = pair.Server;
            var entMan = server.EntMan;
            var map = await pair.CreateTestMap();
            var host = server.ResolveDependency<IConsoleHost>();
            var playerMan = server.ResolveDependency<Robust.Server.Player.IPlayerManager>();
            var fidelity = server.System<DrydockFidelitySystem>();
            var xforms = server.System<SharedTransformSystem>();
            var containers = server.System<Robust.Shared.Containers.SharedContainerSystem>();

            var grid = map.Grid.Owner;
            EntityUid locker = default;
            var position = new Vector2(5.5f, 3f);
            var rotation = Angle.FromDegrees(90);
            await server.WaitPost(() =>
            {
                xforms.SetLocalPositionRotation(grid, position, rotation);
                // The locker gets a tile of its own: beside a wall's it is shoved off the grid, and the second run would
                // store a different ship.
                server.System<SharedMapSystem>().SetTile(grid, entMan.GetComponent<Robust.Shared.Map.Components.MapGridComponent>(grid), new Vector2i(1, 0), map.Tile.Tile);
                var wallTile = new EntityCoordinates(grid, 0.5f, 0.5f);
                var lockerTile = new EntityCoordinates(grid, 1.5f, 0.5f);
                entMan.SpawnEntity("WallSolid", wallTile);
                entMan.SpawnEntity("APCBasic", wallTile);
                locker = entMan.SpawnEntity("LockerSteel", lockerTile);
                var crowbar = entMan.SpawnEntity("Crowbar", lockerTile);

                // A mob is not savable: the walk leaves it out, and the command has to name it rather than count it.
                entMan.SpawnEntity("MobMoth", new EntityCoordinates(grid, 0.5f, 0.5f));
                Assert.That(containers.Insert(crowbar, containers.GetContainer(locker, "entity_storage")), Is.True, "The control: the crowbar has to go into the locker.");
            });

            Assert.That(host.AvailableCommands.ContainsKey(CommandName), Is.True, "The console host has to have registered the command.");

            // Run 1.
            var first = await Run(pair, host, entMan.GetNetEntity(grid).ToString());
            var (stored1, rows1, image1) = ParseStored(first);
            var (loaded1, newGrid1) = ParseLoaded(first, entMan);

            List<string> tree1 = new();
            EntityCoordinates coordinates1 = default;
            Angle rotation1 = default;
            await server.WaitPost(() =>
            {
                tree1 = fidelity.GridTreeList(newGrid1).Select(uid => entMan.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID ?? "(none)").ToList();
                coordinates1 = new EntityCoordinates(entMan.GetComponent<TransformComponent>(newGrid1).ParentUid, entMan.GetComponent<TransformComponent>(newGrid1).LocalPosition);
                rotation1 = entMan.GetComponent<TransformComponent>(newGrid1).LocalRotation;
            });

            bool oldExists = true, crowbarInLocker = false;
            await server.WaitPost(() =>
            {
                oldExists = entMan.EntityExists(grid);
                var lockers = fidelity.GridTreeList(newGrid1).Where(uid => entMan.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID == "LockerSteel").ToList();
                var crowbars = fidelity.GridTreeList(newGrid1).Where(uid => entMan.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID == "Crowbar").ToList();
                crowbarInLocker = lockers.Count == 1 && crowbars.Count == 1
                                  && containers.TryGetContainingContainer(crowbars[0], out var holder) && holder.Owner == lockers[0];
            });

            Assert.Multiple(() =>
            {
                Assert.That(stored1, Is.GreaterThan(0), "Something has to have been stored.");
                Assert.That(rows1, Is.GreaterThan(stored1), "Each entity stores at least a row or two.");
                Assert.That(loaded1, Is.EqualTo(stored1), "The load has to bring back as many entities as the store took.");
                Assert.That(oldExists, Is.False, "The old grid's uid has to be gone.");
                Assert.That(newGrid1, Is.Not.EqualTo(grid), "The grid that came back is a new entity.");
                Assert.That(coordinates1.Position, Is.EqualTo(position), "The new grid has to sit where the old one did.");
                Assert.That(rotation1.Degrees, Is.EqualTo(90).Within(0.001), "And face the way it did.");
                Assert.That(tree1, Does.Contain("WallSolid").And.Contain("APCBasic").And.Contain("LockerSteel").And.Contain("Crowbar"), "The wall, the APC, the locker and the crowbar have to come back.");
                Assert.That(crowbarInLocker, Is.True, "The crowbar has to be in the locker.");
                Assert.That(first.Any(line => line.StartsWith("ERROR:", StringComparison.Ordinal)), Is.False, $"No error line: {string.Join(" | ", first)}");
                Assert.That(first.Any(line => line.StartsWith("Left out because its prototype is not savable:", StringComparison.Ordinal) && line.Contains("MobMoth x1", StringComparison.Ordinal)), Is.True,
                    $"The command has to name what it leaves out by prototype: {string.Join(" | ", first)}");
            });

            // A second run, on the grid the first one made.
            await pair.RunTicksSync(5);
            var second = await Run(pair, host, entMan.GetNetEntity(newGrid1).ToString());
            // The command's own output, so a failure below can be read against it.
            foreach (var line in first.Concat(second))
                await TestContext.Out.WriteLineAsync($"[command] {line}");

            var (stored2, _, image2) = ParseStored(second);
            var (loaded2, newGrid2) = ParseLoaded(second, entMan);

            bool firstGone = true;
            await server.WaitPost(() => firstGone = !entMan.EntityExists(newGrid1));
            Assert.Multiple(() =>
            {
                Assert.That(stored2, Is.EqualTo(stored1), "A second run has to store the same number of entities.");
                Assert.That(loaded2, Is.EqualTo(stored2));
                Assert.That(firstGone, Is.True, "The first run's grid has to be gone after the second.");
                Assert.That(newGrid2, Is.Not.EqualTo(newGrid1));
            });

            // A retry of an image that is already loaded is refused, because a second load would duplicate the ship, and so is
            // an id the store does not hold.
            var duplicate = await Run(pair, host, "load", image2.ToString());
            var unknown = await Run(pair, host, "load", Guid.NewGuid().ToString());
            Assert.Multiple(() =>
            {
                Assert.That(duplicate.Any(line => line.StartsWith("ERROR: Refused:", StringComparison.Ordinal) && line.Contains("already loaded", StringComparison.Ordinal)), Is.True,
                    $"An image that is loaded has to refuse a second load: {string.Join(" | ", duplicate)}");
                Assert.That(unknown.Any(line => line.StartsWith("ERROR:", StringComparison.Ordinal)), Is.True, "An unknown image id has to be refused.");
            });
            // A grid with a player aboard is refused and left alone.
            var session = playerMan.Sessions.First();
            EntityUid occupant = default;
            await server.WaitPost(() =>
            {
                occupant = entMan.SpawnEntity(null, new EntityCoordinates(newGrid2, 0.5f, 0.5f));
                playerMan.SetAttachedEntity(session, occupant);
            });

            var refused = await Run(pair, host, entMan.GetNetEntity(newGrid2).ToString());
            bool stillThere = false, occupantThere = false;
            await server.WaitPost(() =>
            {
                stillThere = entMan.EntityExists(newGrid2);
                occupantThere = entMan.EntityExists(occupant);
                playerMan.SetAttachedEntity(session, null);
            });

            Assert.Multiple(() =>
            {
                Assert.That(refused.Any(line => line.StartsWith("ERROR: Refused:", StringComparison.Ordinal) && line.Contains("player-controlled", StringComparison.Ordinal)), Is.True,
                    $"A grid with a player aboard has to be refused: {string.Join(" | ", refused)}");
                Assert.That(stillThere, Is.True, "A refused grid has to be left where it is.");
                Assert.That(occupantThere, Is.True, "And so has the player's entity.");
            });

            await pair.CleanReturnAsync();
        }

        private static async Task<List<string>> Run(TestPair pair, IConsoleHost host, params string[] args)
        {
            var shell = new CaptureShell(host);
            await pair.Server.WaitPost(() => host.AvailableCommands[CommandName].Execute(shell, string.Join(' ', args), args));
            return shell.Lines;
        }

        private static (int Entities, int Rows, Guid Image) ParseStored(List<string> lines)
        {
            var match = lines.Select(line => StoredLine.Match(line)).FirstOrDefault(m => m.Success);
            Assert.That(match, Is.Not.Null, $"The command has to print its store line: {string.Join(" | ", lines)}");
            return (int.Parse(match!.Groups[1].Value), int.Parse(match.Groups[2].Value), Guid.Parse(match.Groups[3].Value));
        }

        private static (int Entities, EntityUid Grid) ParseLoaded(List<string> lines, IEntityManager entMan)
        {
            var match = lines.Select(line => LoadedLine.Match(line)).FirstOrDefault(m => m.Success);
            Assert.That(match, Is.Not.Null, $"The command has to print its load line: {string.Join(" | ", lines)}");
            Assert.That(NetEntity.TryParse(match!.Groups[2].Value, out var net), Is.True, "The load line has to name the grid.");
            return (int.Parse(match.Groups[1].Value), entMan.GetEntity(net));
        }
    }
}
