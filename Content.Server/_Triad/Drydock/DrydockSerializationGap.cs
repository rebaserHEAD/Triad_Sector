using System;
using System.Collections.Immutable;
using Content.Shared._NF.Market;
using Content.Shared.Lathe;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// The two facts about serializability the drydock's tests share, in one place so every test asks
/// the same code: whether an exception means the engine has no serializer for a type, and which such
/// types the codec carries with serializers of its own.
/// </summary>
public static class DrydockSerializationGap
{
    /// <summary>
    /// Does this exception mean the serializer has no way to write the type at all, as opposed to
    /// the value simply being a bad sample?
    ///
    /// Two doors: the generated data-definition path throws <see cref="InvalidOperationException"/>,
    /// <c>WriteNoSerializer</c>'s fallback throws <see cref="ArgumentException"/>. Deliberately
    /// narrow: anything unmatched is treated as a bad sample and the type assumed writable. The
    /// audit and the codec tests ask this one method, so they cannot disagree about what a gap is.
    /// </summary>
    public static bool IsNoCoverage(Exception e)
    {
        return e switch
        {
            InvalidOperationException when e.Message.Contains("No data definition found") => true,
            ArgumentException when e.Message.Contains("No type serializer or data definition found") => true,
            _ => false,
        };
    }

    /// <summary>
    /// The types the engine has no serializer for that a stored ship still carries, through the
    /// codec's own serializers (<see cref="Codec.DrydockLatheRecipeBatchSerializer"/>,
    /// <see cref="Codec.DrydockMarketDataSerializer"/>).
    ///
    /// An entry is a content decision, and the reasoning belongs on the Drydock State Fidelity
    /// Design wiki page. The audit asserts against
    /// this set, so a type that starts serializing natively fails the build rather than quietly
    /// becoming redundant hand-written work.
    /// </summary>
    public static readonly ImmutableHashSet<Type> CapturedTypes = ImmutableHashSet.Create(
        // Player-modified market state.
        typeof(MarketData),
        // Production a player queued up.
        typeof(LatheRecipeBatch));
}
