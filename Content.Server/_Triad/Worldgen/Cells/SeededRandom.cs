using Robust.Shared.Random;

namespace Content.Server._Triad.Worldgen.Cells;

/// <summary>
///     Per-stage RNG for pre-determined debris. Every procedural stage of one piece of debris
///     derives from its record seed, salted so the stages do not share a stream: adding or
///     removing a roll in one stage must not shift the others.
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
