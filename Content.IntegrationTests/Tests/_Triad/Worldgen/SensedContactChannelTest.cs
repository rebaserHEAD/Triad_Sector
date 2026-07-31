// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.Server._Triad.Worldgen.Cells;
using Content.Shared._Triad.CCVar;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using ClientContacts = Content.Client._Triad.Worldgen.SensedContactsSystem;

namespace Content.IntegrationTests.Tests._Triad.Worldgen;

/// <summary>
///     Covers the radar contact channel end to end: the server's delta diffing, the wire, and
///     the client's per-console caches. Everything here drives a real connected pair, because
///     the failures this fixture exists to catch (a console going quiet, a console starving
///     another console) only exist in the interaction between the two halves.
///
///     Records are injected straight into <see cref="CellDescribeSystem.Records"/> rather than
///     grown by a describe pass. The describe pass is covered elsewhere, it takes hundreds of
///     ticks to produce anything, and it does not let a test say "the visible set is exactly
///     these two rocks and nothing changes for six seconds", which is the whole point here.
/// </summary>
[TestFixture]
public sealed class SensedContactChannelTest
{
    private const string ConsoleProto = "TriadContactTestConsole";

    [TestPrototypes]
    private const string Prototypes = $@"
- type: entity
  id: {ConsoleProto}
  components:
  - type: RadarConsole
";

    /// <summary>Ids well clear of anything <see cref="CellDescribeSystem"/> hands out in a test round.</summary>
    private const int FirstTestRecordId = 900_001;

    private static DebrisRecord MakeRecord(int id, EntityUid map, Vector2 point)
    {
        return new DebrisRecord
        {
            Id = id,
            Map = map,
            Point = point,
            Proto = "TriadContactTestRock",
            Seed = id,
            // A unit square is enough: nothing in the contact path reads the shape, only that
            // there is one. Records with a null hull are filtered out before they are sent.
            Hull = new[]
            {
                new Vector2i(0, 0),
                new Vector2i(1, 0),
                new Vector2i(1, 1),
                new Vector2i(0, 1),
            },
            DetectSignature = 1f,
            DetectBias = 0f,
            State = SensedState.Dormant,
        };
    }

    /// <summary>
    ///     A connected pair with a clean map, one console per requested slot, and the sensed tier
    ///     switched on. Returns the map plus the server-side console uids.
    /// </summary>
    private static async Task<(EntityUid Map, List<EntityUid> Consoles)> Setup(
        Content.IntegrationTests.Pair.TestPair pair, int consoleCount)
    {
        var server = pair.Server;
        var cfg = server.ResolveDependency<IConfigurationManager>();

        var map = await pair.CreateTestMap();
        var consoles = new List<EntityUid>();

        await server.WaitPost(() =>
        {
            cfg.SetCVar(TriadCCVars.WorldgenSensedEnabled, true);
            cfg.SetCVar(TriadCCVars.WorldgenDescribeRange, 3072f);

            for (var i = 0; i < consoleCount; i++)
            {
                // Spread them out so they are distinct entities; all well inside radar range of
                // the records, which sit at the origin of the same map.
                consoles.Add(server.EntMan.SpawnEntity(
                    ConsoleProto,
                    new MapCoordinates(new Vector2(i * 4f, 0f), map.MapId)));
            }
        });

        await pair.RunTicksSync(5);
        return (map.MapUid, consoles);
    }

    /// <summary>
    ///     Drives one client poll cycle: ask for contacts on every console, then run enough ticks
    ///     for the request to reach the server and the reply to land. The client gates requests on
    ///     its own 500ms throttle, so this ticks past that rather than assuming a request went out.
    /// </summary>
    private static async Task Poll(
        Content.IntegrationTests.Pair.TestPair pair, IReadOnlyList<EntityUid> clientConsoles, int ticks = 20)
    {
        var client = pair.Client;
        var cSys = client.System<ClientContacts>();

        await client.WaitPost(() =>
        {
            foreach (var console in clientConsoles)
                cSys.RequestContacts(console);
        });

        await pair.RunTicksSync(ticks);
    }

    /// <summary>
    ///     A console whose visible set never changes must keep being told so. The server used to
    ///     skip the reply when a poll produced no adds and no removes, and the client blanks a
    ///     console after its staleness window of silence, so a settled picture (parked, docked,
    ///     station-keeping) went dark while the rocks were still there.
    /// </summary>
    [Test]
    public async Task SettledPictureStaysLit()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
        var (server, client) = (pair.Server, pair.Client);

