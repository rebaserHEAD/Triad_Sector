// Triad: the drydock's round-end sweep holds the restart. The one call in RestartRound is marked
// in the upstream file; the dependency behind it lives here so that file gains no field.
using Content.Server._Triad.Drydock;

namespace Content.Server.GameTicking;

public sealed partial class GameTicker
{
    [Dependency] private DrydockSystem _drydock = default!;
}
