
using Robust.Shared.Configuration;

namespace Content.Shared._Triad.CCVar;

/// <summary>
/// Configuration variables for Triad features
/// </summary>
[CVarDefs]
public sealed class TriadCCVars
{
    // Triad: tamper protection
    /// <summary>
    /// Tamper protection rollout mode. "off" disables all checks and logging; "notify" passively
    /// collects signatures and writes audit events but never blocks; "enforce" rejects loads that
    /// fail the signature/trust checks. Unrecognised values fall back to "notify".
    /// </summary>
    public static readonly CVarDef<string> TamperMode =
        CVarDef.Create("triad.tamper_mode", "notify", CVar.SERVERONLY);

    /// <summary>
    /// F14 fix: directory holding the on-disk PEM files for the tamper-protection signing keys.
    /// The DB stores only the public key and a KeyId; the private key lives in a file named
    /// {KeyId}.pem inside this directory. Default is relative to the server's working directory.
    /// Admins should ensure the directory has restrictive permissions (0700 on Unix; ACL
    /// equivalent on Windows). The keystore creates the directory with 0700 on first use.
    /// </summary>
    public static readonly CVarDef<string> TamperSigningKeysDir =
        CVarDef.Create("triad.tamper_signing_keys_dir", "./triad-signing-keys", CVar.SERVERONLY);

    // Triad: radiator overhaul
    /// <summary>
    /// Whether radiators pushed into the top thermal bucket (white-hot) slowly
    /// take structural damage until they rupture. Off by default: the glow ramp
    /// and the contact burn already telegraph an overloaded fin, so losing the
    /// hardware on top of that is punishment rather than feedback. Turn it on
    /// to force players to spread load across an array.
    /// </summary>
    public static readonly CVarDef<bool> RadiatorOverheatDamage =
        CVarDef.Create("triad.radiator_overheat_damage", false, CVar.SERVERONLY);

    public static readonly CVarDef<bool> UseNightVisionColor =
        CVarDef.Create("triad.use_night_vision_color", false, CVar.CLIENTONLY | CVar.ARCHIVE, "If a custom night vision color should be used instead of the default.");

    public static readonly CVarDef<string> NightVisionColor =
        CVarDef.Create("triad.night_vision_color", "#00FF00", CVar.CLIENTONLY | CVar.ARCHIVE, "The tint/phosphor color of night vision.");

    // Triad: atmos
    /// <summary>
    /// Whether atmos input devices (scrubbers, siphoning vents, passive vents, intakes) may pull gas
    /// out of a map's own atmosphere. Off by default, which limits them to the sector map and ships
    /// in FTL, so expedition planets can no longer be drained for free gas.
    /// </summary>
    public static readonly CVarDef<bool> AllowMapGasExtraction =
        CVarDef.Create("triad.atmos.allow_map_gas_extraction", false, CVar.SERVER | CVar.REPLICATED);

    // Triad: drydock
    /// <summary>
    /// Master switch for the drydock. Off means the console offers neither store nor retrieve and
    /// the maintenance ladder does not run. Stored ships are untouched either way.
    /// </summary>
    public static readonly CVarDef<bool> DrydockEnabled =
        CVarDef.Create("triad.drydock.enabled", false, CVar.SERVERONLY);

    /// <summary>
    /// Retrieve is allowed, store is refused, and the re-bake ladder pauses. This is the switch to
    /// reach for when a build is suspected of writing bad revisions: the deploy pipeline is a daily
    /// cron with no rollback path, so refusing loudly for a day beats filing a day of bad blobs
    /// while still letting people fly the ships they already own.
    /// </summary>
    public static readonly CVarDef<bool> DrydockReadOnly =
        CVarDef.Create("triad.drydock.read_only", false, CVar.SERVERONLY);

    /// <summary>
    /// How many revisions keep their blob. Revision history itself is kept indefinitely; this only
    /// bounds the documents, and the current revision is never pruned regardless of this value.
    /// Zero or less prunes nothing at all, which is the safe direction to misconfigure.
    /// </summary>
    public static readonly CVarDef<int> DrydockKeepBlobs =
        CVarDef.Create("triad.drydock.keep_blobs", 3, CVar.SERVERONLY);

    /// <summary>
    /// The fraction of what was paid for a berth that selling it returns. A grant was paid nothing
    /// for and returns nothing whatever this says.
    /// </summary>
    public static readonly CVarDef<float> DrydockBerthRefund =
        CVarDef.Create("triad.drydock.berth_refund", 0.5f, CVar.SERVERONLY);

    /// <summary>
    /// How long a transfer offer stands before it expires and the ship leaves escrow. The clock is
    /// the persisted deadline on the offer row, so it keeps running through the recipient logging
    /// off and through a server restart, and both sides read the same remaining time from it.
    /// </summary>
    public static readonly CVarDef<int> DrydockTransferOfferSeconds =
        CVarDef.Create("triad.drydock.transfer_offer_seconds", 1800, CVar.SERVERONLY);

    /// <summary>
    /// Whether the drydock offers to import ships saved under the old shipyard save system. This is
    /// onboarding, not a feature: it exists to drain the legacy pool and is meant to be switched off
    /// once it has.
    /// </summary>
    public static readonly CVarDef<bool> DrydockImportEnabled =
        CVarDef.Create("triad.drydock.import_enabled", false, CVar.SERVERONLY);

