// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Prometheus;

namespace Content.Server._Triad.Worldgen.Cells;

public static class RecordMetrics
{
    public static readonly Counter CellsDescribed = Metrics.CreateCounter(
        "ss14_records_cells_described_total",
        "Total number of worldgen cells described by the records system.");

    public static readonly Gauge Records = Metrics.CreateGauge(
        "ss14_records_records",
        "Number of live worldgen cell records currently tracked.");

    public static readonly Histogram DescribePass = Metrics.CreateHistogram(
        "ss14_records_describe_seconds",
        "Time spent per describe pass.",
        new HistogramConfiguration
        {
            Buckets = Histogram.ExponentialBuckets(0.0001, 2, 14)
        });

    public static readonly Gauge MaterializeQueueDepth = Metrics.CreateGauge(
        "ss14_records_materialize_queue_depth",
        "Number of dormant cells queued for JIT materialization.");

    public static readonly Histogram MaterializeBatch = Metrics.CreateHistogram(
        "ss14_records_materialize_batch_seconds",
        "Time spent per materialize batch.",
        new HistogramConfiguration
        {
            Buckets = Histogram.ExponentialBuckets(0.0001, 2, 14)
        });

    public static readonly Counter ContactsSent = Metrics.CreateCounter(
        "ss14_records_contacts_sent_total",
        "Total number of record contact records sent to clients.");

    // The two gauges below measure the tier's reason for existing rather than its internals:
    // radar reach without entity cost. Everything above this line reports on the machinery.

    /// <summary>
    ///     Live debris grids, counted in both modes on purpose. The records system's claim is that
    ///     JIT materialization holds fewer entities resident than the stock burst-spawn placer at
    ///     the same radar reach, and this is the number that settles it. Flip
    ///     <c>triad.worldgen.records_enabled</c> and read it either side.
    /// </summary>
    public static readonly Gauge ResidentDebris = Metrics.CreateGauge(
        "ss14_records_resident_debris",
        "Currently resident debris grids (SpaceDebrisComponent), counted with the tier on or off.");

    /// <summary>
    ///     Cost of one console's contact scan. <c>CollectVisible</c> walks every live record with
    ///     no spatial index, per console, twice a second, and records do not evict in-round, so
    ///     this is what tells us whether the perception side is eating the residency win.
    /// </summary>
    public static readonly Histogram ContactScan = Metrics.CreateHistogram(
        "ss14_records_contact_scan_seconds",
        "Duration of one console's record contact visibility scan.",
        new HistogramConfiguration
        {
            Buckets = Histogram.ExponentialBuckets(0.00001, 2, 16)
        });

    /// <summary>Records walked per contact scan; the linear term in <see cref="ContactScan"/>.</summary>
    public static readonly Histogram ContactScanRecords = Metrics.CreateHistogram(
        "ss14_records_contact_scan_records",
        "Records examined during one console's record contact visibility scan.",
        new HistogramConfiguration
        {
            Buckets = Histogram.ExponentialBuckets(64, 2, 12)
        });

    public static readonly Gauge CaptureBlobs = Metrics.CreateGauge(
        "ss14_records_capture_blobs",
        "Captured debris blobs currently held in the round-scoped store.");

    public static readonly Gauge CaptureBytes = Metrics.CreateGauge(
        "ss14_records_capture_bytes",
        "Compressed bytes currently held by the debris capture store.");

    public static readonly Counter Captures = Metrics.CreateCounter(
        "ss14_records_captures_total",
        "Total debris grids captured to blobs at cell unload.");

    /// <summary>
    ///     Each eviction is one touched rock reverting to its pristine roll: data loss for
    ///     whoever touched it, tolerated only because the alternative is unbounded memory.
    ///     Anything above zero in production means the budget cvar is undersized.
    /// </summary>
    public static readonly Counter CaptureEvictions = Metrics.CreateCounter(
        "ss14_records_capture_evictions_total",
        "Captured debris blobs evicted by the capture store's memory budget.");

    public static readonly Counter CaptureRestoreFailures = Metrics.CreateCounter(
        "ss14_records_capture_restore_failures_total",
        "Captured debris blobs that failed to deserialize at materialize time.");

    /// <summary>
    ///     A touched grid whose serialization failed every retry and was released to the GC
    ///     uncaptured: its changes regenerate on next materialize. Like evictions, anything
    ///     above zero is an alarm bell, but this one means a serializer bug, not a budget knob.
    /// </summary>
    public static readonly Counter CaptureGiveUps = Metrics.CreateCounter(
        "ss14_records_capture_giveups_total",
        "Touched debris grids released to GC after exhausting capture retries.");
}
