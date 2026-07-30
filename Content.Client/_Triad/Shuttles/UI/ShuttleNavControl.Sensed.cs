using System.Numerics;
using Content.Client._Triad.Worldgen;
using Robust.Client.Graphics;

namespace Content.Client.Shuttles.UI;

public partial class ShuttleNavControl // Triad
{
    private SensedContactsSystem? _triadSensedContacts;

    private SensedContactsSystem TriadSensedContacts => _triadSensedContacts ??= EntManager.System<SensedContactsSystem>();

    // reused across frames to avoid a per-contact allocation while drawing hull outlines
    private readonly List<Vector2> _triadHullVertsBuffer = new();

    private void TriadRequestSensedContacts()
    {
        if (_consoleEntity is not { } console)
            return;

        TriadSensedContacts.RequestContacts(console);
    }

    private void TriadDrawSensedContacts(DrawingHandleScreen handle, Matrix3x2 worldToView)
    {
        if (_consoleEntity is not { } console)
            return;

        var cullBounds = new Box2(-64f, -64f, Size.X + 64f, Size.Y + 64f);

        foreach (var contact in TriadSensedContacts.GetContacts(console))
        {
            var viewPos = Vector2.Transform(contact.MapPosition, worldToView);
            if (!cullBounds.Contains(viewPos))
                continue;

            if (contact.Hull.Length == 0)
                continue;

            _triadHullVertsBuffer.Clear();
            foreach (var hullVert in contact.Hull)
            {
                _triadHullVertsBuffer.Add(Vector2.Transform(contact.MapPosition + hullVert, worldToView));
            }

            _triadHullVertsBuffer.Add(_triadHullVertsBuffer[0]);

            handle.DrawPrimitives(DrawPrimitiveTopology.LineStrip, _triadHullVertsBuffer, contact.Color.WithAlpha(0.8f));
        }
    }
}