        var (map, consoles) = await Setup(pair, 1);
        var describe = server.System<CellDescribeSystem>();

        await server.WaitPost(() =>
        {
            var record = MakeRecord(FirstTestRecordId, map, Vector2.Zero);
            describe.Records[record.Id] = record;
        });

        var clientConsole = pair.ToClientUid(consoles[0]);
        var cSys = client.System<ClientContacts>();

        // Get the picture on screen first.
        await Poll(pair, new[] { clientConsole });
        await Poll(pair, new[] { clientConsole });

        Assert.That(cSys.GetContacts(clientConsole), Is.Not.Empty,
            "console never received the record it was in range of");

        // Now change nothing at all and keep polling well past the client's staleness window.
        // Six seconds of client time against a five second window, so a server that answers only
        // on change leaves this console dark.
        for (var i = 0; i < 12; i++)
            await Poll(pair, new[] { clientConsole }, ticks: 20);

        Assert.That(cSys.GetContacts(clientConsole), Is.Not.Empty,
            "console blanked on a settled picture: the empty delta is the keepalive, so every poll must be answered");

        await server.WaitPost(() => server.EntMan.DeleteEntity(map));
        await pair.CleanReturnAsync();
    }

    /// <summary>
    ///     Every open console has to get its own contacts. The request throttle used to be one
    ///     system-wide scalar against per-console caches, so whichever radar control ticked first
    ///     consumed the gate every cycle and any second console rendered nothing at all.
    /// </summary>
    [Test]
    public async Task EveryConsoleGetsItsOwnContacts()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
        var (server, client) = (pair.Server, pair.Client);

        var (map, consoles) = await Setup(pair, 2);
        var describe = server.System<CellDescribeSystem>();

        await server.WaitPost(() =>
        {
            var record = MakeRecord(FirstTestRecordId, map, Vector2.Zero);
            describe.Records[record.Id] = record;
        });

        var clientConsoles = consoles.Select(pair.ToClientUid).ToList();
        var cSys = client.System<ClientContacts>();

        // Both consoles ask on the same frame, every cycle, which is what a nav screen and a
        // gunnery screen open together actually do.
        for (var i = 0; i < 4; i++)
            await Poll(pair, clientConsoles);

        Assert.Multiple(() =>
        {
            for (var i = 0; i < clientConsoles.Count; i++)
            {
                Assert.That(cSys.GetContacts(clientConsoles[i]), Is.Not.Empty,
                    $"console {i} of {clientConsoles.Count} received nothing; the request gate is starving it");
            }
        });

        await server.WaitPost(() => server.EntMan.DeleteEntity(map));
        await pair.CleanReturnAsync();
    }

    /// <summary>
    ///     The channel is a delta, so it has to carry removals as well as additions. A record that
    ///     materializes stops being a contact (its grid draws itself from then on) and the console
    ///     has to be told to drop it.
    /// </summary>
    [Test]
    public async Task MaterializedRecordIsRemovedFromTheContactSet()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
        var (server, client) = (pair.Server, pair.Client);

        var (map, consoles) = await Setup(pair, 1);
        var describe = server.System<CellDescribeSystem>();

        DebrisRecord record = null!;
        await server.WaitPost(() =>
        {
            record = MakeRecord(FirstTestRecordId, map, Vector2.Zero);
            describe.Records[record.Id] = record;
        });

        var clientConsole = pair.ToClientUid(consoles[0]);
        var cSys = client.System<ClientContacts>();

        await Poll(pair, new[] { clientConsole });
        await Poll(pair, new[] { clientConsole });
        Assert.That(cSys.GetContacts(clientConsole), Is.Not.Empty, "record never arrived");

        // Materialization hands the contact off to the real grid.
        await server.WaitPost(() => record.State = SensedState.Materialized);

        await Poll(pair, new[] { clientConsole });
        await Poll(pair, new[] { clientConsole });

        Assert.That(cSys.GetContacts(clientConsole), Is.Empty,
            "materialized record still painting as a contact; the grid and the contact are now doubled");

        await server.WaitPost(() => server.EntMan.DeleteEntity(map));
        await pair.CleanReturnAsync();
    }
}
