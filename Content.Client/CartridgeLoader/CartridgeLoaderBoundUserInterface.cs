using Content.Client._Triad.CartridgeLoader; // Triad
using Content.Client.UserInterface.Fragments;
using Content.Shared._Triad.CartridgeLoader; // Triad
using Content.Shared.CartridgeLoader;
using Robust.Client.UserInterface;

namespace Content.Client.CartridgeLoader;


public abstract class CartridgeLoaderBoundUserInterface : BoundUserInterface
{
    [ViewVariables]
    private EntityUid? _activeProgram;

    [ViewVariables]
    private UIFragment? _activeCartridgeUI;

    [ViewVariables]
    private Control? _activeUiFragment;

    // Triad: the last state handed to the current fragment, so the same state object is not fed to it twice
    private BoundUserInterfaceState? _lastFedState;

    // Triad: what the program list last showed, null before the first sync
    private List<(EntityUid Program, InstallationStatus Status)>? _shownPrograms;
    private readonly List<(EntityUid, CartridgeComponent)> _programScratch = new();
    private readonly CartridgeLoaderSystem _loader;

    private IEntityManager _entManager;

    protected CartridgeLoaderBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
        _entManager = IoCManager.Resolve<IEntityManager>();
        _loader = EntMan.System<CartridgeLoaderSystem>(); // Triad
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);

        if (state is not CartridgeLoaderUiState) // Triad: no pattern variable, nothing is read from the state
        {
            // Triad: removed, the slot carries loader states only and a program's state arrives through FeedCartridge
            // _activeCartridgeUI?.UpdateState(state);
            _loader.Log.Error(
                $"{state.GetType().Name} reached the state slot of the cartridge loader on {EntMan.ToPrettyString(Owner)}. Programs publish through CartridgeLoaderSystem.UpdateCartridgeUiState.");
            // End Triad
            return;
        }

        // Triad begin: nothing is read from this state. The program list and the open program follow what replicates
        // on the loader and its programs, once a frame (SyncWithLoader). A predicting client is handed this state
        // several times a tick, the server's and then its own prediction again, and nothing is rebuilt for each.
        // // TODO move this to a component state and ensure the net ids.
        // var programs = GetCartridgeComponents(_entManager.GetEntityList(loaderUiState.Programs));
        // UpdateAvailablePrograms(programs);
        //
        // var activeUI = _entManager.GetEntity(loaderUiState.ActiveUI);
        //
        // _activeProgram = activeUI;
        //
        // var ui = RetrieveCartridgeUI(activeUI);
        // var comp = RetrieveCartridgeComponent(activeUI);
        // var control = ui?.GetUIFragmentRoot();
        //
        // //Prevent the same UI fragment from getting disposed and attached multiple times
        // if (_activeUiFragment?.GetType() == control?.GetType())
        //     return;
        //
        // if (_activeUiFragment is not null)
        //     DetachCartridgeUI(_activeUiFragment);
        //
        // if (control is not null && _activeProgram.HasValue)
        // {
        //     AttachCartridgeUI(control, Loc.GetString(comp?.ProgramName ?? "default-program-name"));
        //     SendCartridgeUiReadyEvent(_activeProgram.Value);
        // }
        //
        // _activeCartridgeUI = ui;
        // _activeUiFragment?.Dispose();
        // _activeUiFragment = control;
        // Triad end
    }

    // Triad begin
    protected override void Open()
    {
        base.Open();

        EntMan.System<CartridgeUiStateSystem>().Track(this);
    }

    /// <summary>
    /// Brings the program list and the fragment in line with the loader's replicated components. Called once a frame
    /// while the ui is open.
    /// </summary>
    public void SyncWithLoader()
    {
        if (!IsOpened || !EntMan.TryGetComponent(Owner, out CartridgeLoaderComponent? loader))
            return;

        SyncPrograms();

        var active = loader.ActiveProgram;

        // Program identity decides, so two programs sharing a fragment type still swap. A fragment that could not be
        // built, because its program has not replicated yet, is tried again next frame.
        if (_activeProgram == active && (_activeUiFragment is not null || active is null))
            return;

        _activeProgram = active;
        BuildActiveFragment(announce: true);
    }

    private void SyncPrograms()
    {
        _programScratch.Clear();
        foreach (var program in _loader.GetAllPrograms(Owner))
        {
            // A program whose component has not replicated yet joins the list on a later frame.
            if (RetrieveCartridgeComponent(program) is { } cartridge)
                _programScratch.Add((program, cartridge));
        }

        if (_shownPrograms is not null && _shownPrograms.Count == _programScratch.Count)
        {
            var same = true;
            for (var i = 0; i < _shownPrograms.Count && same; i++)
            {
                var (program, cartridge) = _programScratch[i];
                same = _shownPrograms[i] == (program, cartridge.InstallationStatus);
            }

            if (same)
                return;
        }

        _shownPrograms ??= new();
        _shownPrograms.Clear();
        foreach (var (program, cartridge) in _programScratch)
        {
            _shownPrograms.Add((program, cartridge.InstallationStatus));
        }

        UpdateAvailablePrograms(_programScratch);
    }

    /// <summary>
    /// Builds the active program's fragment and feeds it the state that program last published.
    /// </summary>
    /// <param name="announce">Whether to tell the server the fragment is ready, which asks the program to publish.</param>
    private void BuildActiveFragment(bool announce)
    {
        var ui = RetrieveCartridgeUI(_activeProgram);
        var comp = RetrieveCartridgeComponent(_activeProgram);
        var control = ui?.GetUIFragmentRoot();

        var previousFragment = _activeUiFragment;
        if (previousFragment is not null)
            DetachCartridgeUI(previousFragment);

        // Set before the ready message goes out, so its answer has somewhere to land.
        _activeCartridgeUI = ui;
        _activeUiFragment = control;
        _lastFedState = null;

        if (ui is not null && control is not null && _activeProgram is { } program)
        {
            AttachCartridgeUI(control, Loc.GetString(comp?.ProgramName ?? "default-program-name"));

            // Every fragment is fed the state its program last replicated as soon as it is built, so it never waits
            // on the ready answer. That state can be older than what the answer brings.
            if (EntMan.TryGetComponent(program, out CartridgeUiStateComponent? stored) && stored.State is { } state)
            {
                _lastFedState = state;
                ui.UpdateState(state);
            }

            if (announce)
                SendCartridgeUiReadyEvent(program);
        }

        // Disposed last, so the outgoing fragment tears down after the incoming one has claimed any registration
        // they share.
        previousFragment?.Dispose();
    }

    /// <summary>
    /// Hands a program's newly replicated state to its fragment, if that program is the one showing.
    /// A null state means the program cleared it, which puts the fragment back to its empty view.
    /// </summary>
    public void FeedCartridge(EntityUid program, BoundUserInterfaceState? state)
    {
        if (!IsOpened || _activeProgram != program || _activeCartridgeUI is null)
            return;

        // A state applied again (PVS re-entry hands back the same object) would rebuild the fragment's controls
        // under the player for no change.
        if (ReferenceEquals(state, _lastFedState))
            return;

        if (state is null)
        {
            BuildActiveFragment(announce: false);
            return;
        }

        _lastFedState = state;
        _activeCartridgeUI.UpdateState(state);
    }
    // Triad end

    protected void ActivateCartridge(EntityUid cartridgeUid)
    {
        var message = new CartridgeLoaderUiMessage(_entManager.GetNetEntity(cartridgeUid), CartridgeUiMessageAction.Activate);
        SendPredictedMessage(message);
    }

    protected void DeactivateActiveCartridge()
    {
        if (!_activeProgram.HasValue)
            return;

        var message = new CartridgeLoaderUiMessage(_entManager.GetNetEntity(_activeProgram.Value), CartridgeUiMessageAction.Deactivate);
        SendPredictedMessage(message);
    }

    protected void InstallCartridge(EntityUid cartridgeUid)
    {
        var message = new CartridgeLoaderUiMessage(_entManager.GetNetEntity(cartridgeUid), CartridgeUiMessageAction.Install);
        SendPredictedMessage(message);
    }

    protected void UninstallCartridge(EntityUid cartridgeUid)
    {
        var message = new CartridgeLoaderUiMessage(_entManager.GetNetEntity(cartridgeUid), CartridgeUiMessageAction.Uninstall);
        SendPredictedMessage(message);
    }

    private List<(EntityUid, CartridgeComponent)> GetCartridgeComponents(List<EntityUid> programs)
    {
        var components = new List<(EntityUid, CartridgeComponent)>();

        foreach (var program in programs)
        {
            var component = RetrieveCartridgeComponent(program);
            if (component is not null)
                components.Add((program, component));
        }

        return components;
    }

    /// <summary>
    /// The implementing ui needs to add the passed ui fragment as a child to itself
    /// </summary>
    protected abstract void AttachCartridgeUI(Control cartridgeUIFragment, string? title);

    /// <summary>
    /// The implementing ui needs to remove the passed ui from itself
    /// </summary>
    protected abstract void DetachCartridgeUI(Control cartridgeUIFragment);

    protected abstract void UpdateAvailablePrograms(List<(EntityUid, CartridgeComponent)> programs);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            _activeUiFragment?.Dispose();

            // Triad: FeedCartridge and SyncWithLoader reach nothing through this BUI once its fragment is disposed
            _activeUiFragment = null;
            _activeCartridgeUI = null;
            _activeProgram = null;
            _lastFedState = null;
            _shownPrograms = null;

            if (EntMan.TrySystem(out CartridgeUiStateSystem? uiState))
                uiState.Untrack(this);
            // End Triad
        }
    }

    protected CartridgeComponent? RetrieveCartridgeComponent(EntityUid? cartridgeUid)
    {
        return EntMan.GetComponentOrNull<CartridgeComponent>(cartridgeUid);
    }

    private void SendCartridgeUiReadyEvent(EntityUid cartridgeUid)
    {
        var message = new CartridgeLoaderUiMessage(_entManager.GetNetEntity(cartridgeUid), CartridgeUiMessageAction.UIReady);
        SendMessage(message);
    }

    private UIFragment? RetrieveCartridgeUI(EntityUid? cartridgeUid)
    {
        var component = EntMan.GetComponentOrNull<UIFragmentComponent>(cartridgeUid);
        component?.Ui?.Setup(this, cartridgeUid);
        return component?.Ui;
    }
}
