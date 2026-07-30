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
}
