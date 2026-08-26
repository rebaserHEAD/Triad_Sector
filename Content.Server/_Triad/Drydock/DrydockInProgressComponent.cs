namespace Content.Server._Triad.Drydock;

/// <summary>
/// Present on a grid for the span of a store attempt. While it is there, container insertion
/// targeting anything on the grid is refused, so nothing can be put aboard a ship during the
/// database write that happens while the grid is still live and docked.
///
/// <para><c>UnsavedComponent</c> is load-bearing, and was found the hard way in the implementation
/// this is ported from. The marker is stamped before the grid is serialized, because that is the
/// window it exists to guard, so without the attribute it rides the blob and a retrieved ship comes
/// back permanently mid-store: every container aboard refuses insertion, hands included, and
/// nothing on the ship can be picked up.</para>
/// </summary>
[RegisterComponent, UnsavedComponent]
public sealed partial class DrydockInProgressComponent : Component;
