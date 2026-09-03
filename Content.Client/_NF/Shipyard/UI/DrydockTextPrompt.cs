// Triad: drydock tab.
using System.Numerics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Maths;

namespace Content.Client._NF.Shipyard.UI;

/// <summary>
/// One line of text and a button that stays locked until the text passes. The sale confirmation
/// (type the ship's name) and the rename (a name in the allowed shape) are both this. The
/// instructions sit in the field as its placeholder and vanish on the first keystroke; the
/// server applies the same rule again on receipt.
/// </summary>
public sealed class DrydockTextPrompt : DefaultWindow
{
    private readonly LineEdit _field;
    private readonly Button _confirm;
    private readonly Label? _counter;
    private readonly Func<string, bool> _accepts;
    private readonly int? _maxLength;

    public DrydockTextPrompt(string title, string? body, string placeholder, string confirmLabel, Func<string, bool> accepts, int? maxLength, Action<string> onConfirm)
    {
        Title = title;
        SetSize = new Vector2(420, 0);
        _accepts = accepts;
        _maxLength = maxLength;

        var column = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, Margin = new Thickness(6) };

        if (body != null)
            column.AddChild(new Label { Text = body, Margin = new Thickness(0, 0, 0, 8) });

        var row = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Horizontal, HorizontalExpand = true };
        _field = new LineEdit { PlaceHolder = placeholder, HorizontalExpand = true };
        row.AddChild(_field);
        if (maxLength != null)
        {
            _counter = new Label { Modulate = Color.FromHex("#999999"), Margin = new Thickness(8, 0, 0, 0) };
            row.AddChild(_counter);
        }
        column.AddChild(row);

        var buttons = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Horizontal, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HAlignment.Right };
        var cancel = new Button { Text = Loc.GetString("shipyard-console-prompt-cancel"), StyleClasses = { "ButtonSquare" }, MinWidth = 90 };
        _confirm = new Button { Text = confirmLabel, StyleClasses = { "ButtonSquare", "ButtonCaution" }, MinWidth = 90, Margin = new Thickness(4, 0, 0, 0), Disabled = true };
        buttons.AddChild(cancel);
        buttons.AddChild(_confirm);
        column.AddChild(buttons);

        Contents.AddChild(column);

        _field.OnTextChanged += _ => Refresh();
        _field.OnTextEntered += _ =>
        {
            if (!_confirm.Disabled)
                Submit(onConfirm);
        };
        cancel.OnPressed += _ => Close();
        _confirm.OnPressed += _ => Submit(onConfirm);

        Refresh();
    }

    protected override void Opened()
    {
        base.Opened();
        _field.GrabKeyboardFocus();
    }

    private void Refresh()
    {
        var text = _field.Text;
        _confirm.Disabled = !_accepts(text);
        if (_counter != null && _maxLength is { } max)
        {
            _counter.Text = Loc.GetString("shipyard-console-rename-counter", ("count", text.Length), ("max", max));
            _counter.Modulate = text.Length > max ? Color.FromHex("#ff6a6a") : Color.FromHex("#999999");
        }
    }

    private void Submit(Action<string> onConfirm)
    {
        onConfirm(_field.Text);
        Close();
    }
}
