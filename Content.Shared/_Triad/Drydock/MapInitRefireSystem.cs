// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Content.Shared._Triad.Drydock;

/// <summary>
/// Raises <c>MapInitEvent</c> on an entity that is already map-initialized, and says while it is
/// doing so. The drydock's legacy import re-raises map init on a hull loaded from a legacy document
/// (<c>DrydockFidelitySystem.RefireMapInitSliced</c>) so every system's runtime setup runs; a handler
/// whose one-shot work would duplicate what the ship already holds, or log an error for it, checks
/// <see cref="Refiring"/> and skips that work. Every other map init (a purchase, a map load, a spawn,
/// a retrieve, which re-raises nothing) sees <see cref="Refiring"/> false and runs unchanged.
/// </summary>
public sealed class MapInitRefireSystem : EntitySystem
{
    /// <summary>
    /// True only inside <see cref="Raise"/>, on this entity manager. Never true across a tick, so a
    /// map init that happens between two raises of a sliced walk is an ordinary one.
    /// </summary>
    public bool Refiring { get; private set; }

    public void Raise(EntityUid uid)
    {
        Refiring = true;
        try
        {
            RaiseLocalEvent(uid, new MapInitEvent());
        }
        finally
        {
            Refiring = false;
        }
    }
}
