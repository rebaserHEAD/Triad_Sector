using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Content.Server._Triad.Drydock.Loader;
using Content.Server.Administration;
using Content.Shared.Administration;
using Content.Shared.Mobs.Components;
using Robust.Shared.Console;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Map.Components;
using Robust.Shared.Player;

namespace Content.Server._Triad.Drydock.Admin;

/// <summary>
/// A dev smoke test of the grid image loop: stores a grid to the in-memory row store, despawns it the store's way, and
/// loads it back in place. The despawn waits for the store's whole promise: nothing unwritable, the image filed, and
/// the filed image read back. A refused store leaves the grid where it is. A load that fails after the despawn says
/// so and leaves the image under its id, and <c>drydock_image_roundtrip load &lt;imageId&gt;</c> loads it again.
///
/// <para>The image carries no minds, so a grid with a player aboard is refused; the mobs aboard that the walk leaves out
/// are counted before anything happens, because the despawn deletes them. Images live in memory only and are gone at a
/// restart.</para>
/// </summary>
[AdminCommand(AdminFlags.Debug)]
public sealed class DrydockImageRoundTripCommand : IConsoleCommand
{
    private static readonly DrydockMemoryRowStore Images = new();

    /// <summary>The map each filed image came from, and the grid last loaded from it, for a retry.</summary>
    private static readonly Dictionary<Guid, (EntityUid Map, EntityUid? Loaded)> Records = new();

    public string Command => "drydock_image_roundtrip";
    public string Description => "Dev: stores a grid as a drydock image in memory, despawns it, and loads it back in place.";
    public string Help => $"Usage: {Command} <gridUid> | {Command} load <imageId>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var entMan = IoCManager.Resolve<IEntityManager>();
        var system = entMan.System<DrydockImageSystem>();

        if (args.Length == 2 && args[0] == "load")
        {
            LoadAgain(shell, entMan, system, args[1]);
            return;
        }

        if (args.Length != 1 || !NetEntity.TryParse(args[0], out var net) || !entMan.TryGetEntity(net, out var grid)
            || !entMan.HasComponent<MapGridComponent>(grid))
        {
            shell.WriteError($"{Help}\nThe argument must be the uid of a grid.");
            return;
        }

