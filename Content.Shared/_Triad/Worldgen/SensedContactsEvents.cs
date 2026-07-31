// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Robust.Shared.Serialization;

namespace Content.Shared._Triad.Worldgen;

[Serializable, NetSerializable]
public sealed class RequestSensedContactsEvent : EntityEventArgs
{
    public NetEntity Console;

    public RequestSensedContactsEvent(NetEntity console)
    {
        Console = console;
    }
}

[Serializable, NetSerializable]
public sealed class SensedContactsDeltaEvent : EntityEventArgs
{
    public NetEntity Console;

    /// <summary>
    /// Client clears its set for this console before applying.
    /// </summary>
    public bool FullReset;

    public List<SensedContactData> Adds;
    public List<int> Removes;

    public SensedContactsDeltaEvent(NetEntity console, bool fullReset, List<SensedContactData> adds, List<int> removes)
    {
        Console = console;
        FullReset = fullReset;
        Adds = adds;
        Removes = removes;
    }
}

/// <summary>
///     One dormant rock as the radar draws it. <paramref name="Hull"/> is in grid-local tile units
///     relative to <paramref name="MapPosition"/>.
///     Deliberately <see cref="Vector2i"/> and not <see cref="Vector2"/>: outline vertices are tile
///     corners, so they are always integers, and NetSerializer writes an int as a zigzag varint but
///     a float as four fixed bytes. Every coordinate a shipping prototype produces fits well inside
///     one byte, so this is 2 bytes per vertex instead of 8. That is what makes a faithful outline
///     affordable on the wire at all.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct SensedContactData(int Id, Vector2 MapPosition, Vector2i[] Hull, Color Color);
