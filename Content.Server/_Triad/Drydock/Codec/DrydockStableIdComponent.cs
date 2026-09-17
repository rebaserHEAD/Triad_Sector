namespace Content.Server._Triad.Drydock.Codec;

/// <summary>
/// The name an entity answers to inside a stored image, so one row can point at another.
///
/// <para>The engine's own answer is a document-local number: <c>EntitySerializer.YamlUidMap</c>
/// hands out an entity's position in the one file being written, and
/// <c>EntityDeserializer.UidMap</c> resolves it on the way back. That number means nothing outside
/// the document that produced it, which is exactly what rows cannot use, because a modify operation
/// addresses one entity without reading its neighbours.</para>
///
/// <para>Also not a <see cref="Robust.Shared.GameObjects.NetEntity"/>: that is minted per round and
/// is gone by the next one.</para>
/// </summary>
[RegisterComponent]
public sealed partial class DrydockStableIdComponent : Component
{
    /// <summary>
    /// Assigned once, at the first capture that sees the entity, and never reused. The sequence it
    /// is drawn from lives on the hull, not on an image row: one climbing counter per stored ship,
    /// shared by every image, revision and fork of that ship, so an id is unambiguous across all of
    /// them. A test counter that restarts at 1 per image is a test convenience, not the contract.
    ///
    /// <para>The yaml key is pinned, because renaming the field would orphan every reference in
    /// every stored row.</para>
    /// </summary>
    [DataField("stableId", required: true)]
    public long Value;
}
