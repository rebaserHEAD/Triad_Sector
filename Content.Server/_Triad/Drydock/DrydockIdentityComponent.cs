using System;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// A ship's persistent identity, carried by the grid itself. It rides the blob, so a ship that is
/// stored and retrieved comes back as the same ship rather than as a new one that merely looks
/// similar, and it survives a round boundary because it is grid state rather than round state.
///
/// <para>The implementation this was ported from stamped the id onto the shuttle deed instead. It
/// lives here for two reasons. The deed is an upstream component from another fork, so a field on
/// it conflicts on every pull from that fork. And a deed is a card that can be dropped, burned or
/// reissued, while identity has to outlive all three: the restore path explicitly mints a fresh
/// deed against an existing ship id, which only works if the id was never the deed's to hold.</para>
/// </summary>
[RegisterComponent]
public sealed partial class DrydockIdentityComponent : Component
{
    /// <summary>
    /// The id as text, and text on purpose. The engine serializer has no writer for
    /// <see cref="Guid"/>, so a Guid-typed data field is silently dropped rather than refused, and
    /// a stored ship would come back with no identity at all. That is the one field in this system
    /// whose loss cannot be recovered from, since nothing else on the grid says which hull it is.
    ///
    /// <para>Found by the serializability audit gate on the commit that introduced it, which is
    /// what the gate is for.</para>
    ///
    /// <para>The yaml key is pinned rather than derived from the field name, because this is a
    /// persisted format: renaming the field must not orphan every stored ship.</para>
    /// </summary>
    [DataField("shipId", required: true)]
    public string RawShipId = string.Empty;

    /// <summary>
    /// Not a data field, deliberately: the text above is what persists, and this is only how the
    /// rest of the code reads it. An unparseable value reads as <see cref="Guid.Empty"/>, which the
    /// store treats as "no identity yet" and mints over.
    /// </summary>
    public Guid ShipId
    {
        get => Guid.TryParse(RawShipId, out var parsed) ? parsed : Guid.Empty;
        set => RawShipId = value.ToString("D");
    }
}
