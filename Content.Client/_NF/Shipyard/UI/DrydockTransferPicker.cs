// Triad: drydock tab.
using System.Linq;
using System.Numerics;
using Content.Shared._NF.Shipyard.BUI;
using Content.Shared._Triad.ShipSize;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Maths;

namespace Content.Client._NF.Shipyard.UI;

/// <summary>
/// The transfer picker: the captains online right now, one button each, greyed when none of
/// their free berths would take the ship. Picking one sends the offer; the server checks all of
/// this again, so a captain who logged off between the state and the click is a refusal, not a
/// stranded ship.
/// </summary>
public sealed class DrydockTransferPicker : DefaultWindow
{
    public event Action<Guid>? OnPicked;

    public DrydockTransferPicker(string shipName, string? shipClass, IReadOnlyList<DrydockCaptainInfo> captains)
    {
        Title = Loc.GetString("shipyard-console-transfer-picker-title", ("ship", shipName));
        SetSize = new Vector2(380, 320);

        var list = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, Margin = new Thickness(4) };
        list.AddChild(new Label
        {
            Text = Loc.GetString("shipyard-console-transfer-picker-hint"),
            Modulate = Color.FromHex("#999999"),
            Margin = new Thickness(6, 0, 6, 8),
        });

        if (captains.Count == 0)
            list.AddChild(new Label { Text = Loc.GetString("shipyard-console-transfer-picker-empty"), Margin = new Thickness(6, 0) });

        foreach (var captain in captains.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var fits = captain.FreeBerthClasses.Any(berthClass => ClassFits(shipClass, berthClass));
            var entry = new ContainerButton
            {
                StyleClasses = { ContainerButton.StyleClassButton },
                Disabled = !fits,
                HorizontalExpand = true,
            };
            var line = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Horizontal, Margin = new Thickness(10, 6), HorizontalExpand = true };
            line.AddChild(new Label { Text = captain.Name, HorizontalExpand = true, Modulate = fits ? Color.White : Color.FromHex("#777777") });
            line.AddChild(new Label
            {
                Text = fits ? string.Join(", ", captain.FreeBerthClasses.Distinct()) : Loc.GetString("shipyard-console-transfer-picker-no-berth"),
                Modulate = Color.FromHex("#999999"),
                Margin = new Thickness(12, 0, 0, 0),
            });
            entry.AddChild(line);

            var userId = captain.UserId;
            entry.OnPressed += _ =>
            {
                OnPicked?.Invoke(userId);
                Close();
            };
            list.AddChild(entry);
        }

        var scroll = new ScrollContainer { VerticalExpand = true, HorizontalExpand = true, HScrollEnabled = false };
        scroll.AddChild(list);
        Contents.AddChild(scroll);
    }

    /// <summary>
    /// The same ladder the server's berth check walks: a berth takes any hull of its class or
    /// smaller. Unparseable text on either side is "does not fit", never a crash.
    /// </summary>
    private static bool ClassFits(string? shipClass, string berthClass)
    {
        return Enum.TryParse<ShipSizeClass>(shipClass, out var ship)
            && Enum.TryParse<ShipSizeClass>(berthClass, out var berth)
            && berth >= ship;
    }
}
