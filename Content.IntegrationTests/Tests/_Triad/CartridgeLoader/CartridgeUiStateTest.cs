using System.Collections.Generic;
using Content.Server.CartridgeLoader.Cartridges;
using Content.Server.PDA;
using Content.Shared._Triad.CartridgeLoader;
using Content.Shared.CartridgeLoader;
using Content.Shared.CartridgeLoader.Cartridges;
using Content.Shared.Containers;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.EntityTable.EntitySelectors;
using Content.Shared.Interaction;
using Content.Shared.PDA;
using Robust.Server.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Triad.CartridgeLoader;

/// <summary>
/// The fork's rules on top of the shared cartridge loader: a program publishes its ui state onto its own entity and
/// the loader's BUI state slot stays with the PDA shell, and Frontier's self-installing cartridges still install.
/// </summary>
[TestFixture]
[TestOf(typeof(CartridgeLoaderSystem))]
public sealed class CartridgeUiStateTest
{
    private const string Pda = "PassengerPDA";
    private const string ManyProgramPda = "NFAdminPDA";
    private const string LooseCartridge = "TriadTestLooseCartridge";
    private const string SelfInstallingCartridge = "TriadTestSelfInstallingCartridge";

    // The stock fork cartridges install and delete themselves on insert (autoInstall, disposable), so the tests that
    // need a cartridge sitting in the slot use one that does neither.
    [TestPrototypes]
    private const string Prototypes = $@"
- type: entity
  parent: BaseItem
  id: {LooseCartridge}
  components:
  - type: Cartridge
    programName: default-program-name

- type: entity
  parent: BaseItem
  id: {SelfInstallingCartridge}
  components:
  - type: Cartridge
    programName: default-program-name
    autoInstall: true
    disposable: true
    readonly: true
";

    [Test]
    public async Task ProgramsPublishingInOneTickEachKeepTheirState()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var loader = entMan.System<CartridgeLoaderSystem>();
        var pdaSystem = entMan.System<PdaSystem>();
        var ui = entMan.System<UserInterfaceSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var pda = entMan.SpawnEntity(Pda, map.MapCoords);
            var programs = loader.GetPreinstalledPrograms(pda);
            Assert.That(programs, Has.Count.GreaterThanOrEqualTo(2), $"{Pda} preinstalls at least two programs");

            var first = programs[0];
            var second = programs[1];
            var firstState = new NotekeeperUiState(new List<string> { "first" });
            var secondState = new NetProbeUiState(new List<ProbedNetworkDevice>());

            // The shell and two programs, all written inside one tick.
            pdaSystem.UpdatePdaUi(pda);
            ui.TryGetUiState<BoundUserInterfaceState>(pda, PdaUiKey.Key, out var shell);

            loader.UpdateCartridgeUiState(first, firstState);
            loader.UpdateCartridgeUiState(second, secondState);
            ui.TryGetUiState<BoundUserInterfaceState>(pda, PdaUiKey.Key, out var afterPrograms);

            // Then the loader's own update, which the PDA answers with a fresh shell state.
            loader.UpdateUiState(pda);
            ui.TryGetUiState<BoundUserInterfaceState>(pda, PdaUiKey.Key, out var afterLoader);

            Assert.Multiple(() =>
            {
                Assert.That(entMan.GetComponent<CartridgeUiStateComponent>(first).State, Is.SameAs(firstState));
                Assert.That(entMan.GetComponent<CartridgeUiStateComponent>(second).State, Is.SameAs(secondState));
                Assert.That(shell, Is.InstanceOf<PdaUpdateState>());
                Assert.That(afterPrograms, Is.SameAs(shell), "a program publishing leaves the PDA's BUI slot alone");
                Assert.That(afterLoader, Is.InstanceOf<PdaUpdateState>(), "a loader update puts a shell state in the slot, not a bare loader state");
            });

            entMan.DeleteEntity(pda);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task StateIsClearedWhenTheCartridgeLeavesItsLoader()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var loader = entMan.System<CartridgeLoaderSystem>();
        var slots = entMan.System<ItemSlotsSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var pda = entMan.SpawnEntity(Pda, map.MapCoords);
            var cartridge = entMan.SpawnEntity(LooseCartridge, map.MapCoords);
            var loaderComp = entMan.GetComponent<CartridgeLoaderComponent>(pda);

