using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// Phase stopwatch for the store and retrieve pipelines, so a hitch is attributed to a phase rather
/// than guessed at. <see cref="Mark"/> closes the running phase and opens the next, so the caller
/// only names boundaries.
///
/// <para>Wall clock, not game-thread time. Phases spanning an <c>await</c> on the database include
/// time the server spent ticking normally, so they are not stalls; the store's gate and commit are
/// the two that do. The synchronous phases are the ones that can hitch, and they are the reason
/// this exists.</para>
///
/// <para>Emitted at Info: a store or retrieve is a rare deliberate player action, so one line each
/// costs nothing and reaches the server log without anyone enabling debug first.</para>
/// </summary>
public sealed class DrydockPhaseTimer
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private readonly List<(string Phase, long Ms)> _phases = new();
    private long _last;

    public void Mark(string phase)
    {
        var now = _watch.ElapsedMilliseconds;
        _phases.Add((phase, now - _last));
        _last = now;
    }

    public long TotalMs => _watch.ElapsedMilliseconds;

    /// <summary>
    /// One key=value line in execution order. Flat and delimiter-free so Loki can pattern-match the
    /// phases out of it without a parser.
    /// </summary>
    public string Format(string operation, Guid shipId, int entities)
    {
        var sb = new StringBuilder();
        sb.Append("Drydock timing: ").Append(operation).Append(' ').Append(shipId);
        sb.Append(" entities=").Append(entities);

        foreach (var (phase, ms) in _phases)
            sb.Append(' ').Append(phase).Append('=').Append(ms).Append("ms");

        return sb.Append(" total=").Append(TotalMs).Append("ms").ToString();
    }
}
