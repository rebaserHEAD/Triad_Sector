// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Client._Triad.Worldgen;
using Robust.Client.Graphics;
using Robust.Shared.Map;

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

    /// <summary>
    ///     The chart underlay: last-known rocks the server is not currently vouching for, drawn
    ///     dim beneath the live pass so stale reads as charted rather than confirmed. Runs its
    ///     own full loop before the live loop because the two share the vertex buffer.
    /// </summary>
    private void TriadDrawChartContacts(DrawingHandleScreen handle, Matrix3x2 worldToView, MapId map)
    {
        if (_consoleEntity is not { } console)
            return;

        var cullBounds = new Box2(-64f, -64f, Size.X + 64f, Size.Y + 64f);

        foreach (var contact in TriadSensedContacts.GetChart(console, map))
        {
            var viewPos = Vector2.Transform(contact.MapPosition, worldToView);
            if (!cullBounds.Contains(viewPos))
                continue;

            _triadHullVertsBuffer.Clear();
            foreach (var outlineVert in contact.Outline)
            {
                _triadHullVertsBuffer.Add(Vector2.Transform(contact.MapPosition + outlineVert, worldToView));
            }

            _triadHullVertsBuffer.Add(_triadHullVertsBuffer[0]);

            handle.DrawPrimitives(DrawPrimitiveTopology.LineStrip, _triadHullVertsBuffer, contact.Color.WithAlpha(0.3f));
        }
    }

    private void TriadDrawSensedContacts(DrawingHandleScreen handle, Matrix3x2 worldToView)
    {
        if (_consoleEntity is not { } console)
            return;

        var cullBounds = new Box2(-64f, -64f, Size.X + 64f, Size.Y + 64f);

        // GetContacts only yields contacts whose outline has been derived (the recipe wire format
        // means shapes are rolled client-side, budgeted per frame), so a cold picture fills in
        // over a few frames rather than hitching one.
        foreach (var contact in TriadSensedContacts.GetContacts(console))
        {
            var viewPos = Vector2.Transform(contact.MapPosition, worldToView);
            if (!cullBounds.Contains(viewPos))
                continue;

            _triadHullVertsBuffer.Clear();
            foreach (var outlineVert in contact.Outline)
            {
                _triadHullVertsBuffer.Add(Vector2.Transform(contact.MapPosition + outlineVert, worldToView));
            }

            _triadHullVertsBuffer.Add(_triadHullVertsBuffer[0]);

            handle.DrawPrimitives(DrawPrimitiveTopology.LineStrip, _triadHullVertsBuffer, contact.Color.WithAlpha(0.8f));
        }
    }
}
