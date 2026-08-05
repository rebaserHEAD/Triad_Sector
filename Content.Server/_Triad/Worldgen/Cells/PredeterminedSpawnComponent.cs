// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Content.Server._Triad.Worldgen.Cells;

/// <summary>
///     Stamped onto a spawner marker (RandomSpawner / ConditionalSpawner / EntityTableSpawner)
///     that the floor-plan populator places on pre-determined debris. Carries the salted seed the
///     marker's MapInit rolls must use, so the loot layer (which crate, how many, where they
///     scatter) rebuilds identically when the rock re-materializes.
///
///     Absent means the marker rolls the shared RNG exactly as before. ConditionalSpawnerSystem
///     serves dungeons, salvage missions and ghost roles game-wide; this component is how one
///     marker opts in without changing the default for any of them.
///
///     Unsaved: generation-time context, consumed at MapInit; a captured debris grid must not
///     carry it into a blob.
/// </summary>
[RegisterComponent, UnsavedComponent]
public sealed partial class PredeterminedSpawnComponent : Component
{
    public int Seed;
}
