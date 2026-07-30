using Content.Server._Triad.Worldgen.Cells; // Triad: seeded room rolls for pre-determined debris
using Robust.Shared.Map.Components;

namespace Content.Server.Procedural;

public sealed class RoomFillSystem : EntitySystem
{
    [Dependency] private readonly DungeonSystem _dungeon = default!;
    [Dependency] private readonly SharedMapSystem _maps = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<RoomFillComponent, MapInitEvent>(OnRoomFillMapInit);
    }

    private void OnRoomFillMapInit(EntityUid uid, RoomFillComponent component, MapInitEvent args)
    {
        var xform = Transform(uid);

        if (xform.GridUid != null)
        {
            // Triad: seeded when the grid being filled is pre-determined debris, so the room pick
            // is the same every time that rock materializes instead of re-rolling on each load.
            // RoomFill also backs non-worldgen markers (dungeon generation, wrecks, maints) whose
            // grid never carries a PredeterminedShapeComponent; those keep rolling on a fresh,
            // unseeded Random exactly as before. DungeonSystem takes System.Random, so this derives
            // the seed directly rather than going through SeededRandom.ForStage, matching what
            // BlobFloorPlanBuilderSystem does under the same constraint.
            var random = TryComp<PredeterminedShapeComponent>(xform.GridUid.Value, out var shape)
                ? new Random(shape.Seed ^ SeededRandom.RoomStage)
                : new Random();
            var room = _dungeon.GetRoomPrototype(random, component.RoomWhitelist, component.MinSize, component.MaxSize);

            if (room != null)
            {
                var mapGrid = Comp<MapGridComponent>(xform.GridUid.Value);
                _dungeon.SpawnRoom(
                    xform.GridUid.Value,
                    mapGrid,
                    _maps.LocalToTile(xform.GridUid.Value, mapGrid, xform.Coordinates) - new Vector2i(room.Size.X/2,room.Size.Y/2),
                    room,
                    random,
                    null,
                    clearExisting: component.ClearExisting,
                    rotation: component.Rotation);
            }
            else
            {
                Log.Error($"Unable to find matching room prototype for {ToPrettyString(uid)}");
            }
        }

        // Final cleanup
        QueueDel(uid);
    }
}
