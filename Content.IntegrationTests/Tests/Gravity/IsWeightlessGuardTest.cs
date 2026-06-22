using Content.Shared.Gravity;
using Robust.Shared.GameObjects;
using Robust.Shared.Map.Components;

namespace Content.IntegrationTests.Tests.Gravity
{
    // Triad: pins the IsWeightless inventory-less guard (SharedGravitySystem.cs).
    // The guard short-circuits the IsWeightlessEvent raise for entities lacking
    // inventory/magboots/moonboots and must stay behavior-equivalent to the raise path:
    // an inventory-less mob is weightless iff its grid/map lacks enabled gravity.
    [TestFixture]
    [TestOf(typeof(SharedGravitySystem))]
    public sealed class IsWeightlessGuardTest
    {
        [TestPrototypes]
        private const string Prototypes = @"
# Inventory-less mob: takes the guard fast-path.
- type: entity
  name: WeightlessGuardCarpDummy
  id: WeightlessGuardCarpDummy
  components:
  - type: Physics
    bodyType: Dynamic

# Has inventory: takes the original event-raise path (control).
- type: entity
  name: WeightlessGuardCrewDummy
  id: WeightlessGuardCrewDummy
  components:
  - type: Inventory
  - type: Physics
    bodyType: Dynamic

# A grid that supports gravity, gravity enabled.
- type: entity
  name: WeightlessGuardGravityGridDummy
  id: WeightlessGuardGravityGridDummy
  components:
  - type: Gravity
    enabled: true
";

        [Test]
        public async Task GuardMatchesGravityState()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;

            var entMan = server.ResolveDependency<IEntityManager>();
            var gravity = server.System<SharedGravitySystem>();

            var testMap = await pair.CreateTestMap();

            EntityUid carp = default;
            EntityUid crew = default;

            await server.WaitAssertion(() =>
            {
                // No gravity on a bare test map: both should read weightless.
                carp = entMan.SpawnEntity("WeightlessGuardCarpDummy", testMap.GridCoords);
                crew = entMan.SpawnEntity("WeightlessGuardCrewDummy", testMap.GridCoords);

                Assert.Multiple(() =>
                {
                    // Guard fast-path (no inventory) and event path (inventory) agree: weightless.
                    Assert.That(gravity.IsWeightless(carp), Is.True, "inventory-less mob should be weightless with no gravity");
                    Assert.That(gravity.IsWeightless(crew), Is.True, "crewmember should be weightless with no gravity");
                });

                // Turn the test map's grid into a gravity-supporting, gravity-enabled grid.
                var gridUid = entMan.GetComponent<TransformComponent>(carp).GridUid ?? testMap.Grid.Owner;
                var grav = entMan.EnsureComponent<GravityComponent>(gridUid);
                grav.EnabledVV = true;
            });

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                Assert.Multiple(() =>
                {
                    // Both paths must flip to not-weightless once the grid has gravity.
                    Assert.That(gravity.IsWeightless(carp), Is.False, "inventory-less mob should not be weightless under gravity");
                    Assert.That(gravity.IsWeightless(crew), Is.False, "crewmember should not be weightless under gravity");
                });
            });

            await pair.CleanReturnAsync();
        }
    }
}
