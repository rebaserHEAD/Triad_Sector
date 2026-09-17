using System.Linq;
using Content.Server.Cargo.Systems; // Triad: PriceCalculationEvent is still server-side here
using Content.Server.Kitchen.Components; // Triad: BeingMicrowavedEvent relay
using Content.Shared.Xenoarchaeology.Artifact;
using Content.Shared.Xenoarchaeology.Artifact.Components;

namespace Content.Server.Xenoarchaeology.Artifact;

/// <inheritdoc cref="SharedXenoArtifactSystem"/>
public sealed partial class XenoArtifactSystem : SharedXenoArtifactSystem
{
    /// <summary>
    /// Triad: artifacts that started up this tick and still want a graph. See OnArtifactStartup.
    /// </summary>
    private readonly List<EntityUid> _pendingGeneration = new();

    /// <inheritdoc/>
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<XenoArtifactComponent, MapInitEvent>(OnArtifactMapInit);
        SubscribeLocalEvent<XenoArtifactComponent, PriceCalculationEvent>(OnCalculatePrice);

        // Triad: BeingMicrowavedEvent is a server-side class event here, so it cannot go through the
        // shared by-ref relay. Relay it by hand for XATMicrowaveSystem.
        SubscribeLocalEvent<XenoArtifactComponent, BeingMicrowavedEvent>(OnMicrowaved);
    }

    /// <inheritdoc />
    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_pendingGeneration.Count == 0)
            return;

        // Triad: drained a tick after startup so a freshly spawned artifact has had its MapInit and
        // cleared the flag by the time we look. See OnArtifactStartup for why this exists.
        foreach (var uid in _pendingGeneration)
        {
            if (!TryComp<XenoArtifactComponent>(uid, out var comp) || !comp.IsGenerationRequired)
                continue;

            // The test is the MAP's life stage, not the entity's. An entity loaded from a file onto a
            // map that is already running never receives MapInitEvent: the map's init already
            // happened, and the loader restores it as merely Initialized. That artifact is stranded,
            // and it is the one we are here for. An artifact on a map that has NOT initialized yet
            // (the shipyard hold) still has its MapInit coming and must be left alone.
            if (Transform(uid).MapUid is not { } mapUid
                || MetaData(mapUid).EntityLifeStage < EntityLifeStage.MapInitialized)
                continue;

            if (GetAllNodes((uid, comp)).Any())
            {
                comp.IsGenerationRequired = false;
                continue;
            }

            Log.Info($"{ToPrettyString(uid)} arrived map-initialized with no node graph (restored from a file?); generating one now.");
            GenerateArtifactStructure((uid, comp));
        }

        _pendingGeneration.Clear();
    }

    private void OnMicrowaved(EntityUid uid, XenoArtifactComponent comp, BeingMicrowavedEvent args)
    {
        RelayEventToNodes((uid, comp), ref args);
    }


    private void OnArtifactMapInit(Entity<XenoArtifactComponent> ent, ref MapInitEvent args)
    {
        if (ent.Comp.IsGenerationRequired)
            GenerateArtifactStructure(ent);
    }

    private void OnCalculatePrice(Entity<XenoArtifactComponent> ent, ref PriceCalculationEvent args)
    {
        // Triad: whole-artifact multiplier on top of the per-node sum, shared with the extract path
        var price = 0.0;
        foreach (var node in GetAllNodes(ent))
        {
            if (node.Comp.Locked)
                continue;

            price += node.Comp.ResearchValue * ent.Comp.PriceMultiplier;
        }

        args.Price += price * GetArtifactPayoutMultiplier(ent);
    }
}
