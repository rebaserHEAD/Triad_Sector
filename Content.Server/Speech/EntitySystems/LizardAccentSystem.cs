using System.Text.RegularExpressions;
using Content.Server.Speech.Components;
using Content.Server._Triad.Speech.EntitySystems; // Triad: AccentHelpers relocated to _Triad

namespace Content.Server.Speech.EntitySystems;

public sealed class LizardAccentSystem : EntitySystem
{
    private static readonly Regex RegexLowerS = new("s+");
    private static readonly Regex RegexUpperS = new("S+");
    private static readonly Regex RegexInternalX = new(@"(\w)x");
    private static readonly Regex RegexLowerEndX = new(@"\bx([\-|r|R]|\b)");
    private static readonly Regex RegexUpperEndX = new(@"\bX([\-|r|R]|\b)");
    // Triad: soft c (before e/i/y) also hisses -- city -> ssity, nice -> nissse. Runs before the s+ pass
    // so the s it produces gets lengthened too. Hard c (cat, clone) is a /k/ sound and is left alone.
    private static readonly Regex RegexSoftC = new(@"c(?=[eiy])", RegexOptions.IgnoreCase);

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<LizardAccentComponent, AccentGetEvent>(OnAccent);
    }

    private void OnAccent(EntityUid uid, LizardAccentComponent component, AccentGetEvent args)
    {
        var message = args.Message;

        // Triad: soft c -> s before the hiss pass, so "city" -> "ssity", "nice" -> "nissse".
        message = AccentHelpers.ReplaceCasePreserving(message, RegexSoftC, "s");
        // hissss
        message = RegexLowerS.Replace(message, "sss");
        // hiSSS -- Triad: a lone leading capital S is ordinary capitalization (Steal, Silver), not a
        // shout, so only the first S stays capital (Sss); a run of 2+ capital S is a genuine shout and
        // stays SSS. Without this, "Something" came out "SSSomething".
        message = RegexUpperS.Replace(message, m => m.Value.Length > 1 ? "SSS" : "Sss");
        // ekssit
        message = RegexInternalX.Replace(message, "$1kss");
        // ecks
        message = RegexLowerEndX.Replace(message, "ecks$1");
        // eckS
        message = RegexUpperEndX.Replace(message, "ECKS$1");

        args.Message = message;
    }
}
