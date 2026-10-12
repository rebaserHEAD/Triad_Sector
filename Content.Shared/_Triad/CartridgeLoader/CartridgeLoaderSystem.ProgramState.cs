using Content.Shared._Triad.CartridgeLoader;
using Robust.Shared.GameStates;

namespace Content.Shared.CartridgeLoader;

// The loader's ui state slot holds one value for the whole loader, so a program never writes there: it publishes onto
// its own entity (CartridgeUiStateComponent) and the slot is left to the ui's owner.
public sealed partial class CartridgeLoaderSystem
{
    private void InitializeProgramState()
    {
        // BoundUIOpenedEvent on the loader belongs to NanoChatCartridgeSystem, and a (component, event) pair takes one
        // subscriber, so the resend on open is called from PdaSystem.OnPdaOpen.
        SubscribeLocalEvent<CartridgeUiStateComponent, ComponentGetStateAttemptEvent>(OnProgramStateGetAttempt);
    }

    /// <summary>
    /// Publishes a program's ui state. The state object is kept and sent again later
    /// (<see cref="ResendActiveProgramState"/>), serialized as it stands at that moment.
    /// </summary>
    /// <remarks>
    /// Only the server publishes. A client predicting a program's message keeps its fragment as it is until the
    /// server's state arrives.
    /// </remarks>
    /// <param name="program">The program or cartridge entity publishing. It must be on a loader.</param>
    /// <param name="state">The programs ui state. Programs should use their own ui state class inheriting from <see cref="BoundUserInterfaceState"/></param>
    public void UpdateCartridgeUiState(Entity<CartridgeComponent?> program, BoundUserInterfaceState state)
    {
        if (_net.IsClient)
            return;

        if (TerminatingOrDeleted(program) || !Resolve(program, ref program.Comp))
            return;

        if (program.Comp.LoaderUid is null)
            return;

        var stored = EnsureComp<CartridgeUiStateComponent>(program);
        stored.State = state;
        Dirty(program, stored);
    }

    /// <summary>
    /// Drops a program's published state, which returns its fragment to the empty view.
    /// </summary>
    public void ClearCartridgeUiState(EntityUid program)
    {
        if (_net.IsClient)
            return;

        if (TerminatingOrDeleted(program) || !TryComp(program, out CartridgeUiStateComponent? stored) || stored.State is null)
            return;

        stored.State = null;
        Dirty(program, stored);
    }

    /// <summary>
    /// Sends the active program's stored state again. It reaches only players with the ui open
    /// (<see cref="OnProgramStateGetAttempt"/>), so whoever just opened it has missed what was published while it was shut.
    /// </summary>
    public void ResendActiveProgramState(Entity<CartridgeLoaderComponent?> loader)
    {
        if (_net.IsClient || !Resolve(loader, ref loader.Comp, false))
            return;

        if (loader.Comp.ActiveProgram is { } active && TryComp(active, out CartridgeUiStateComponent? stored))
            Dirty(active, stored);
    }

    /// <summary>
    /// Asks the active program to publish again, for when something it reads off the loader changed.
    /// </summary>
    public void RefreshActiveProgram(Entity<CartridgeLoaderComponent?> loader)
    {
        if (_net.IsClient || !Resolve(loader, ref loader.Comp, false))
            return;

        if (loader.Comp.ActiveProgram is not { } active || !_userInterface.IsUiOpen(loader.Owner, loader.Comp.UiKey))
            return;

        var ev = new CartridgeUiReadyEvent((loader.Owner, loader.Comp));
        RaiseLocalEvent(active, ref ev);
    }

    /// <summary>
    /// Whether somebody is looking at this program right now: it is its loader's active program and the loader's ui is open.
    /// </summary>
    public bool IsProgramShowing(Entity<CartridgeComponent?> program)
    {
        if (!Resolve(program, ref program.Comp, false) || program.Comp.LoaderUid is not { } loaderUid)
            return false;

        return TryComp(loaderUid, out CartridgeLoaderComponent? loader)
               && loader.ActiveProgram == program.Owner
               && _userInterface.IsUiOpen(loaderUid, loader.UiKey);
    }

    /// <summary>
    /// A program's state goes only to players who have its loader's ui open. This runs inside PVS jobs, so it only reads.
    /// </summary>
    private void OnProgramStateGetAttempt(Entity<CartridgeUiStateComponent> ent, ref ComponentGetStateAttemptEvent args)
    {
        // A cleared state goes to everyone: it tells nothing, and the client that held the old one has to drop it
        // even though the cartridge is off its loader by now.
        if (ent.Comp.State is null)
            return;

        args.Cancelled = args.Player?.AttachedEntity is not { } viewer
                         || !TryComp(ent, out CartridgeComponent? cartridge)
                         || cartridge.LoaderUid is not { } loaderUid
                         || !TryComp(loaderUid, out CartridgeLoaderComponent? loader)
                         || !_userInterface.IsUiOpen(loaderUid, loader.UiKey, viewer);
    }
}

/// <summary>
/// Raised on a loader when its ui state is due. The entity that owns the ui handles it by publishing its own state,
/// which carries the loader's (the PDA's <c>PdaUpdateState</c>). Unhandled, the loader publishes a bare
/// <see cref="CartridgeLoaderUiState"/>.
/// </summary>
[ByRefEvent]
public record struct CartridgeLoaderUiStateUpdateEvent(bool Handled = false);
