// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.Random;

namespace Content.Server._Triad.Worldgen.Cells;

/// <summary>
///     Per-stage RNG for pre-determined debris. The stages that decide a rock's own contents
///     derive from its record seed, salted so the stages do not share a stream: adding or
///     removing a roll in one stage must not shift the others.
///
///     Not every roll a debris grid makes is covered. Decoration applied by upstream systems that
///     never consult <see cref="PredeterminedShapeComponent"/>, chiefly RoomFill, keeps rolling on
///     the shared random and so re-rolls on each materialization.
/// </summary>
public static class SeededRandom
{
    public const int ShapeStage = 0;
    public const int InteriorStage = 0x5f37;
    public const int DepositStage = 0x2b91;

    /// <summary>
    ///     A seeded RNG for the given stage, or null when the entity is not pre-determined and
    ///     the caller should keep using the shared random.
    /// </summary>
    public static IRobustRandom? ForStage(IEntityManager entityManager, EntityUid uid, int stage)
    {
        if (!entityManager.TryGetComponent<PredeterminedShapeComponent>(uid, out var shape))
            return null;

        var rand = new RobustRandom();
        rand.SetSeed(shape.Seed ^ stage);
        return rand;
    }
}
