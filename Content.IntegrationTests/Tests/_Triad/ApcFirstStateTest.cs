using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Shared.APC;
using Robust.Shared.GameObjects;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._Triad;

/// <summary>
/// An APC's first charge state waits for its battery's first sync. Startup defers the state update to the next tick,
/// but the power net syncs its batteries in a batch every 0.5 s, and until the first batch the network battery reads
/// 0 of 0, which computes as Lack. Nothing recomputes a full battery whose charge does not change, so an APC spawned
/// or loaded full read Lack until someone opened it.
/// </summary>
[TestOf(typeof(ApcSystem))]
public sealed class ApcFirstStateTest
{
    [Test]
    public async Task AFreshApcReadsItsBatteryOnceItHasSynced()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var map = await pair.CreateTestMap();
        var second = (int) Math.Ceiling(1 / server.ResolveDependency<IGameTiming>().TickPeriod.TotalSeconds);

        // The empty one settles first, so its battery can show where the power net's batch falls.
        EntityUid empty = default;
        await server.WaitPost(() => empty = entMan.SpawnEntity("APCConstructed", map.GridCoords));
        await pair.RunTicksSync(second);

        // Zero its network battery and tick until the batch writes it back (BatterySystem.cs:81). The full one then
        // spawns straight after a batch, so its first tick comes before its battery's first sync.
        var batch = false;
        for (var i = 0; i < second && !batch; i++)
        {
            await server.WaitPost(() => entMan.GetComponent<PowerNetworkBatteryComponent>(empty).NetworkBattery.Capacity = 0);
            await pair.RunTicksSync(1);
            await server.WaitPost(() => batch = entMan.GetComponent<PowerNetworkBatteryComponent>(empty).NetworkBattery.Capacity > 0);
        }

        EntityUid full = default;
        await server.WaitPost(() => full = entMan.SpawnEntity("APCBasic", map.GridCoords));
        await pair.RunTicksSync(1);

        var syncedAtFirstTick = true;
        await server.WaitPost(() => syncedAtFirstTick = entMan.GetComponent<PowerNetworkBatteryComponent>(full).NetworkBattery.Capacity > 0);
        await pair.RunTicksSync(second);

        (ApcChargeState State, bool Pending, float Charge, float Max) fullApc = default, emptyApc = default;
        await server.WaitPost(() =>
        {
            (ApcChargeState, bool, float, float) Read(EntityUid uid)
            {
                var apc = entMan.GetComponent<ApcComponent>(uid);
                var battery = entMan.GetComponent<BatteryComponent>(uid);
                return (apc.LastChargeState, apc.NeedStateUpdate, battery.CurrentCharge, battery.MaxCharge);
            }

            fullApc = Read(full);
            emptyApc = Read(empty);
        });

        Assert.Multiple(() =>
        {
            Assert.That(batch, Is.True, "The control: the power net has to run a batch within a second.");
            Assert.That(syncedAtFirstTick, Is.False, "The control: the full APC's first tick has to come before its battery's first sync.");
            Assert.That(fullApc.Charge, Is.EqualTo(fullApc.Max).And.GreaterThan(0), "The control: the full APC's battery has to be full.");
            Assert.That(emptyApc.Charge, Is.Zero, "The control: the empty APC's battery has to be empty.");

            Assert.That(fullApc.Pending, Is.False, "The full APC's deferred update has to have run.");
            Assert.That(fullApc.State, Is.EqualTo(ApcChargeState.Full), "And it has to read Full, not the Lack of a battery not yet synced.");
            Assert.That(emptyApc.Pending, Is.False, "The empty APC's deferred update has to have run.");
            Assert.That(emptyApc.State, Is.EqualTo(ApcChargeState.Lack), "And it has to read Lack.");
        });

        await pair.CleanReturnAsync();
    }
}