            Assert.That(slots.TryInsert(pda, loaderComp.CartridgeSlot, cartridge, null), Is.True);

            loader.UpdateCartridgeUiState(cartridge, new NetProbeUiState(new List<ProbedNetworkDevice>()));
            Assert.That(entMan.GetComponent<CartridgeUiStateComponent>(cartridge).State, Is.Not.Null);

            Assert.That(slots.TryEject(pda, loaderComp.CartridgeSlot, null, out _), Is.True);
            Assert.That(entMan.GetComponent<CartridgeUiStateComponent>(cartridge).State, Is.Null,
                "a state computed for one PDA does not travel with the cartridge to the next");

            entMan.DeleteEntity(cartridge);
            entMan.DeleteEntity(pda);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task CartridgeOffALoaderHoldsNoState()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var loader = entMan.System<CartridgeLoaderSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var cartridge = entMan.SpawnEntity(LooseCartridge, map.MapCoords);

            loader.UpdateCartridgeUiState(cartridge, new NetProbeUiState(new List<ProbedNetworkDevice>()));
            Assert.That(entMan.HasComponent<CartridgeUiStateComponent>(cartridge), Is.False);

            entMan.DeleteEntity(cartridge);
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Frontier's cartridge flags: autoInstall installs on insert, readonly makes the program one that cannot be
    /// uninstalled, disposable uses the cartridge up.
    /// </summary>
    [Test]
    public async Task SelfInstallingCartridgeInstallsReadonlyAndIsUsedUp()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var loader = entMan.System<CartridgeLoaderSystem>();
        var slots = entMan.System<ItemSlotsSystem>();
        var map = await pair.CreateTestMap();

        EntityUid pda = default;
        EntityUid cartridge = default;

