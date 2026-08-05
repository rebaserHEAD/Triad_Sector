// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.Maths;

namespace Content.Shared._Triad.Worldgen;

/// <summary>
///     Shared profiling constants for the worldgen records pipeline. Server and client both open
///     <c>ProfManager.Group</c> zones for their half of it, and a capture is only readable if both
///     halves land in the same visual band, so the tint lives here rather than once per assembly.
/// </summary>
public static class WorldgenProfiling
{
    /// <summary>
    ///     Tint for every worldgen zone, so our work is one recognisable colour in a Tracy capture
    ///     otherwise full of engine zones. Tracy paints child zones with their parent's colour, so
    ///     only the outermost zone of a pass needs to pass it. The in-game profiler ignores it.
    /// </summary>
    public static readonly Color ZoneColor = Color.FromHex("#c9772e");
}
