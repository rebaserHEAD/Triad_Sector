
using Robust.Shared.Configuration;

namespace Content.Shared._Triad.CCVar;

/// <summary>
/// Configuration variables for Triad features
/// </summary>
[CVarDefs]
public sealed class TriadCCVars
{
    /// <summary>
    ///     How much the ship cost will be. 0.3f = 30% of full appraisal
    /// </summary>
    public static readonly CVarDef<float> LoadShipPrice =
        CVarDef.Create("triad.load_ship_price", 0.3f, CVar.SERVER | CVar.REPLICATED);

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
    // End Triad
}
