using System;
using System.Threading.Tasks;
using Content.Server.GameTicking;
using Content.Shared._Triad.CCVar;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// The escrow sweep. An offer's deadline is a persisted timestamp, so it keeps running while the
/// owner is logged off and across a restart; this is the one place that acts on it. A ship past
/// its deadline goes back to Stored and the offer is marked Expired.
/// </summary>
public sealed partial class DrydockSystem
{
    [Dependency] private readonly GameTicker _ticker = default!;

    /// <summary>
    /// How often the sweep runs. Coarse on purpose: the deadline the players see is thirty minutes,
    /// and the accept path checks the deadline itself, so a sweep that lands late never lets a
    /// stale offer complete. Finer would only add database reads.
    /// </summary>
    private const float TransferSweepSeconds = 30f;

    private float _transferSweepAccumulator;
    private bool _transferSweepRunning;

    public override void Initialize()
    {
        base.Initialize();

        // The boot sweep: a restart mid-offer must not strand a ship in escrow until the next
        // deadline happens to pass while someone is looking.
        _ = SweepExpiredTransfers();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        _transferSweepAccumulator += frameTime;
        if (_transferSweepAccumulator < TransferSweepSeconds)
            return;

        _transferSweepAccumulator = 0f;
        _ = SweepExpiredTransfers();
    }

    private async Task SweepExpiredTransfers()
    {
        // One sweep in flight at a time. A slow database must not stack sweeps that then race each
        // other over the same rows; the resolve is conditional on Pending so a lost race is
        // harmless, but it is still wasted work.
        if (_transferSweepRunning
            || !_cfg.GetCVar(TriadCCVars.DrydockEnabled)
            || _cfg.GetCVar(TriadCCVars.DrydockReadOnly))
        {
            return;
        }

        _transferSweepRunning = true;
        try
        {
            var roundId = _ticker.RoundId > 0 ? _ticker.RoundId : (int?)null;
            var released = await _store.ExpireTransfers(DateTime.UtcNow, roundId);
            if (released.Count == 0)
                return;

            Log.Info($"Drydock: {released.Count} transfer offer(s) expired; ships returned to their owners' berths.");
            _shipyard.KickDrydockRefreshAll();
        }
        catch (Exception e)
        {
            Log.Error($"Drydock: the transfer expiry sweep threw: {e}");
        }
        finally
        {
            _transferSweepRunning = false;
        }
    }
}
