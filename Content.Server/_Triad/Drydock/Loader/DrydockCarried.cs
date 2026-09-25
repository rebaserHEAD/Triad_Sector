using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Content.Server._Triad.Drydock.Codec;
using Robust.Shared.GameObjects;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;

namespace Content.Server._Triad.Drydock.Loader;

/// <summary>
/// A load's carried values (<see cref="GridStoringEvent.Carry{T}"/>), handed to <see cref="GridRestoringEvent"/> and the
/// directed <see cref="GridRestoredEvent"/>. It answers about the image, not the world: a parsed row answers whatever has
/// become of its entity, and a handler that needs the entity alive checks that itself.
///
/// <para>Valid only while the load's <see cref="DrydockLoadSession.Complete"/> runs, and every call throws after it. A
/// value is decoded on each call and never cached, so an owner decodes once, applies once, and copies anything it needs
/// later into its own state.</para>
/// </summary>
public sealed class DrydockCarried
{
    private readonly DrydockCodec _codec;
    private readonly IReadOnlyDictionary<EntityUid, (string? Prototype, MappingDataNode Row)> _rows;
    private bool _closed;

    internal DrydockCarried(DrydockCodec codec, IReadOnlyDictionary<EntityUid, (string? Prototype, MappingDataNode Row)> rows)
    {
        _codec = codec;
        _rows = rows;
    }

    /// <summary>
    /// The value carried for <paramref name="uid"/> under <paramref name="key"/>, false only when it carried none. A
    /// value present that does not decode as <typeparamref name="T"/> throws <see cref="DrydockCarriedException"/>.
    /// </summary>
    public bool TryGet<T>(EntityUid uid, string key, [NotNullWhen(true)] out T? value) where T : notnull
    {
        value = default;
        if (!TryGetNode(uid, key, out var prototype, out var node))
            return false;

        object? read;
        try
        {
            read = _codec.ReadValue(typeof(T), node);
        }
        catch (Exception e)
        {
            throw new DrydockCarriedException(prototype, key, typeof(T), e);
        }

        if (read is not T typed)
            throw new DrydockCarriedException(prototype, key, typeof(T), null);

        value = typed;
        return true;
    }

    /// <summary>Whether <paramref name="uid"/> carried a value under <paramref name="key"/>.</summary>
    public bool Has(EntityUid uid, string key) => TryGetNode(uid, key, out _, out _);

    internal void Close() => _closed = true;

    private bool TryGetNode(EntityUid uid, string key, out string? prototype, [NotNullWhen(true)] out DataNode? node)
    {
        if (_closed)
            throw new InvalidOperationException("The carried lookup is closed; copy the value out at GridRestoredEvent.");

        prototype = null;
        node = null;
        if (!_rows.TryGetValue(uid, out var entry))
            return false;

        prototype = entry.Prototype;
        return entry.Row.TryGet(key, out node);
    }
}

/// <summary>A carried value that is present and does not decode as the type asked for: a fault, never an absence.</summary>
public sealed class DrydockCarriedException(string? prototype, string key, Type type, Exception? inner)
    : Exception($"Drydock load: '{key}' carried on {prototype ?? "(no prototype)"} does not decode as {type.Name}.", inner);
