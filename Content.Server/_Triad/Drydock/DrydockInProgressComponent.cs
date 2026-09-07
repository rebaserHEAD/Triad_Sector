namespace Content.Server._Triad.Drydock;

/// <summary>
/// Present on a grid for the span of a store attempt. While it is there, container insertion
/// targeting anything on the grid is refused, so nothing can be put aboard a ship during the
/// database write that happens while the grid is still live and docked.
///
/// <para><c>UnsavedComponent</c> is load-bearing: the marker is stamped before the grid is
/// serialized, so without it a retrieved ship comes back permanently mid-store, every container
/// aboard refusing insertion, hands included.</para>
/// </summary>
[RegisterComponent, UnsavedComponent]
public sealed partial class DrydockInProgressComponent : Component;
