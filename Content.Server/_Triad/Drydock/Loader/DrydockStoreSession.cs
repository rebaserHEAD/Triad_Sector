using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Content.Server._Triad.Drydock.Codec;
using Robust.Shared.GameObjects;
using Robust.Shared.Map.Components;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Timing;

namespace Content.Server._Triad.Drydock.Loader;

/// <summary>
/// One store, in separate calls: <see cref="WriteEntity"/> for each entity in <see cref="Aboard"/>, then
/// <see cref="WriteTiles"/>, then <see cref="Complete"/>. Every savable entity from the grid down is written, each
/// component the engine would save by the codec and carried as JSON text, so the caller can time or slice between calls.
/// </summary>
public sealed class DrydockStoreSession
{
    private readonly DrydockImageSystem _system;
    private readonly EntityUid _grid;
    private readonly IDrydockStoreProbe? _probe;
    private readonly DrydockWalk _walk;
    private readonly DrydockCodec _codec;
    private readonly List<DrydockImageEntity> _entities = new();
    private readonly List<DrydockUnwritableMember> _unwritable = new();
    private readonly Dictionary<string, int> _stripped = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _appearanceSkipped = new(StringComparer.Ordinal);
    private int _bytes;
    private int _writes;
    private string? _tiles;

    internal DrydockStoreSession(DrydockImageSystem system, EntityUid grid, IDrydockStoreProbe? probe)
    {
        _system = system;
        _grid = grid;
        _probe = probe;
        _walk = system.Walk(grid);

        var ids = _walk.Ids;
        _codec = new DrydockCodec(
            system.Serialization,
            system.Entities,
            system.Prototypes,
            system.Timing,
            uid => ids.TryGetValue(uid, out var id) ? id : null,
            _ => throw new InvalidOperationException("The store resolves nothing."));
    }

    private static string Text(Robust.Shared.Serialization.Markdown.DataNode node) => DrydockNodeJson.Encode(node)!.ToJsonString();

    /// <summary>The entities to write, in load order.</summary>
    public IReadOnlyList<EntityUid> Aboard => _walk.Aboard;

    /// <summary>The stable id each entity aboard will be stored under, from the store's walk.</summary>
    public IReadOnlyDictionary<EntityUid, long> Ids => _walk.Ids;

    public void WriteEntity(EntityUid uid)
    {
        var entMan = _system.Entities;
        var factory = _system.ComponentFactory;
        var meta = entMan.GetComponent<MetaDataComponent>(uid);
        var rows = new Dictionary<string, string>();
        var kept = new List<IComponent>();
        foreach (var component in entMan.GetComponents(uid))
        {
            var registration = factory.GetRegistration(component.GetType());

            // A component the manifest strips names a round, a crew member or a live link, and is not the ship's.
            if (DrydockCodecManifestMembers.Stripped.ContainsKey(registration.Name))
            {
                _stripped[registration.Name] = _stripped.GetValueOrDefault(registration.Name) + 1;
                continue;
            }

            kept.Add(component);
            if (registration.Unsaved)
                continue;

            _probe?.Before(uid, registration.Name);
            var text = Text(_codec.Write((uid, meta), component));
            _probe?.After(uid, registration.Name);
            _writes++;

            rows[registration.Name] = text;
            _bytes += text.Length;
        }

        _probe?.Before(uid, DrydockImageSystem.AppearanceRow);
        if (entMan.TryGetComponent<AppearanceComponent>(uid, out var appearance)
            && _system.WriteAppearance(_codec, appearance, _appearanceSkipped) is { } appearanceRow)
        {
            var text = Text(appearanceRow);
            rows[DrydockImageSystem.AppearanceRow] = text;
            _bytes += text.Length;
        }

        _probe?.After(uid, DrydockImageSystem.AppearanceRow);

        _probe?.Before(uid, DrydockCodec.ManifestRow);
        if (_codec.WriteManifest((uid, meta), kept, factory, _unwritable) is { } manifestRow)
        {
            var text = Text(manifestRow);
            rows[DrydockCodec.ManifestRow] = text;
            _bytes += text.Length;
        }

        _probe?.After(uid, DrydockCodec.ManifestRow);

        _entities.Add(new DrydockImageEntity(
            _walk.Ids[uid],
            meta.EntityPrototype?.ID,
            meta.EntityLifeStage >= EntityLifeStage.MapInitialized,
            meta.EntityPaused,
            rows));
    }

    /// <summary>The tile table. Needs the grid's own entity written, because the chunk size is read from its grid row.</summary>
    public void WriteTiles()
    {
        // The chunk size is an internal data field, so it is taken from the grid component's own row, which carries it
        // when it is not the default (MapGridComponent.cs:45-46).
        var gridComp = _system.Entities.GetComponent<MapGridComponent>(_grid);
        var gridId = _walk.Ids[_grid];
        var gridRow = (MappingDataNode) DrydockNodeJson.Decode(JsonNode.Parse(_entities.Single(e => e.Id == gridId).Rows["MapGrid"])!);
        var chunkSize = gridRow.TryGet<ValueDataNode>("chunkSize", out var sizeNode)
            ? ushort.Parse(sizeNode.Value, System.Globalization.CultureInfo.InvariantCulture)
            : MapGridComponent.DefaultChunkSize;

        var tileDefs = _system.TileDefinitions;
        var tiles = DrydockTileTable.Write(
            chunkSize,
            _system.Maps.GetAllTiles(_grid, gridComp).Select(tile => (tile.GridIndices, tile.Tile)),
            id => tileDefs[id].ID);

        _tiles = Text(tiles);
    }

    public DrydockImageStoreResult Complete()
    {
        if (_tiles == null)
            throw new InvalidOperationException("Drydock store: WriteTiles has not run.");

        if (_entities.Count != _walk.Aboard.Count)
            throw new InvalidOperationException($"Drydock store: {_entities.Count} of {_walk.Aboard.Count} entities written.");

        var image = new DrydockImage(_walk.Ids[_grid], _entities, _tiles, _walk.Unsaved, _bytes + _tiles.Length);
        return new DrydockImageStoreResult(image, _unwritable, _stripped, _appearanceSkipped, _writes);
    }
}
