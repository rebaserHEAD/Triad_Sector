// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server.Worldgen.Components.Debris;
using Content.Shared._Triad.Worldgen;
using Robust.Shared.Prototypes;

namespace Content.Server._Triad.Worldgen.Cells;

/// <summary>
///     The one place a prototype's blob parameters become a <see cref="SensedProtoRecipe"/>.
///     Describe sizing, the contact channel's legend, the collision tile cache and the tests all
///     roll shapes from a recipe minted here, so a builder field that matters to the roll gets
///     picked up everywhere or nowhere. Game thread only: the by-name component lookup resolves
///     the factory through thread-local IoC.
/// </summary>
public static class DebrisRecipe
{
    /// <summary>Null for prototypes without a blob builder, which never shape a record.</summary>
    public static SensedProtoRecipe? TryFrom(EntityPrototype proto)
    {
        if (!proto.TryGetComponent<BlobFloorPlanBuilderComponent>("BlobFloorPlanBuilder", out var blob))
            return null;

        // Floor of one because Roll rejects a tileset count below it. A proto shipping an empty
        // tileset still rolls a shape for radar here; it fails at materialize, where the missing
        // tiles actually matter.
        return new SensedProtoRecipe(proto.ID, blob.Radius, blob.FloorPlacements, blob.BlobDrawProb,
            Math.Max(1, blob.FloorTileset.Count));
    }
}
