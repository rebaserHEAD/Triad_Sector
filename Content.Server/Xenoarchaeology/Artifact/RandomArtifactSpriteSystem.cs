using Content.Shared.Item;
using Content.Shared.Xenoarchaeology.Artifact;
using Content.Shared.Xenoarchaeology.XenoArtifacts;
using Robust.Server.GameObjects;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server.Xenoarchaeology.Artifact;

public sealed class RandomArtifactSpriteSystem : EntitySystem
{
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private IGameTiming _time = default!;
    [Dependency] private AppearanceSystem _appearance = default!;
    [Dependency] private SharedItemSystem _item = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<RandomArtifactSpriteComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<RandomArtifactSpriteComponent, ArtifactUnlockingStartedEvent>(UnlockingStageStarted);
        SubscribeLocalEvent<RandomArtifactSpriteComponent, ArtifactUnlockingFinishedEvent>(UnlockingStageFinished);
        SubscribeLocalEvent<RandomArtifactSpriteComponent, XenoArtifactActivatedEvent>(ArtifactActivated);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<RandomArtifactSpriteComponent, AppearanceComponent>();
        while (query.MoveNext(out var uid, out var component, out var appearance))
        {
            if (component.ActivationStart == null)
                continue;

            var timeDif = _time.CurTime - component.ActivationStart.Value;
            // Triad: TimeSpan.Seconds is the seconds COMPONENT of the span, not its length, so with
            // the default 0.4s activation this stayed 0 for a whole second and only cleared when it
            // ticked over to 1. The flash ran two and a half times as long as authored.
            if (timeDif.TotalSeconds >= component.ActivationTime)
            {
                _appearance.SetData(uid, SharedArtifactsVisuals.IsActivated, false, appearance);
                component.ActivationStart = null;
            }
        }
    }


    private void OnMapInit(EntityUid uid, RandomArtifactSpriteComponent component, MapInitEvent args)
    {
        RollSprite((uid, component));
    }


    /// <summary>
    /// Triad: rolls an index if the component does not have one yet, then applies it.
    /// </summary>
    private void RollSprite(Entity<RandomArtifactSpriteComponent> ent)
    {
        var index = ent.Comp.SpriteIndex ??= _random.Next(ent.Comp.MinSprite, ent.Comp.MaxSprite + 1);
        ApplySprite(ent, index);
    }

    private void ApplySprite(Entity<RandomArtifactSpriteComponent> ent, int index)
    {
        _appearance.SetData(ent, SharedArtifactsVisuals.SpriteIndex, index);
        _item.SetHeldPrefix(ent, "ano" + index.ToString("D2")); //set item artifact inhands
    }

    private void UnlockingStageStarted(Entity<RandomArtifactSpriteComponent> ent, ref ArtifactUnlockingStartedEvent args)
    {
        _appearance.SetData(ent, SharedArtifactsVisuals.IsUnlocking, true);
    }

    private void UnlockingStageFinished(Entity<RandomArtifactSpriteComponent> ent, ref ArtifactUnlockingFinishedEvent args)
    {
        _appearance.SetData(ent, SharedArtifactsVisuals.IsUnlocking, false);
    }

    private void ArtifactActivated(Entity<RandomArtifactSpriteComponent> ent, ref XenoArtifactActivatedEvent args)
    {
        _appearance.SetData(ent, SharedArtifactsVisuals.IsActivated, true);
        ent.Comp.ActivationStart = _time.CurTime;
    }
}
