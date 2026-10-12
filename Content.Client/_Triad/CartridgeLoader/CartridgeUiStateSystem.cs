using Content.Client.CartridgeLoader;
using Content.Shared._Triad.CartridgeLoader;
using Content.Shared.CartridgeLoader;
using Robust.Shared.GameStates;

namespace Content.Client._Triad.CartridgeLoader;

/// <summary>
/// Keeps open loader BUIs in step with what replicates on the entities: each program's ui state as it arrives, and
/// the loader's program list and active program once a frame.
/// </summary>
public sealed partial class CartridgeUiStateSystem : EntitySystem
{
    [Dependency] private SharedUserInterfaceSystem _ui = default!;

    private readonly HashSet<CartridgeLoaderBoundUserInterface> _open = new();
    private readonly List<CartridgeLoaderBoundUserInterface> _syncing = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<CartridgeUiStateComponent, AfterAutoHandleStateEvent>(OnProgramStateHandled);
    }

    public void Track(CartridgeLoaderBoundUserInterface bui)
    {
        _open.Add(bui);
    }

    public void Untrack(CartridgeLoaderBoundUserInterface bui)
    {
        _open.Remove(bui);
    }

    /// <summary>
    /// A frame runs after the tick's state application and prediction, so what is read off the loader here has settled.
    /// </summary>
    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);

        if (_open.Count == 0)
            return;

        // Copied, since a sync that closes a ui untracks it.
        _syncing.Clear();
        _syncing.AddRange(_open);

        foreach (var bui in _syncing)
        {
            bui.SyncWithLoader();
        }
    }

    private void OnProgramStateHandled(Entity<CartridgeUiStateComponent> ent, ref AfterAutoHandleStateEvent args)
    {
        if (!TryComp(ent, out CartridgeComponent? cartridge) || cartridge.LoaderUid is not { } loaderUid)
            return;

        if (!TryComp(loaderUid, out CartridgeLoaderComponent? loader))
            return;

        if (_ui.TryGetOpenUi(loaderUid, loader.UiKey, out var bui) && bui is CartridgeLoaderBoundUserInterface loaderBui)
            loaderBui.FeedCartridge(ent, ent.Comp.State);
    }
}
