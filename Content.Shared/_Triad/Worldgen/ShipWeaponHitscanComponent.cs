// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.GameStates;

namespace Content.Shared._Triad.Worldgen;

/// <summary>
///     Marks a hitscan prototype as ship-grade, opting its beam into data-space blocking on
///     described-but-unmaterialized debris. The hitscan cousin of Mono's
///     ShipWeaponProjectileComponent gate, and scoped the same way: ship artillery beams cross
///     kilometres of belt and must stop on rocks the radar paints; small-arms fire never does
///     and never pays the check.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class ShipWeaponHitscanComponent : Component;
