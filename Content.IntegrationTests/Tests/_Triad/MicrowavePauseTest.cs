using Content.IntegrationTests.Pair;
using Content.Server.Kitchen.Components;
using Content.Server.Kitchen.EntitySystems;
using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._Triad;

/// <summary>
/// A cooking microwave's malfunction deadline across a map pause. The deadline is an auto-paused field, so the engine
/// adds the pause residency to it at the unpause. A cook with nothing metal in it has no deadline and has to keep
/// none: a deadline made by the shift is already past, and the next update rolls a malfunction, which for a kitchen
/// microwave (explosion chance 1) destroys it.
/// </summary>
[TestOf(typeof(MicrowaveSystem))]
public sealed class MicrowavePauseTest
{
    private const string Microwave = "KitchenMicrowave";

    [Test]
    public async Task ACookWithNothingMetalKeepsNoDeadlineAcrossAPause()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var map = await pair.CreateTestMap();
        var second = TicksPerSecond(pair);
        var microwave = await SpawnPoweredMicrowave(pair, map.GridCoords);

        var cookingAtStart = false;
        TimeSpan? atStart = default;
        await server.WaitPost(() =>
        {
            Cook(entMan, microwave, "FoodMeat", map.GridCoords);
            cookingAtStart = entMan.TryGetComponent<ActiveMicrowaveComponent>(microwave, out var active);
            atStart = active?.MalfunctionTime;
            entMan.System<SharedMapSystem>().SetPaused(map.MapUid, true);
        });

        await pair.RunTicksSync(second);
        await server.WaitPost(() => entMan.System<SharedMapSystem>().SetPaused(map.MapUid, false));

        // The malfunction roll runs every update, so an overdue deadline has been rolled well before this.
        await pair.RunTicksSync(second);

        var cookingAfter = false;
        var broken = true;
        TimeSpan? after = default;
        await server.WaitPost(() =>
        {
            cookingAfter = entMan.TryGetComponent<ActiveMicrowaveComponent>(microwave, out var active);
            after = active?.MalfunctionTime;
            broken = entMan.GetComponent<MicrowaveComponent>(microwave).Broken;
        });

        Assert.Multiple(() =>
        {
            Assert.That(after, Is.EqualTo(atStart), "The deadline has to be what the cook start left it, unshifted and unrolled.");
            Assert.That(after, Is.Null, "And that is no deadline at all.");

            Assert.That(cookingAtStart, Is.True, "The control: the microwave has to start cooking.");
            Assert.That(cookingAfter, Is.True, "The control: it has to still be cooking after the pause.");
            Assert.That(broken, Is.False, "The control: it has to be whole.");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AMalfunctioningCooksDeadlineMovesByThePauseResidency()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var timing = server.ResolveDependency<IGameTiming>();
        var map = await pair.CreateTestMap();
        var second = TicksPerSecond(pair);
        var microwave = await SpawnPoweredMicrowave(pair, map.GridCoords);

        TimeSpan? atPause = default;
        var pausedAt = TimeSpan.Zero;
        var interval = 0f;
        await server.WaitPost(() =>
        {
            Cook(entMan, microwave, "SheetSteel1", map.GridCoords);
            atPause = entMan.GetComponentOrNull<ActiveMicrowaveComponent>(microwave)?.MalfunctionTime;
            interval = entMan.GetComponent<MicrowaveComponent>(microwave).MalfunctionInterval;

            // Paused in the cook start's own tick, so no update has rolled the deadline before it is read.
            pausedAt = timing.CurTime;
            entMan.System<SharedMapSystem>().SetPaused(map.MapUid, true);
        });

        await pair.RunTicksSync(second);

        TimeSpan? atUnpause = default;
        var unpausedAt = TimeSpan.Zero;
        await server.WaitPost(() =>
        {
            unpausedAt = timing.CurTime;
            entMan.System<SharedMapSystem>().SetPaused(map.MapUid, false);
            atUnpause = entMan.GetComponentOrNull<ActiveMicrowaveComponent>(microwave)?.MalfunctionTime;
        });

        Assert.Multiple(() =>
        {
            Assert.That(atPause, Is.EqualTo(pausedAt + TimeSpan.FromSeconds(interval)),
                "The control: a cook with metal in it starts with a deadline one interval out.");
            Assert.That(unpausedAt, Is.GreaterThan(pausedAt), "The control: the pause has to last.");

            Assert.That(atUnpause, Is.EqualTo(atPause + (unpausedAt - pausedAt)),
                "The deadline has to move by exactly the time the map was paused.");
        });

        await pair.CleanReturnAsync();
    }

    private static int TicksPerSecond(TestPair pair) =>
        (int) Math.Ceiling(1 / pair.Server.ResolveDependency<IGameTiming>().TickPeriod.TotalSeconds);

    /// <summary>
    /// A microwave that needs no power, left a second for the power net to mark it powered, with its malfunction's
    /// explosion and lightning turned off: a roll shows only as the deadline it writes.
    /// </summary>
    private static async Task<EntityUid> SpawnPoweredMicrowave(TestPair pair, EntityCoordinates at)
    {
        var server = pair.Server;
        var entMan = server.EntMan;
        EntityUid microwave = default;
        await server.WaitPost(() =>
        {
            microwave = entMan.SpawnEntity(Microwave, at);
            entMan.System<PowerReceiverSystem>().SetNeedsPower(microwave, false);

            var comp = entMan.GetComponent<MicrowaveComponent>(microwave);
            comp.ExplosionChance = 0;
            comp.LightningChance = 0;
            comp.CurrentCookTimerTime = 60;
        });

        await pair.RunTicksSync(TicksPerSecond(pair));

        var powered = false;
        await server.WaitPost(() => powered = entMan.GetComponent<ApcPowerReceiverComponent>(microwave).Powered);
        Assert.That(powered, Is.True, "The control: the microwave has to be powered to cook.");

        return microwave;
    }

    private static void Cook(IEntityManager entMan, EntityUid microwave, string item, EntityCoordinates at)
    {
        var comp = entMan.GetComponent<MicrowaveComponent>(microwave);
        entMan.System<SharedContainerSystem>().Insert(entMan.SpawnEntity(item, at), comp.Storage);
        entMan.System<MicrowaveSystem>().Wzhzhzh(microwave, comp, null);
    }
}
