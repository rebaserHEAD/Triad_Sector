namespace Content.Server.Wires;

public sealed partial class WiresSystem
{
    /// <summary>
    /// Refills <see cref="WiresComponent.Statuses"/> from the wires and pushes the panel state, as every change to the wires'
    /// data does (<see cref="UpdateUserInterface"/>). Opening the panel pushes nothing (<see cref="OnInteractUsing"/>), so a
    /// wire list built by anything but map init shows an empty panel until this runs.
    /// </summary>
    public void RefreshUserInterface(EntityUid uid, WiresComponent? wires = null)
    {
        UpdateUserInterface(uid, wires);
    }
}
