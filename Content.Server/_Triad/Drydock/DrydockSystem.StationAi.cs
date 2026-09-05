using Content.Shared.Mind.Components;
using Content.Shared.Silicons.StationAi;
using Robust.Shared.Containers;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// The one content-specific hook in the whole store path, kept in its own file so it lifts out
/// cleanly in a fork that has no station AI. Everything else the drydock does adapts to whatever
/// content exists; this knows about exactly one thing, and only because that thing deliberately
/// lives off the grid.
/// </summary>
public sealed partial class DrydockSystem
{
    [Dependency] private SharedContainerSystem _containers = default!;

    /// <summary>
    /// Empties any vacant station AI core aboard, before the grid is serialized.
    ///
    /// <para>A station AI's runtime apparatus hangs off the grid: the core points at an invisible
    /// eye entity in null space, and the brain in the core's slot references that same eye. The
    /// serializer therefore logs dangling references for any ship carrying an active AI, which the
    /// round-trip check then rejects. Rather than trying to store the AI, the vacant brain and the
    /// eye are deleted and the reference cleared, so the physical core survives empty and takes a
    /// fresh intellicard on the far side. Minds and their apparatus do not ride through storage, the
    /// same rule the organics gate enforces.</para>
    ///
    /// <para>An occupied core is left strictly alone. Deleting a live AI's brain would ghost the
    /// player, and refusing to store a ship with an AI aboard is the organics gate's call, not this
    /// step's.</para>
    ///
    /// <para>Unlike the rest of preparation this is not undoable, which is why it runs after
    /// everything that is. That costs nothing: an empty core is the intended end state, so an abort
    /// after this point has simply reached it early on a hull that is still flying.</para>
    /// </summary>
    private void SanitizeStationAiCores(EntityUid gridUid)
    {
        var query = AllEntityQuery<StationAiCoreComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var core, out var xform))
        {
            if (xform.GridUid != gridUid)
                continue;

            EntityUid? brain = null;
            if (_containers.TryGetContainer(uid, StationAiHolderComponent.Container, out var container)
                && container.ContainedEntities.Count > 0)
            {
                brain = container.ContainedEntities[0];

                if (TryComp<MindContainerComponent>(brain, out var mind) && mind.HasMind)
                    continue;
            }

            // Immediate deletes, not queued. Serialization runs synchronously later in this same
            // tick, so a merely queued brain would still be a grid child when the serializer walks
            // the tree, and the dangling reference would come straight back. Clear the eye reference
            // first so the still-live core never points at a deleted entity.
            if (core.RemoteEntity is { } eye)
            {
                core.RemoteEntity = null;
                Dirty(uid, core);
                Del(eye);
            }

            if (brain is { } b)
                Del(b);
        }
    }
}
