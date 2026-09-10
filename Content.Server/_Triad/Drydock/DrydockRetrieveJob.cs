using System;
using System.Threading;
using System.Threading.Tasks;
using Robust.Shared.CPUJob.JobQueues;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// The retrieve pipeline, run a few milliseconds at a time on the main thread. The same shape as
/// <see cref="DrydockStoreJob"/> and deliberately not shared with it through a generic base: the two
/// differ only in their context type and the pipeline they call, and a base class parameterised over
/// both would cost more to read than the forty lines it saved.
///
/// <para>The inbound leg has a harder floor than the outbound one. The store can drive the engine's
/// serializer one entity at a time from content, but every per-entity loop inside the deserializer
/// is private, so the grid load is one bulk call and the slicing starts at the revive epilogue
/// after it.</para>
/// </summary>
public sealed class DrydockRetrieveJob : Job<DrydockRetrieveOutcome>, IDrydockSlice
{
    private readonly DrydockSystem _system;

    private readonly int _stride;

    /// <summary>
    /// Real time since the last <see cref="Begin"/> or <see cref="Step"/>. Wall clock rather than
    /// the job's own stopwatch, because the thing the watchdog is looking for is a job that stopped
    /// being run at all, which the job's stopwatch cannot see.
    /// </summary>
    private readonly System.Diagnostics.Stopwatch _sinceProgress = System.Diagnostics.Stopwatch.StartNew();

    public DrydockRetrieveJob(
        DrydockSystem system,
        DrydockRetrieveContext context,
        double maxTime,
        int stride,
        DrydockProgressCallback? onProgress,
        CancellationToken cancellation)
        : base(maxTime, cancellation)
    {
        _system = system;
        _stride = Math.Max(1, stride);
        Context = context;
        Progress = new DrydockProgress(DrydockPhases.Retrieve, onProgress);
    }

    public DrydockRetrieveContext Context { get; }

    public DrydockProgress Progress { get; }

    public bool Slicing => MaxTime > 0.0;

    /// <summary>Worst single run span in milliseconds, a lower bound on the worst tick. Sampled at each suspension.</summary>
    public double WorstSliceMs { get; private set; }

    public int Slices { get; private set; }

    /// <summary>
    /// The phase that owns the span currently being timed: whichever one was open when this run
    /// started, not whichever one is open when it ends. <c>Begin</c> calls
    /// <c>Progress.BeginPhase</c> before it suspends, so reading the phase off Progress at a
    /// suspension would file every phase's tail under the phase that follows it. Null until the
    /// first phase opens.
    /// </summary>
    private DrydockPhase? _spanPhase;

    /// <summary>Per-tick, per-phase main-thread cost. See <see cref="DrydockSpanMeter"/>.</summary>
    public DrydockSpanMeter Meter { get; } = new();

    public double SecondsSinceProgress => _sinceProgress.Elapsed.TotalSeconds;

    // Explicit, because Job<T>.Cancellation is protected and a public one of the same name would
    // collide with the inherited member.
    CancellationToken IDrydockSlice.Cancellation => Cancellation;

    public async Task Begin(DrydockPhase phase, int items)
    {
        Progress.BeginPhase(phase, items);
        _sinceProgress.Restart();

        if (!Slicing)
        {
            _spanPhase = phase;
            return;
        }

        // Unconditional, so one phase's overrun never lands on the next phase's first items.
        Sample();

        // After the sample, so the span just closed is credited to the phase that ran it.
        _spanPhase = phase;
        await SuspendNow();
    }

    public async Task Step(int index)
    {
        Progress.Advance(index);
        _sinceProgress.Restart();

        if (!Slicing || index % _stride != 0)
            return;

        // The engine's SuspendIfOutOfTime would do this, but it cannot tell us whether it actually
        // suspended, and the worst-slice figure is only meaningful when sampled at a real one.
        if (StopWatch.Elapsed.TotalSeconds <= MaxTime)
            return;

        Sample();
        await SuspendNow();
    }

    public async Task<T> Await<T>(Task<T> task)
    {
        Sample();
        _sinceProgress.Restart();

        var result = await WaitAsyncTask(task);

        _sinceProgress.Restart();
        return result;
    }

    public async Task Await(Task task)
    {
        Sample();
        _sinceProgress.Restart();

        await WaitAsyncTask(task);

        _sinceProgress.Restart();
    }

    protected override async Task<DrydockRetrieveOutcome?> Process()
    {
        try
        {
            return await _system.RunRetrievePipeline(Context, this);
        }
        finally
        {
            // The dock is the last thing a retrieve does and nothing suspends after it: from the
            // resume inside Begin(Dock) to the pipeline's return there is no Await, Begin or Step,
            // which the retrieve states as an invariant. So without this the final run - holding
            // TryFTLDock, one of the four calls content cannot interrupt - was never sampled at
            // all, and every retrieve timing line under-reported its own worst span. The guard is
            // the engine's own (Job.cs:193-200): a job still Waiting reached here because an
            // awaited task faulted, so the stopwatch is holding a span containing the whole
            // off-thread wait. The fault path's own main-thread work is not measured.
            if (Status != JobStatus.Waiting)
                Sample(suspending: false);

            Meter.Flush();
        }
    }

    /// <summary>
    /// Records the run span that is about to end. The engine restarts the stopwatch at the top of
    /// every run, so its elapsed time at a suspension is how long this one run held the main thread.
    /// A tick can still hold more than one run, because the queue re-runs a suspended job while its
    /// own clock has room, so this is a lower bound on the worst tick rather than the worst tick.
    /// </summary>
    private void Sample(bool suspending = true)
    {
        var ms = StopWatch.Elapsed.TotalMilliseconds;
        if (ms > WorstSliceMs)
            WorstSliceMs = ms;

        Meter.Add(_system.CurTickValue, _spanPhase, ms);

        // Only a real suspension is a slice. The pipeline's closing sample is not one, and counting
        // it would change what the shipped timing line's slices= has always meant.
        if (suspending)
            Slices++;
    }
}
