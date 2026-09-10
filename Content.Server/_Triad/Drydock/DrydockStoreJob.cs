using System;
using System.Threading;
using System.Threading.Tasks;
using Robust.Shared.CPUJob.JobQueues;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// The store pipeline, run a few milliseconds at a time on the main thread. The fork's first
/// <see cref="Job{T}"/>: the engine's pathfinder and the dungeon generator use the same machinery to
/// spread expensive work over ticks, and a drydock store is the same shape of problem - a couple of
/// seconds of main-thread work that nobody but the captain who asked for it should ever feel.
///
/// <para>The job is also the pipeline's <see cref="IDrydockSlice"/>. That pairing is deliberate: the
/// only way to suspend is through methods on this object, so a pipeline written against the
/// interface cannot accidentally take a bare await, which would leave <see cref="Job{T}"/> with no
/// resume handle and hang the job forever in release.</para>
/// </summary>
public sealed class DrydockStoreJob : Job<DrydockStoreOutcome>, IDrydockSlice
{
    private readonly DrydockSystem _system;

    /// <summary>How many items pass between stopwatch reads. Never less than one.</summary>
    private readonly int _stride;

    /// <summary>
    /// Real time since the last <see cref="Begin"/> or <see cref="Step"/>. Wall clock rather than
    /// the job's own stopwatch, because the thing the watchdog is looking for is a job that stopped
    /// being run at all, which the job's stopwatch cannot see.
    /// </summary>
    private readonly System.Diagnostics.Stopwatch _sinceProgress = System.Diagnostics.Stopwatch.StartNew();

    public DrydockStoreJob(
        DrydockSystem system,
        DrydockStoreContext context,
        double maxTime,
        int stride,
        DrydockProgressCallback? onProgress,
        CancellationToken cancellation)
        : base(maxTime, cancellation)
    {
        _system = system;
        _stride = Math.Max(1, stride);
        Context = context;
        Progress = new DrydockProgress(DrydockPhases.Store, onProgress);
    }

    public DrydockStoreContext Context { get; }

    public DrydockProgress Progress { get; }

    /// <summary>
    /// Whether this job can suspend at all. A budget of zero would make the engine's own
    /// out-of-time check a no-op, so rather than run a whole store inside one tick under a name that
    /// says otherwise, the caller is expected to skip the job entirely at that setting.
    /// </summary>
    public bool Slicing => MaxTime > 0.0;

    /// <summary>Worst single run span in milliseconds, a lower bound on the worst tick. Sampled at each suspension.</summary>
    public double WorstSliceMs { get; private set; }

    public int Slices { get; private set; }

    public double SecondsSinceProgress => _sinceProgress.Elapsed.TotalSeconds;

    // Explicit, because Job<T>.Cancellation is protected and a public one of the same name would
    // collide with the inherited member.
    CancellationToken IDrydockSlice.Cancellation => Cancellation;

    public async Task Begin(DrydockPhase phase, int items)
    {
        Progress.BeginPhase(phase, items);
        _sinceProgress.Restart();

        if (!Slicing)
            return;

        // Unconditional, not budget-conditional. A phase that starts with most of a tick's budget
        // already spent by the phase before it would otherwise overrun on its very first items,
        // and the phases whose first items are expensive are exactly the ones worth protecting.
        Sample();
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

    // No `new` modifier: Job<T> has no member named Await - it has WaitAsyncTask - so `new` would
    // be an error rather than a hide.
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

    protected override async Task<DrydockStoreOutcome?> Process()
    {
        return await _system.RunStorePipeline(Context, this);
    }

    /// <summary>
    /// Records the run span that is about to end. The engine restarts the stopwatch at the top of
    /// every run, so its elapsed time at a suspension is how long this one run held the main thread.
    /// A tick can still hold more than one run, because the queue re-runs a suspended job while its
    /// own clock has room, so this is a lower bound on the worst tick rather than the worst tick.
    /// </summary>
    private void Sample()
    {
        var ms = StopWatch.Elapsed.TotalMilliseconds;
        if (ms > WorstSliceMs)
            WorstSliceMs = ms;

        Slices++;
    }
}
