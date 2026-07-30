
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
    /// Default page size for the admin audit-log viewer. Hard-capped server-side at 500.
    /// </summary>
    public static readonly CVarDef<int> TamperAdminPageSize =
        CVarDef.Create("triad.tamper_admin_page_size", 50, CVar.SERVERONLY);

    /// <summary>
    /// F14 fix: directory holding the on-disk PEM files for the tamper-protection signing keys.
    /// The DB stores only the public key and a KeyId; the private key lives in a file named
    /// {KeyId}.pem inside this directory. Default is relative to the server's working directory.
    /// Admins should ensure the directory has restrictive permissions (0700 on Unix; ACL
    /// equivalent on Windows). The keystore creates the directory with 0700 on first use.
    /// </summary>
    public static readonly CVarDef<string> TamperSigningKeysDir =
        CVarDef.Create("triad.tamper_signing_keys_dir", "./triad-signing-keys", CVar.SERVERONLY);

    public static readonly CVarDef<bool> UseNightVisionColor =
        CVarDef.Create("triad.use_night_vision_color", false, CVar.CLIENTONLY | CVar.ARCHIVE, "If a custom night vision color should be used instead of the default.");

    public static readonly CVarDef<string> NightVisionColor =
        CVarDef.Create("triad.night_vision_color", "#00FF00", CVar.CLIENTONLY | CVar.ARCHIVE, "The tint/phosphor color of night vision.");

    // Triad: worldgen sensed tier
    /// <summary>
    /// Master switch for the sensed worldgen tier (describe service, radar contacts, JIT materialization). Off = stock burst-spawn path.
    /// </summary>
    public static readonly CVarDef<bool> WorldgenSensedEnabled =
        CVarDef.Create("triad.worldgen.sensed_enabled", true, CVar.SERVERONLY);

    /// <summary>
    /// Radius around anything present (ship, person, station) in which cells are described, making
    /// debris radar-visible before any entity exists. Sensors do not extend this; they only decide
    /// how much of it a given console is shown. Defaults to the stock radar range, since a console
    /// can only paint what has been described and anything shorter puts a hard ring on the screen.
    /// </summary>
    public static readonly CVarDef<float> WorldgenSensedRange =
        CVarDef.Create("triad.worldgen.sensed_range", 3072f, CVar.SERVERONLY);

    public static readonly CVarDef<float> WorldgenDescribeBudgetMs =
        CVarDef.Create("triad.worldgen.describe_budget_ms", 1.5f, CVar.SERVERONLY);

    public static readonly CVarDef<float> WorldgenMaterializeBudgetMs =
        CVarDef.Create("triad.worldgen.materialize_budget_ms", 2f, CVar.SERVERONLY);

    /// <summary>
    /// Dormant debris within this range of any loader spawns immediately, ignoring the budget.
    /// </summary>
    public static readonly CVarDef<float> WorldgenMaterializePanicRange =
        CVarDef.Create("triad.worldgen.materialize_panic_range", 96f, CVar.SERVERONLY);

    /// <summary>
    /// Seconds of loader velocity lead applied to the describe sweep center.
    /// </summary>
    public static readonly CVarDef<float> WorldgenDescribeLeadS =
        CVarDef.Create("triad.worldgen.describe_lead_s", 3f, CVar.SERVERONLY);

    public static readonly CVarDef<int> WorldgenContactAddsPerPoll =
        CVarDef.Create("triad.worldgen.contact_adds_per_poll", 256, CVar.SERVERONLY);
}
