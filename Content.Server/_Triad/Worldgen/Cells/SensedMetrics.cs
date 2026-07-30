// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Prometheus;

namespace Content.Server._Triad.Worldgen.Cells;

public static class SensedMetrics
{
    public static readonly Counter CellsDescribed = Metrics.CreateCounter(
        "ss14_sensed_cells_described_total",
        "Total number of worldgen cells described by the sensed tier.");

    public static readonly Gauge Records = Metrics.CreateGauge(
        "ss14_sensed_records",
        "Number of live sensed-tier cell records currently tracked.");

    public static readonly Histogram DescribePass = Metrics.CreateHistogram(
        "ss14_sensed_describe_seconds",
        "Time spent per describe pass.",
        new HistogramConfiguration
        {
            Buckets = Histogram.ExponentialBuckets(0.0001, 2, 14)
        });

    public static readonly Gauge MaterializeQueueDepth = Metrics.CreateGauge(
        "ss14_sensed_materialize_queue_depth",
        "Number of dormant cells queued for JIT materialization.");

    public static readonly Histogram MaterializeBatch = Metrics.CreateHistogram(
        "ss14_sensed_materialize_batch_seconds",
        "Time spent per materialize batch.",
        new HistogramConfiguration
        {
            Buckets = Histogram.ExponentialBuckets(0.0001, 2, 14)
        });

    public static readonly Counter ContactsSent = Metrics.CreateCounter(
        "ss14_sensed_contacts_sent_total",
        "Total number of sensed contact records sent to clients.");

    // The two gauges below measure the tier's reason for existing rather than its internals:
    // radar reach without entity cost. Everything above this line reports on the machinery.

    /// <summary>
    ///     Live debris grids, counted in both modes on purpose. The sensed tier's claim is that
    ///     JIT materialization holds fewer entities resident than the stock burst-spawn placer at
    ///     the same radar reach, and this is the number that settles it. Flip
    ///     <c>triad.worldgen.sensed_enabled</c> and read it either side.
    /// </summary>
    public static readonly Gauge ResidentDebris = Metrics.CreateGauge(
        "ss14_sensed_resident_debris",
        "Currently resident debris grids (SpaceDebrisComponent), counted with the tier on or off.");

    /// <summary>
    ///     Cost of one console's contact scan. <c>CollectVisible</c> walks every live record with
    ///     no spatial index, per console, twice a second, and records do not evict in-round, so
    ///     this is what tells us whether the perception side is eating the residency win.
    /// </summary>
    public static readonly Histogram ContactScan = Metrics.CreateHistogram(
        "ss14_sensed_contact_scan_seconds",
        "Duration of one console's sensed contact visibility scan.",
        new HistogramConfiguration
        {
            Buckets = Histogram.ExponentialBuckets(0.00001, 2, 16)
        });

    /// <summary>Records walked per contact scan; the linear term in <see cref="ContactScan"/>.</summary>
    public static readonly Histogram ContactScanRecords = Metrics.CreateHistogram(
        "ss14_sensed_contact_scan_records",
        "Records examined during one console's sensed contact visibility scan.",
        new HistogramConfiguration
        {
            Buckets = Histogram.ExponentialBuckets(64, 2, 12)
        });
}
