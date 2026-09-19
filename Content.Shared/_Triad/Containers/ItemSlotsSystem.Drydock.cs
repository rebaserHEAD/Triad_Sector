namespace Content.Shared.Containers.ItemSlots;

/// <summary>
/// The drydock's load restores a stored slot into the live instance a component re-added, so the component that holds
/// a reference to that instance keeps it. <see cref="ItemSlot.CopyFrom"/> is this system's alone.
/// </summary>
public sealed partial class ItemSlotsSystem
{
    /// <summary>Copies <paramref name="stored"/> into <paramref name="live"/>, in place.</summary>
    public void RestoreSlot(ItemSlot live, ItemSlot stored)
    {
        live.CopyFrom(stored);
    }
}
