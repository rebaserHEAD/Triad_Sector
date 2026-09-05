// Triad: drydock tab.
using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Maths;

namespace Content.Client._NF.Shipyard.UI;

/// <summary>
/// A button that drops a short menu: the row's three dots, Store in #N, and Buy berth. The rare
/// verbs live here so the row itself carries one button. Items are set by the menu that owns the
/// row and rebuilt on every state, so a stale entry cannot outlive its state.
/// </summary>
public sealed class DrydockMenuButton : Button
{
    /// <summary>
    /// One entry: a label on the left, a detail on the right, and whether it can be taken. A
    /// disabled entry stays visible with its detail saying why, since a tooltip would not fire on
    /// it. <paramref name="DividerAbove"/> draws the rule that separates the ship verbs from the
    /// berth verbs.
    /// </summary>
    public sealed record Item(string Label, string? Detail, bool Enabled, Action? OnPressed, bool DividerAbove = false);

    private const float MenuWidth = 200f;

    private static readonly Color MenuBackground = Color.FromHex("#141414");
    private static readonly Color MenuBorder = Color.FromHex("#5a5a5a");
    private static readonly Color RowRule = Color.FromHex("#262626");
    private static readonly Color SectionRule = Color.FromHex("#3a3a3a");

    private readonly List<Item> _items = new();

    /// <summary>Open the menu flush with the button's right edge rather than its left.</summary>
    public bool AlignRight { get; set; }

    /// <summary>
    /// A line of warning drawn under the entries, or null. Store uses it when one of the berths
    /// listed is where an offer on the tab would land.
    /// </summary>
    public string? Note { get; set; }

    public DrydockMenuButton()
    {
        OnPressed += _ => OpenMenu();
    }

    public void SetItems(IEnumerable<Item> items)
    {
        _items.Clear();
        _items.AddRange(items);
        Disabled = _items.Count == 0;
    }

    private void OpenMenu()
    {
        if (_items.Count == 0 || Root is not { } root)
            return;

        var popup = new Popup();
        var panel = new PanelContainer
        {
            PanelOverride = new StyleBoxFlat
            {
                BackgroundColor = MenuBackground,
                BorderColor = MenuBorder,
                BorderThickness = new Thickness(1),
            },
        };
        var list = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, MinWidth = MenuWidth };
        panel.AddChild(list);
        popup.AddChild(panel);

        for (var i = 0; i < _items.Count; i++)
        {
            var item = _items[i];

            // A rule between entries; the section rule is heavier and stands in for the row one.
            if (i > 0)
                list.AddChild(Rule(item.DividerAbove ? SectionRule : RowRule));

            var entry = new ContainerButton
            {
                StyleClasses = { ContainerButton.StyleClassButton },
                Disabled = !item.Enabled,
                HorizontalExpand = true,
            };
            var text = item.Enabled ? Color.White : DrydockText.Disabled;
            var detail = item.Enabled ? DrydockText.Dim : DrydockText.Disabled;
            var line = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Horizontal, Margin = new Thickness(10, 7), HorizontalExpand = true };
            line.AddChild(new Label { Text = item.Label, HorizontalExpand = true, Modulate = text });
            if (item.Detail != null)
                line.AddChild(new Label { Text = item.Detail, Modulate = detail, Margin = new Thickness(12, 0, 0, 0) });
            entry.AddChild(line);

            var pressed = item.OnPressed;
            entry.OnPressed += _ =>
            {
                popup.Close();
                pressed?.Invoke();
            };
            list.AddChild(entry);
        }

        if (Note != null)
        {
            list.AddChild(new Label
            {
                Text = Note,
                Modulate = DrydockText.Warning,
                Margin = new Thickness(10, 6),
            });
        }

        // Added by hand rather than OpenAtMouse, which is the only path the engine orphans for
        // us, so the hide hook takes it out of the modal root again.
        popup.OnPopupHide += () => popup.Orphan();
        root.ModalRoot.AddChild(popup);

        var width = Math.Max(Width, MenuWidth);
        var origin = AlignRight
            ? new Vector2(GlobalPosition.X + Width - width, GlobalPosition.Y + Height)
            : new Vector2(GlobalPosition.X, GlobalPosition.Y + Height);
        popup.Open(UIBox2.FromDimensions(origin, new Vector2(width, 0)));
    }

    private static PanelContainer Rule(Color color)
    {
        return new PanelContainer
        {
            PanelOverride = new StyleBoxFlat { BackgroundColor = color },
            MinHeight = 1,
            HorizontalExpand = true,
        };
    }
}