        await server.WaitAssertion(() =>
        {
            pda = entMan.SpawnEntity(Pda, map.MapCoords);
            cartridge = entMan.SpawnEntity(SelfInstallingCartridge, map.MapCoords);
            var loaderComp = entMan.GetComponent<CartridgeLoaderComponent>(pda);
            var before = loader.GetPreinstalledPrograms(pda).Count;

            Assert.That(slots.TryInsert(pda, loaderComp.CartridgeSlot, cartridge, null), Is.True);

            var unremovable = loader.GetPreinstalledPrograms(pda);
            Assert.That(unremovable, Has.Count.EqualTo(before + 1), "a readonly cartridge installs among the programs that cannot be uninstalled");
            Assert.That(entMan.GetComponent<CartridgeComponent>(unremovable[^1]).InstallationStatus, Is.EqualTo(InstallationStatus.Readonly));
            Assert.That(loader.GetRemovablePrograms(pda), Is.Empty);
        });

        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.Deleted(cartridge), Is.True, "a disposable cartridge is used up by installing it");
            entMan.DeleteEntity(pda);
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// The uid in a loader ui message comes from the client. Install acts only on the cartridge in this loader's
    /// slot, and Uninstall only on a program that can be uninstalled.
    /// </summary>
    [Test]
    public async Task UiMessagesActOnlyOnWhatTheLoaderAllows()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var loader = entMan.System<CartridgeLoaderSystem>();
        var slots = entMan.System<ItemSlotsSystem>();
        var map = await pair.CreateTestMap();

        EntityUid pda = default;
        EntityUid stray = default;
        EntityUid slotted = default;
        var preinstalled = 0;

        void Send(EntityUid target, CartridgeUiMessageAction action)
        {
            // Raised the way the engine raises a BUI message: directed at the ui's owner, typed by its runtime type.
            object message = new CartridgeLoaderUiMessage(entMan.GetNetEntity(target), action) { Actor = pda };
            entMan.EventBus.RaiseLocalEvent(pda, message);
        }

        await server.WaitAssertion(() =>
        {
            pda = entMan.SpawnEntity(Pda, map.MapCoords);
            stray = entMan.SpawnEntity(LooseCartridge, map.MapCoords);
            preinstalled = loader.GetPreinstalledPrograms(pda).Count;

            // A preinstalled program, and a cartridge that is not in this PDA.
            Send(loader.GetPreinstalledPrograms(pda)[0], CartridgeUiMessageAction.Uninstall);
            Send(stray, CartridgeUiMessageAction.Install);
        });

        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(loader.GetPreinstalledPrograms(pda), Has.Count.EqualTo(preinstalled), "a preinstalled program is not uninstalled");
                Assert.That(loader.GetRemovablePrograms(pda), Is.Empty, "a cartridge outside the slot is not installed");
                Assert.That(entMan.Deleted(stray), Is.False);
            });

            // The same two messages for what the loader does allow, so the refusals above are not the message going nowhere.
            slotted = entMan.SpawnEntity(LooseCartridge, map.MapCoords);
            var loaderComp = entMan.GetComponent<CartridgeLoaderComponent>(pda);
            Assert.That(slots.TryInsert(pda, loaderComp.CartridgeSlot, slotted, null), Is.True);

            Send(slotted, CartridgeUiMessageAction.Install);
            Assert.That(loader.GetRemovablePrograms(pda), Has.Count.EqualTo(1));

            Send(loader.GetRemovablePrograms(pda)[0], CartridgeUiMessageAction.Uninstall);
        });

        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            Assert.That(loader.GetRemovablePrograms(pda), Is.Empty);

            entMan.DeleteEntity(stray);
            entMan.DeleteEntity(pda);
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// A click with the PDA is relayed to the open program only. The admin PDA carries several programs that act on
    /// a click.
    /// </summary>
    [Test]
    public async Task AClickReachesOnlyTheOpenProgram()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var loader = entMan.System<CartridgeLoaderSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var pda = entMan.SpawnEntity(ManyProgramPda, map.MapCoords);
            // Another PDA: it is a network device for NetProbe, and anything has a price for Appraisal.
            var target = entMan.SpawnEntity(Pda, map.MapCoords);
            var loaderComp = entMan.GetComponent<CartridgeLoaderComponent>(pda);

            var netProbe = loader.TryGetProgram<NetProbeCartridgeComponent>(pda);
            var appraisal = loader.TryGetProgram<AppraisalCartridgeComponent>(pda);
            Assert.That(netProbe, Is.Not.Null, $"{ManyProgramPda} carries NetProbe");
            Assert.That(appraisal, Is.Not.Null, $"{ManyProgramPda} carries Appraisal");

            loader.ActivateProgram((pda, loaderComp), netProbe.Value);

            var click = new AfterInteractEvent(target, pda, target, entMan.GetComponent<TransformComponent>(target).Coordinates, true);
            entMan.EventBus.RaiseLocalEvent(pda, click);

            var probed = netProbe.Value.Comp.ProbedDevices;
            var appraised = appraisal.Value.Comp.AppraisedItems;
            Assert.Multiple(() =>
            {
                Assert.That(probed, Has.Count.EqualTo(1), "the open program takes the click");
                Assert.That(appraised, Is.Empty, "a program that is installed but not open does not");
            });

            entMan.DeleteEntity(target);
            entMan.DeleteEntity(pda);
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Every loader holds as many preinstalled programs as its prototype's fill names.
    /// </summary>
    [Test]
    public async Task EveryLoaderHoldsItsWholeFill()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var protoMan = server.ResolveDependency<IPrototypeManager>();
        var compFactory = server.ResolveDependency<IComponentFactory>();
        var loader = entMan.System<CartridgeLoaderSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                foreach (var proto in protoMan.EnumeratePrototypes<EntityPrototype>())
                {
                    if (proto.Abstract || pair.IsTestPrototype(proto))
                        continue;

                    if (!proto.TryComp<CartridgeLoaderComponent>(out _, compFactory))
                        continue;

                    if (!proto.TryComp<EntityTableContainerFillComponent>(out var fill, compFactory))
                        continue;

                    var containers = fill.Containers;
                    if (!containers.TryGetValue(CartridgeLoaderComponent.UnremovableContainerId, out var selector)
                        || selector is not AllSelector all)
                    {
                        continue;
                    }

                    var uid = entMan.SpawnEntity(proto.ID, map.MapCoords);
                    var installed = loader.GetPreinstalledPrograms(uid).Count;

                    Assert.That(installed, Is.EqualTo(all.Children.Count),
                        $"{proto.ID} fills {all.Children.Count} preinstalled programs and holds {installed}");

                    entMan.DeleteEntity(uid);
                }
            });
        });

        await pair.CleanReturnAsync();
    }
}
