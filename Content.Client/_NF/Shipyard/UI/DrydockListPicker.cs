// Triad: drydock tab.
using System.Numerics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Maths;

namespace Content.Client._NF.Shipyard.UI;

/// <summary>
/// A small window that lists choices, one button each, greyed with a reason when a choice cannot
/// be taken. The transfer picker (captains online) and the move picker (empty berths that fit)
/// are both this. Picking one fires its action and closes the window; the server checks the
/// choice again, so a list that went stale between the state and the click is a refusal, never
/// a wrong outcome.
/// </summary>
public sealed class DrydockListPicker : DefaultWindow
{
    public sealed record Item(string Label, string? Detail, bool Enabled, Action OnPicked);

    public DrydockListPicker(string title, string hint, string emptyText, IReadOnlyList<Item> items)
    {
        Title = title;
        SetSize = new Vector2(380, 320);

        var list = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, Margin = new Thickness(4) };
        list.AddChild(new Label
        {
            Text = hint,
            Modulate = Color.FromHex("#999999"),
            Margin = new Thickness(6, 0, 6, 8),
        });

        if (items.Count == 0)
            list.AddChild(new Label { Text = emptyText, Margin = new Thickness(6, 0) });

        foreach (var item in items)
        {
            var entry = new ContainerButton
            {
                StyleClasses = { ContainerButton.StyleClassButton },
                Disabled = !item.Enabled,
                HorizontalExpand = true,
            };
            var line = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Horizontal, Margin = new Thickness(10, 6), HorizontalExpand = true };
            line.AddChild(new Label { Text = item.Label, HorizontalExpand = true, Modulate = item.Enabled ? Color.White : Color.FromHex("#777777") });
            if (item.Detail != null)
                line.AddChild(new Label { Text = item.Detail, Modulate = Color.FromHex("#999999"), Margin = new Thickness(12, 0, 0, 0) });
            entry.AddChild(line);

            var picked = item.OnPicked;
            entry.OnPressed += _ =>
            {
                picked();
                Close();
            };
            list.AddChild(entry);
        }

        var scroll = new ScrollContainer { VerticalExpand = true, HorizontalExpand = true, HScrollEnabled = false };
        scroll.AddChild(list);
        Contents.AddChild(scroll);
    }
}
