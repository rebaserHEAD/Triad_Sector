// Triad: drydock tab.
using System.Text;
using Robust.Shared.Maths;
using Robust.Shared.Utility;

namespace Content.Client._NF.Shipyard.UI;

/// <summary>
/// The one place the drydock tab writes a clock, a class name or a weighted name, so the card, the
/// rows, the alerts and the prompts all agree on the shape of the same fact.
/// </summary>
internal static class DrydockText
{
    public static readonly Color Dim = Color.FromHex("#999999");
    public static readonly Color Empty = Color.FromHex("#777777");
    public static readonly Color Disabled = Color.FromHex("#666666");
    public static readonly Color Warning = Color.FromHex("#d9a441");
    public static readonly Color Incoming = Color.FromHex("#8fb4dc");

    // The two marked frames: amber for anything in escrow (a row, the alert), blue for what an
    // offer would bring in.
    public static readonly Color AmberBorder = Color.FromHex("#8a6a3a");
    public static readonly Color AmberFill = Color.FromHex("#2a2418");
    public static readonly Color BlueBorder = Color.FromHex("#5b86b8");
    public static readonly Color BlueFill = Color.FromHex("#1a2430");

    /// <summary>The caret the two dropdown buttons wear, so a label reads as a menu and not a verb.</summary>
    public const string Caret = " ▾";

    /// <summary>How long an offer has left: whole minutes while there are any, seconds after that.</summary>
    public static string Left(int seconds)
    {
        return seconds >= 60
            ? Loc.GetString("shipyard-console-transfer-minutes-left", ("minutes", (int)Math.Ceiling(seconds / 60.0)))
            : Loc.GetString("shipyard-console-transfer-seconds-left", ("seconds", Math.Max(0, seconds)));
    }

    /// <summary>How long a ship has been out: "48 m" under an hour, "1 h 05 m" past it.</summary>
    public static string Out(int minutes)
    {
        minutes = Math.Max(0, minutes);
        return minutes < 60
            ? Loc.GetString("shipyard-console-time-minutes", ("minutes", minutes))
            : Loc.GetString("shipyard-console-time-hours", ("hours", minutes / 60), ("minutes", (minutes % 60).ToString("00")));
    }

    /// <summary>
    /// A size class as a label: the stored text with a space before each inner capital, so the
    /// enum's SuperCapital reads as Super Capital. Anything unparseable is shown as it came, since a
    /// row filed by an older build can name a class this build does not have.
    /// </summary>
    public static string Class(string? sizeClass)
    {
        if (string.IsNullOrEmpty(sizeClass))
            return "?";

        var sb = new StringBuilder(sizeClass.Length + 2);
        for (var i = 0; i < sizeClass.Length; i++)
        {
            var c = sizeClass[i];
            if (i > 0 && char.IsUpper(c) && char.IsLower(sizeClass[i - 1]))
                sb.Append(' ');
            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>Appends text in the bold face at the size in force.</summary>
    public static void AddBold(this FormattedMessage message, string text)
    {
        message.PushTag(new MarkupNode("bold", null, null));
        message.AddText(text);
        message.Pop();
    }

    /// <summary>Appends text in the given colour.</summary>
    public static void AddColored(this FormattedMessage message, string text, Color color)
    {
        message.PushColor(color);
        message.AddText(text);
        message.Pop();
    }

    /// <summary>Appends bold text at a point size of its own; the card's ship name is the one user.</summary>
    public static void AddBoldSized(this FormattedMessage message, string text, int size)
    {
        message.PushTag(new MarkupNode("font", null, new Dictionary<string, MarkupParameter> { ["size"] = new MarkupParameter((long)size) }));
        message.AddBold(text);
        message.Pop();
    }
}