    /// <summary>
    /// How many legacy ships one account may import, ever. A save file identifies a file and not a
    /// hull - the envelope carries no ship identity - so someone holding three saves of the same
    /// ship holds three legally loadable files. Burning the hash stops a file being imported twice;
    /// this is what stops the other two becoming ships. Counted against imports that were actually
    /// spent, so the rehearsal imports a non-enforcing server allows do not use anyone's budget up.
    /// </summary>
    public static readonly CVarDef<int> DrydockImportBudget =
        CVarDef.Create("triad.drydock.import_budget", 1, CVar.SERVERONLY);

    /// <summary>
    /// How many milliseconds of main-thread time one drydock store or retrieve may spend per tick.
    /// The store is elastic on purpose: making the one captain who pressed the button wait longer is
    /// free, making sixty other players wait is not. Lower is safer for everyone else and slower for
    /// the captain, so lower is the safe direction to misconfigure.
    ///
    /// <para>Zero or less turns slicing off completely, and it means no job at all rather than a job
    /// with a zero budget: the whole pipeline runs on the caller's own async path against the
    /// synchronous slice, exactly as it ran before the slicing change. A zero-budget job queue would
    /// instead never run anything, because the queue tests its own clock before it dequeues. This is
    /// the rollback lever on a pipeline whose deploy has no other one, and it is what the
    /// integration fixtures set.</para>
    ///
    /// <para>This does not bound the worst tick on its own. Four engine calls cannot be interrupted
    /// from content - the grid serialize, the round-trip validation load, the retrieve's grid load,
    /// and the dock - so the worst tick is this budget plus the longest of those.</para>
    /// </summary>
    public static readonly CVarDef<int> DrydockTickBudgetMs =
        CVarDef.Create("triad.drydock.tick_budget_ms", 2, CVar.SERVERONLY);

    /// <summary>
    /// How many entities a sliced drydock loop processes between stopwatch reads. Higher spends less
    /// time reading the clock and overshoots the budget by more; lower is the safe direction to
    /// misconfigure.
    /// </summary>
    public static readonly CVarDef<int> DrydockSliceStride =
        CVarDef.Create("triad.drydock.slice_stride", 32, CVar.SERVERONLY);

    /// <summary>
    /// How many real seconds a drydock job may go without advancing before it is cancelled and
    /// unwound. This is the release-mode net under the one rule a sliced pipeline cannot enforce at
    /// compile time, that every await goes through the slice: a missed wrapper leaves the job with no
    /// resume handle, which only asserts in debug and hangs forever in release, stranding a frozen
    /// ship on a private map. Raise it if a slow database makes it fire spuriously; zero disables it,
    /// which is the unsafe direction.
    /// </summary>
    public static readonly CVarDef<int> DrydockSliceWatchdogSeconds =
        CVarDef.Create("triad.drydock.slice_watchdog_seconds", 120, CVar.SERVERONLY);
    // End Triad
    // Triad: market data
    // The queue knobs mirror the admin log ones, which solve the same problem at production volume
    // on this server: adminlogs.queue_send_delay_seconds, queue_max, pre_round_queue_max and
    // drop_threshold. Defaults are deliberately identical so the two behave alike under load.

    /// <summary>
    /// Master switch. Off means nothing is recorded and no database work happens at all. On its own
    /// this does not create the tables; the migration does that whether or not this is set.
    /// </summary>
    public static readonly CVarDef<bool> MarketDataEnabled =
        CVarDef.Create("triad.market.enabled", false, CVar.SERVERONLY);

    /// <summary>
    /// Whether to record per-item line rows as well as transaction headers. Lines are the entire
    /// input to the price rollup, so turning this off leaves a Grafana-only feature with no in-game
    /// consumer. Separate from the master switch because line capture is the expensive half and
    /// wants to be disableable on its own if a sale on a full pallet ever costs real frame time.
    /// </summary>
    public static readonly CVarDef<bool> MarketDataLinesEnabled =
        CVarDef.Create("triad.market.lines_enabled", true, CVar.SERVERONLY);

    /// <summary>
    /// How long the writer waits between flushes.
    /// </summary>
    public static readonly CVarDef<float> MarketDataQueueSendDelay =
        CVarDef.Create("triad.market.queue_send_delay_seconds", 5f, CVar.SERVERONLY);

    /// <summary>
    /// Queue depth that forces a flush before the delay elapses.
    /// </summary>
    public static readonly CVarDef<int> MarketDataQueueMax =
        CVarDef.Create("triad.market.queue_max", 5000, CVar.SERVERONLY);

    /// <summary>
    /// Cap on the separate pre-round queue. Rows can be created before a round has an id, so they
    /// are held and stamped once it exists; character spawn sits exactly on that boundary.
    /// </summary>
    public static readonly CVarDef<int> MarketDataPreRoundQueueMax =
        CVarDef.Create("triad.market.pre_round_queue_max", 5000, CVar.SERVERONLY);

    /// <summary>
    /// Depth past which rows are dropped rather than queued. Dropping is the correct behaviour for
    /// telemetry: the tick must never stall for it. Drops are counted and logged, never silent.
    /// </summary>
    public static readonly CVarDef<int> MarketDataDropThreshold =
        CVarDef.Create("triad.market.drop_threshold", 20000, CVar.SERVERONLY);

    /// <summary>
    /// How long raw transactions, splits and lines are kept. The daily price rollup is permanent and
    /// unaffected. Zero or less disables the purge entirely and keeps everything.
    /// </summary>
    public static readonly CVarDef<int> MarketDataRetentionDays =
        CVarDef.Create("triad.market.retention_days", 90, CVar.SERVERONLY);
}
