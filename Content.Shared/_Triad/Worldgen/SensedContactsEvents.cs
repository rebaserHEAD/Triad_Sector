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

[Serializable, NetSerializable]
public readonly record struct SensedContactData(int Id, Vector2 MapPosition, Vector2[] Hull, Color Color);
