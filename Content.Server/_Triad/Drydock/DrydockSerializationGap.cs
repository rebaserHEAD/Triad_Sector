using System;
using System.Collections.Immutable;
using Content.Shared._NF.Market;
using Content.Shared.Lathe;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// The two facts about serializability that the drydock depends on, in one place because both are
/// asserted by a test and relied on by the store path, and a copy of either that drifts from the
/// other is a silent data-loss bug rather than a failing build.
/// </summary>
public static class DrydockSerializationGap
{
    /// <summary>
    /// Does this exception mean the serializer has no way to write the type at all, as opposed to
    /// the value simply being a bad sample?
    ///
    /// The engine reports no coverage through two doors: the generated data-definition path throws
    /// <see cref="InvalidOperationException"/>, and <c>WriteNoSerializer</c>'s fallback throws
    /// <see cref="ArgumentException"/>. It has moved once already, and when it moved the audit
    /// reported zero gaps rather than failing, so this is deliberately narrow: anything not matched
    /// here is treated as a bad sample and the type is assumed writable.
    ///
    /// Both the fidelity layer's runtime probe and the serializability audit ask this question, and
    /// they must ask it of the same code. The audit's control is what proves this still works, and
    /// that proof is only worth anything while the store path shares it.
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
    /// The capture manifest: the types that fail the probe and are still worth preserving by hand.
    /// Everything else that fails is stripped, and comes back at its default.
    ///
    /// This is the only fork-specific knob in the fidelity layer. Adding an entry is a content
    /// decision, not a mechanical one, and the reasoning belongs on the Drydock State Fidelity
    /// Design wiki page next to the verdict for every other confirmed type. The serializability
    /// audit asserts against this set, so a type that starts serializing natively fails the build
    /// here rather than quietly becoming redundant hand-written work.
    /// </summary>
    public static readonly ImmutableHashSet<Type> CapturedTypes = ImmutableHashSet.Create(
        // Player-modified market state.
        typeof(MarketData),
        // Production a player queued up.
        typeof(LatheRecipeBatch));
}
