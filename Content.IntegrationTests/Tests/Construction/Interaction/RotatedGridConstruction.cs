using System.Numerics;
using Content.IntegrationTests.Tests.Interaction;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests.Construction.Interaction;

/// <summary>
/// Triad: pins that the SERVER side of construction is clean on a rotated grid.
///
/// This was written to reproduce the reported "structures build skewed on rotated ships" bug, and it does NOT
/// reproduce it. That negative result is the point, and it is why the test is kept: it fences off the server-side
/// transform path so nobody re-derives it.
///
/// Building a real Window on an 85 degree grid through the full ghost-to-server pipeline yields an entity parented
/// to the grid with local rotation 0, exactly hull-aligned, with no patch applied. So <c>Construct</c>'s
/// <c>SpawnAttachedTo(coords, rotation: angle)</c> is not landing on map-parented coordinates: the client ghost is
/// itself grid-traversed before <c>TryStartConstruction</c> reads its coordinates, so the server receives
/// grid-parented coords and never takes a reparent.
///
/// That distinction matters because <c>SharedTransformSystem.SetParent</c> genuinely does preserve WORLD rotation
/// (<c>newRot = rot - parRot</c>), so a map-to-grid reparent rewrites local rotation by exactly minus the grid
/// rotation. The mechanism is real and would match the reported 1:1 skew; this test shows initial construction does
/// not reach it. If the skew is ever traced to that rewrite, it arrives through some other door.
///
/// Windows are the sharpest available probe: they are <c>canRotate: false</c>, so the server forces
/// <c>Angle.Zero</c> and nothing the client ghost did can influence the result.
///
/// Server state being clean is positive evidence for a client-side, render-side or replication-side cause, which a
/// headless pair like this one cannot observe.
/// </summary>
public sealed class RotatedGridConstruction : InteractionTest
{
    private const string Window = "Window";

    /// <summary>
    /// Deliberately not a multiple of 90. A cardinal grid rotation would pass even with a rotation rewrite present,
    /// because the rewrite would land the structure back on a cardinal.
    /// </summary>
    private const double GridDegrees = 85;

    public override async Task Setup()
    {
        await base.Setup();

        await Server.WaitPost(() =>
        {
            Transform.SetWorldRotation(MapData.Grid.Owner, Angle.FromDegrees(GridDegrees));
        });

        await RunTicks(5);

        // The tiles moved with the grid, so re-derive the coordinates the same way the base setup does.
        PlayerCoords = SEntMan.GetNetCoordinates(
            Transform.WithEntityId(MapData.GridCoords.Offset(new Vector2(0.5f, 0.5f)), MapData.MapUid));
        TargetCoords = SEntMan.GetNetCoordinates(
            Transform.WithEntityId(MapData.GridCoords.Offset(new Vector2(1.5f, 0.5f)), MapData.MapUid));

        await Server.WaitPost(() =>
        {
            Transform.SetCoordinates(SPlayer, SEntMan.GetCoordinates(PlayerCoords));
        });

        await RunTicks(5);
    }

    /// <summary>
    /// A window built on an 85 degree grid sits flush with the hull. Currently passes unpatched; it is a guard
    /// against a future change breaking the server path, not a reproduction of the open skew bug.
    /// </summary>
    [Test]
    public async Task ConstructWindowOnRotatedGrid_IsHullAligned()
    {
        await StartConstruction(Window);
        await InteractUsing(Glass, 5);
        ClientAssertPrototype(Window, Target);

        var target = SEntMan.GetEntity(Target!.Value);
        var xform = SEntMan.GetComponent<TransformComponent>(target);

        Assert.Multiple(() =>
        {
            // Precondition. Without this, a Setup regression that quietly leaves the grid axis-aligned would make
            // everything below pass for the wrong reason.
            Assert.That(Transform.GetWorldRotation(MapData.Grid.Owner).Degrees, Is.EqualTo(GridDegrees).Within(0.01),
                "PRECONDITION: grid must actually be rotated or this test proves nothing");

            // Precondition. The entity has to be on the grid for its local rotation to mean what we think.
            Assert.That(xform.ParentUid, Is.EqualTo(MapData.Grid.Owner),
                "PRECONDITION: window should be parented to the grid");

            // canRotate: false, so the server builds at Angle.Zero, and local zero means flush with the hull.
            Assert.That(xform.LocalRotation.Degrees, Is.EqualTo(0).Within(0.01),
                "window should be hull-aligned");
        });
    }
}
