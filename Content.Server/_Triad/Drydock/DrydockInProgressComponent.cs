namespace Content.Server._Triad.Drydock;

/// <summary>
/// Present on a grid for the span of a store attempt, as the re-entrancy sentinel and nothing else.
/// A store yields - on the database, and now on the tick budget for as long as the pipeline lasts -
/// so a second request for the same grid would otherwise run the whole preparation again over a ship
/// that is already halfway into a berth and file a second revision of it.
///
/// <para>It used to do a second job, refusing container insertion anywhere on the grid, through the
/// only unfiltered subscription to the insertion attempt in the solution. That is gone: from the
/// freeze onwards the ship sits on a private paused map with nobody aboard, so there is nothing left
/// that could insert into it, and every container insertion on the server was paying two component
/// lookups for the length of somebody else's store.</para>
///
/// <para><c>UnsavedComponent</c> is load-bearing. The marker is on the grid while its image is
/// written, and the store writes no row for an unsaved component (<c>DrydockStoreSession.WriteEntity</c>),
/// so a retrieved ship never carries it. Saved, it would come back on the ship and the sentinel gate
/// at the top of the store would answer "already in progress" to every future store of that hull,
/// permanently.</para>
/// </summary>
[RegisterComponent, UnsavedComponent]
public sealed partial class DrydockInProgressComponent : Component;