        RoundTrip(shell, entMan, system, grid.Value);
    }

    private static void RoundTrip(IConsoleShell shell, IEntityManager entMan, DrydockImageSystem system, EntityUid grid)
    {
        var xforms = entMan.System<SharedTransformSystem>();
        if (xforms.GetMap(grid) is not { } mapUid)
        {
            shell.WriteError("The grid is not on a map.");
            return;
        }

        var walk = system.Walk(grid);
        var everyone = new List<EntityUid>();
        var stack = new Stack<EntityUid>();
        stack.Push(grid);
        while (stack.TryPop(out var uid))
        {
            everyone.Add(uid);
            var children = entMan.GetComponent<TransformComponent>(uid).ChildEnumerator;
            while (children.MoveNext(out var child))
                stack.Push(child);
        }

        var players = everyone.Where(entMan.HasComponent<ActorComponent>).ToList();
        if (players.Count > 0)
        {
            shell.WriteError($"Refused: {players.Count} player-controlled entit(y/ies) aboard, and the image carries no minds. The grid is untouched.");
            return;
        }

        var mobs = everyone.Where(entMan.HasComponent<MobStateComponent>).ToList();
        var lost = mobs.Count(uid => !walk.Ids.ContainsKey(uid));
        shell.WriteLine($"{mobs.Count} mob(s) aboard, {lost} of them left out of the image by the walk and lost to the despawn.");

        // The store's whole promise before anything is deleted: nothing unwritable, the image filed, the filed image read back.
        var imageId = Guid.NewGuid();
        DrydockImageStoreResult stored;
        var watch = Stopwatch.StartNew();
        try
        {
            stored = system.Store(grid);
        }
        catch (Exception e)
        {
            shell.WriteError($"Refused: the store threw ({e.GetType().Name}: {e.Message}). The grid is untouched.");
            return;
        }

        var storeTime = watch.Elapsed;
        if (stored.Unwritable.Count > 0)
        {
            shell.WriteError($"Refused: {stored.Unwritable.Count} manifest member(s) could not be written, so the image would lose them. The grid is untouched.");
            foreach (var member in stored.Unwritable)
                shell.WriteError($"  {member.Prototype ?? "(no prototype)"} {member.Entity} {member.Member.Key}: {member.Exception}: {member.Message}");

            return;
        }

        var put = Images.Put(imageId, stored.Image);
        DrydockImage? filed = null;
        if (put.IsCompletedSuccessfully)
            TryGet(imageId, out filed);

        if (filed == null || filed.GridId != stored.Image.GridId || filed.Entities.Count != stored.Image.Entities.Count || filed.Bytes != stored.Image.Bytes)
        {
            shell.WriteError("Refused: the image was not filed and read back whole. The grid is untouched.");
            return;
        }

        Records[imageId] = (mapUid, null);
        shell.WriteLine($"Stored {filed.Entities.Count} entities ({filed.Unsaved} unsavable left out with what they held), {stored.Writes} component rows, "
                        + $"{filed.Bytes} bytes of JSON in {storeTime.TotalMilliseconds:F0} ms, as image {imageId}.");

        // From here the grid is deleted and the image is what remains.
        system.Despawn(grid);
        shell.WriteLine("Despawned the grid on a staging map.");
        Load(shell, entMan, system, imageId, filed, mapUid);
    }

    private static void LoadAgain(IConsoleShell shell, IEntityManager entMan, DrydockImageSystem system, string idText)
    {
        if (!Guid.TryParse(idText, out var imageId) || !Records.TryGetValue(imageId, out var record))
        {
            shell.WriteError("No image by that id is held in memory.");
            return;
        }

        if (record.Loaded is { } loaded && entMan.EntityExists(loaded))
        {
            shell.WriteError($"Refused: that image is already loaded as grid {entMan.GetNetEntity(loaded)}, and a second load would duplicate the ship.");
            return;
        }

        if (!entMan.EntityExists(record.Map))
        {
            shell.WriteError("Refused: the map the image came from is gone.");
            return;
        }

        if (!TryGet(imageId, out var image) || image == null)
        {
            shell.WriteError("Refused: the image is not in the store.");
            return;
        }

        Load(shell, entMan, system, imageId, image, record.Map);
    }

    private static void Load(IConsoleShell shell, IEntityManager entMan, DrydockImageSystem system, Guid imageId, DrydockImage image, EntityUid mapUid)
    {
        var watch = Stopwatch.StartNew();
        var session = system.BeginLoad(image, mapUid);
        try
        {
            session.CreateEntities();
            session.ApplyRows();
            session.Start();
            var result = session.Complete();
            Records[imageId] = (mapUid, result.Grid);

            var manifest = result.Manifest;
            shell.WriteLine($"Loaded {result.Ids.Count} entities as grid {entMan.GetNetEntity(result.Grid)} in {watch.Elapsed.TotalMilliseconds:F0} ms. "
                            + $"Tiles {result.TilesStored} stored, {result.TilesRestored} restored, {result.TilesMissing} missing, {result.TilesExtra} extra.");
            shell.WriteLine($"Manifest: set {manifest.Set(Codec.DrydockApplyMoment.BeforeInit)} before init, {manifest.Set(Codec.DrydockApplyMoment.Seam)} at the seam, "
                            + $"{manifest.Set(Codec.DrydockApplyMoment.AfterStart)} after start; component gone at its moment {manifest.Missing.Values.Sum()}, "
                            + $"refused {manifest.Refused.Values.Sum()}; appearance entries refused {result.AppearanceRefused.Values.Sum()}; "
                            + $"unresolved prototype ids {result.UnresolvedPrototypes.Count}.");
            foreach (var (line, count) in manifest.Missing)
                shell.WriteError($"  gone at its moment: {line} x{count}");
            foreach (var (line, count) in manifest.Refused)
                shell.WriteError($"  refused: {line} x{count}");
            foreach (var (name, count) in result.AppearanceRefused)
                shell.WriteError($"  appearance type refused: {name} x{count}");
        }
        catch (Exception e)
        {
            // A half-built grid would make a retry a duplicate, so it goes; the image is what is kept.
            if (session.Grid.IsValid() && entMan.EntityExists(session.Grid))
                entMan.DeleteEntity(session.Grid);

            shell.WriteError("THE LOAD FAILED AFTER THE DESPAWN: the original grid is GONE.");
            shell.WriteError($"  {e.GetType().Name}: {e.Message}");
            shell.WriteError($"  The image is still held in memory as {imageId}. Load it again with: {DrydockImageRoundTripCommandName} load {imageId}");
            shell.WriteError("  (a half-built grid, if the load made one, was deleted first.)");
        }
    }

    /// <summary>The filed image, when the memory store answered in this frame, which it always does.</summary>
    private static bool TryGet(Guid imageId, out DrydockImage? image)
    {
        var get = Images.Get(imageId);
        image = get.IsCompletedSuccessfully ? get.GetAwaiter().GetResult() : null;
        return get.IsCompletedSuccessfully;
    }

    private const string DrydockImageRoundTripCommandName = "drydock_image_roundtrip";
}
