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
    ///     dim beneath the live pass so stale reads as charted rather than confirmed. Runs
    ///     before the live pass because the two share the vertex buffer.
    /// </summary>
    private void TriadDrawChartContacts(DrawingHandleScreen handle, Matrix3x2 worldToView, MapId map)
    {
        if (_consoleEntity is not { } console)
            return;

        TriadDrawOutlines(handle, worldToView, TriadSensedContacts.GetChart(console, map), alpha: 0.3f);
    }

    private void TriadDrawSensedContacts(DrawingHandleScreen handle, Matrix3x2 worldToView, MapId map)
    {
        if (_consoleEntity is not { } console)
            return;

        // GetContacts only yields contacts whose outline has been derived (the recipe wire format
        // means shapes are rolled client-side, budgeted per frame), so a cold picture fills in
        // over a few frames rather than hitching one.
        TriadDrawOutlines(handle, worldToView, TriadSensedContacts.GetContacts(console, map), alpha: 0.8f);
    }

    private void TriadDrawOutlines(DrawingHandleScreen handle, Matrix3x2 worldToView,
        IEnumerable<SensedContactView> contacts, float alpha)
    {
        var cullBounds = new Box2(-64f, -64f, Size.X + 64f, Size.Y + 64f);

        foreach (var contact in contacts)
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

            handle.DrawPrimitives(DrawPrimitiveTopology.LineStrip, _triadHullVertsBuffer, contact.Color.WithAlpha(alpha));
        }
    }
}
