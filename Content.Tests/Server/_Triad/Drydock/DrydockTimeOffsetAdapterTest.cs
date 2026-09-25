#nullable enable
using System;
using Content.Server._Triad.Drydock.Codec;
using NUnit.Framework;
using Robust.Shared.Serialization.Markdown.Value;

namespace Content.Tests.Server._Triad.Drydock;

/// <summary>
/// The time adapter's sentinels. Zero, the maximum and the minimum mean "never" or "not before the end of time", not a
/// deadline, so each has to come back exactly whatever the clocks at store and at load; a real deadline still comes back
/// as its distance from the new clock, and one too far off to add clamps rather than throws.
/// </summary>
[TestFixture, TestOf(typeof(DrydockTimeOffsetAdapter))]
[Parallelizable(ParallelScope.All)]
public sealed class DrydockTimeOffsetAdapterTest
{
    private static readonly TimeSpan StoreClock = TimeSpan.FromSeconds(1234.5);
    private static readonly TimeSpan LoadClock = TimeSpan.FromSeconds(17.25);

    private static TimeSpan RoundTrip(TimeSpan value, TimeSpan? pause = null) =>
        DrydockTimeOffsetAdapter.Read(DrydockTimeOffsetAdapter.Write(value, StoreClock, pause), LoadClock);

    [Test]
    public void EachSentinelComesBackExactlyAcrossTwoClocks()
    {
        Assert.Multiple(() =>
        {
            Assert.That(RoundTrip(TimeSpan.Zero), Is.EqualTo(TimeSpan.Zero), "Zero is \"never\" and has to stay zero, not become a deadline already past.");
            Assert.That(RoundTrip(TimeSpan.MaxValue), Is.EqualTo(TimeSpan.MaxValue), "The maximum has to stay the maximum.");
            Assert.That(RoundTrip(TimeSpan.MinValue), Is.EqualTo(TimeSpan.MinValue), "The minimum has to stay the minimum.");
            Assert.That(RoundTrip(TimeSpan.Zero, pause: TimeSpan.FromSeconds(900)), Is.EqualTo(TimeSpan.Zero), "A paused entity's zero too.");
        });
    }

    /// <summary>The control: a real deadline still moves with the clock, and the sentinels do not take ordinary values.</summary>
    [Test]
    public void ARealDeadlineStillComesBackAsItsDistance()
    {
        var deadline = StoreClock + TimeSpan.FromSeconds(30);
        var justPast = StoreClock - TimeSpan.FromSeconds(2);
        var written = DrydockTimeOffsetAdapter.Write(deadline, StoreClock, null);

        Assert.Multiple(() =>
        {
            Assert.That(RoundTrip(deadline), Is.EqualTo(LoadClock + TimeSpan.FromSeconds(30)), "A deadline 30 s off has to come back 30 s off the new clock.");
            Assert.That(RoundTrip(justPast), Is.EqualTo(LoadClock - TimeSpan.FromSeconds(2)), "A deadline 2 s past has to come back 2 s past.");
            Assert.That(RoundTrip(StoreClock), Is.EqualTo(LoadClock), "A deadline of now is a real offset of zero, and has to come back as the new now.");
            Assert.That(written.Value, Is.Not.EqualTo(DrydockTimeOffsetAdapter.ZeroToken).And.Not.EqualTo(DrydockTimeOffsetAdapter.MaxToken),
                "An ordinary deadline must be written as a number.");
        });
    }

    [Test]
    public void AnOffsetTooFarToAddClampsRatherThanThrows()
    {
        var huge = new ValueDataNode((TimeSpan.MaxValue.TotalSeconds - 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.That(DrydockTimeOffsetAdapter.Read(huge, LoadClock), Is.EqualTo(TimeSpan.MaxValue),
            "An offset past what the load clock can take has to clamp to the maximum, as the engine's reader does.");
    }
}
