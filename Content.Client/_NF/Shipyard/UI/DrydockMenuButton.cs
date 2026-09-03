// Triad: drydock tab.
using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Maths;

namespace Content.Client._NF.Shipyard.UI;

/// <summary>
/// A button that drops a short menu: the row's three dots, the berth picker beside Store, and Buy
/// berth. The rare verbs live here so the row itself carries one button. Items are set by the
/// menu that owns the row and rebuilt on every state, so a stale entry cannot outlive its state.
/// </summary>
public sealed class DrydockMenuButton : Button
{
    /// <summary>One entry. A disabled entry stays visible with its detail text, since a tooltip would not fire on it.</summary>
    public sealed record Item(string Label, string? Detail, bool Enabled, Action? OnPressed);

    private readonly List<Item> _items = new();

    /// <summary>Open the menu flush with the button's right edge rather than its left.</summary>
    public bool AlignRight { get; set; }

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
                BackgroundColor = Color.FromHex("#141414"),
                BorderColor = Color.FromHex("#5a5a5a"),
                BorderThickness = new Thickness(1),
            },
        };
        var list = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, MinWidth = 220 };
        panel.AddChild(list);
        popup.AddChild(panel);

        foreach (var item in _items)
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

            var pressed = item.OnPressed;
            entry.OnPressed += _ =>
            {
                popup.Close();
                pressed?.Invoke();
            };
            list.AddChild(entry);
        }

        // Added by hand rather than OpenAtMouse, which is the only path the engine orphans for
        // us, so the hide hook takes it out of the modal root again.
        popup.OnPopupHide += () => popup.Orphan();
        root.ModalRoot.AddChild(popup);

        var width = Math.Max(Width, 220f);
        var origin = AlignRight
            ? new Vector2(GlobalPosition.X + Width - width, GlobalPosition.Y + Height)
            : new Vector2(GlobalPosition.X, GlobalPosition.Y + Height);
        popup.Open(UIBox2.FromDimensions(origin, new Vector2(width, 0)));
    }
}
