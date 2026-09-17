#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Content.Shared.NodeContainer.NodeGroups;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Physics.Dynamics.Contacts;
using Robust.Shared.Physics.Dynamics.Joints;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// The system-held state census: every instance and static field of every entity system a server runs, content
    /// and engine, sorted by whether its type can reach an entity. A field reaches one when its type is
    /// <see cref="EntityUid"/>, <see cref="NetEntity"/>, a component, or a seed that holds entities (a node group, a
    /// physics contact, a joint), or contains one: an element, a type argument, or a field of a game type, followed
    /// transitively to <see cref="MaxDepth"/>. Those fields are state a grid image cannot see through components.
    /// Client-only systems are outside the walk: it asks the server's system manager.
    ///
    /// <para>Excluded by rule and counted: <c>[Dependency]</c> fields, references to other systems (their own fields
    /// are enumerated on their own rows) and <see cref="EntityQuery{TComp1}"/> handles. A type walk cannot see what an
    /// <c>object</c>, interface or delegate holds, so those are counted apart: as a system's field, by kind, and inside
    /// the game types a walk passes through, listed at the end of the output.</para>
    ///
    /// <para>Run: <c>CENSUS_OUT=path.tsv dotnet test Content.IntegrationTests --no-build --filter "FullyQualifiedName~DrydockSystemStateCensusLocalTest"</c>.</para>
    /// </summary>
    [TestFixture]
    [Explicit("Exploratory census. Run it deliberately and classify its output.")]
    public sealed class DrydockSystemStateCensusLocalTest
    {
        /// <summary>How many nested types a walk follows before it gives up on a path; each give-up is counted.</summary>
        private const int MaxDepth = 8;

        private static readonly Type[] Seeds = { typeof(INodeGroup), typeof(Contact), typeof(Joint) };

        private readonly Dictionary<Type, string?> _memo = new();
        private readonly HashSet<string> _opaqueInner = new(StringComparer.Ordinal);
        private readonly HashSet<Type> _onWalk = new();
        private int _capHits;
        private int _cycleHits;

        [Test]
        public async Task Enumerate()
        {
            var output = Environment.GetEnvironmentVariable("CENSUS_OUT");
            Assert.That(output, Is.Not.Null.And.Not.Empty, "Set CENSUS_OUT to the TSV path to write.");

            await using var pair = await PoolManager.GetServerClient();
            var systemTypes = pair.Server.ResolveDependency<IEntitySystemManager>().GetEntitySystemTypes().ToList();

            var rows = new List<string>();
            var seen = new HashSet<(Type, string)>();
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
                                       | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            // The test pool registers the harness's own systems too; a production server runs none of them.
            systemTypes.RemoveAll(system => system.Assembly.GetName().Name is { } assembly
                                            && (assembly.Contains("IntegrationTests", StringComparison.Ordinal)
                                                || assembly.StartsWith("Robust.UnitTesting", StringComparison.Ordinal)));

            foreach (var system in systemTypes)
            {
                for (var level = system; level != null && level != typeof(EntitySystem) && level != typeof(object); level = level.BaseType)
                {
                    foreach (var field in level.GetFields(flags))
                    {
                        if (field.IsLiteral || !seen.Add((field.DeclaringType!, field.Name)))
                            continue;

                        var (kind, reach) = Sort(field);
                        counts[kind] = counts.GetValueOrDefault(kind) + 1;

                        if (kind.StartsWith("excluded:", StringComparison.Ordinal) || kind == "none")
                            continue;

                        rows.Add(string.Join('\t',
                            level.Assembly.GetName().Name,
                            level.FullName,
                            field.Name,
                            field.IsStatic ? "static" : "instance",
                            Pretty(field.FieldType),
                            kind,
                            reach));
                    }
                }
            }

            rows.Sort(StringComparer.Ordinal);
            var sb = new StringBuilder();
            sb.AppendLine($"# systems\t{systemTypes.Count}");
            foreach (var (kind, count) in counts.OrderBy(c => c.Key, StringComparer.Ordinal))
                sb.AppendLine($"# fields {kind}\t{count}");
            sb.AppendLine($"# walks stopped at depth {MaxDepth}\t{_capHits}");
            sb.AppendLine($"# walks stopped on a cycle\t{_cycleHits}");
            sb.AppendLine($"# opaque members inside walked game types\t{_opaqueInner.Count}");
            sb.AppendLine("assembly\tdeclaring_type\tfield\tscope\ttype\tkind\treach");
            foreach (var row in rows)
                sb.AppendLine(row);

            sb.AppendLine("# opaque members inside walked game types: declaring_type.field\ttype");
            foreach (var opaque in _opaqueInner.OrderBy(o => o, StringComparer.Ordinal))
                sb.AppendLine(opaque);

            File.WriteAllText(output!, sb.ToString());
            await TestContext.Out.WriteLineAsync(
                $"[census] {systemTypes.Count} systems, {rows.Count} rows, {counts.GetValueOrDefault("reaches")} reaching an entity, written to {output}");

            Assert.That(systemTypes, Is.Not.Empty, "The control: the census saw no systems.");
            Assert.That(counts.GetValueOrDefault("reaches"), Is.GreaterThan(0), "The control: no field reached an entity.");

            await pair.CleanReturnAsync();
        }

        private (string Kind, string Reach) Sort(FieldInfo field)
        {
            var type = field.FieldType;

            if (field.GetCustomAttribute<DependencyAttribute>() != null)
                return ("excluded:dependency", "");
            if (typeof(IEntitySystem).IsAssignableFrom(type))
                return ("excluded:system", "");
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(EntityQuery<>))
                return ("excluded:query", "");
            if (typeof(Delegate).IsAssignableFrom(type))
                return ("opaque:delegate", "");
            if (Reach(type, 0) is { } chain)
                return ("reaches", chain);
            if (type == typeof(object))
                return ("opaque:object", "");
            if (type.IsInterface && !IsCollectionInterface(type))
                return ("opaque:interface", "");

            return ("none", "");
        }

        /// <summary>
        /// The first path by which a type reaches an entity, or null. Game types (Content and Robust assemblies) are
        /// followed through their instance fields; framework types only through elements and type arguments.
        /// </summary>
        private string? Reach(Type type, int depth)
        {
            if (_memo.TryGetValue(type, out var known))
                return known;

            if (depth > MaxDepth)
            {
                _capHits++;
                return null;
            }

            // A type already on the walk reads as no reach for this path only. A null that a cycle or the depth cap
            // produced is not cached, since another path to the same type may still reach.
            if (!_onWalk.Add(type))
            {
                _cycleHits++;
                return null;
            }

            var capsBefore = _capHits;
            var cyclesBefore = _cycleHits;
            var result = ReachUncached(type, depth);
            _onWalk.Remove(type);

            if (result != null || (_capHits == capsBefore && _cycleHits == cyclesBefore))
                _memo[type] = result;

            return result;
        }

        private string? ReachUncached(Type type, int depth)
        {
            if (type == typeof(EntityUid))
                return "EntityUid";
            if (type == typeof(NetEntity))
                return "NetEntity";
            if (typeof(IComponent).IsAssignableFrom(type))
                return $"component {type.Name}";
            if (Seeds.FirstOrDefault(seed => seed.IsAssignableFrom(type)) is { } seedType)
                return $"seed {seedType.Name}";
            if (type.IsPointer || type.IsByRef || typeof(Delegate).IsAssignableFrom(type))
                return null;

            if (type.IsArray)
                return Reach(type.GetElementType()!, depth + 1) is { } inner ? $"{Pretty(type)} > {inner}" : null;

            if (type.IsGenericType)
            {
                foreach (var argument in type.GetGenericArguments())
                {
                    if (Reach(argument, depth + 1) is { } inner)
                        return $"{Pretty(type)} > {inner}";

                    if (IsOpaque(argument))
                        _opaqueInner.Add($"{Pretty(type)} type argument\t{Pretty(argument)}");
                }
            }

            if (type.IsPrimitive || type.IsEnum || type == typeof(string) || !IsGameType(type))
                return null;

            for (var level = type; level != null && level != typeof(object) && IsGameType(level); level = level.BaseType)
            {
                foreach (var field in level.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (typeof(IEntitySystem).IsAssignableFrom(field.FieldType)
                        || field.GetCustomAttribute<DependencyAttribute>() != null)
                    {
                        continue;
                    }

                    if (Reach(field.FieldType, depth + 1) is { } inner)
                        return $"{type.Name}.{field.Name} > {inner}";

                    if (IsOpaque(field.FieldType))
                        _opaqueInner.Add($"{level.FullName}.{field.Name}\t{Pretty(field.FieldType)}");
                }
            }

            return null;
        }

        private static bool IsOpaque(Type type) =>
            type == typeof(object)
            || typeof(Delegate).IsAssignableFrom(type)
            || type.IsInterface && !IsCollectionInterface(type);

        private static bool IsGameType(Type type)
        {
            var name = type.Assembly.GetName().Name ?? "";
            return name.StartsWith("Content.", StringComparison.Ordinal) || name.StartsWith("Robust.", StringComparison.Ordinal);
        }

        private static bool IsCollectionInterface(Type type) =>
            type.Namespace?.StartsWith("System.Collections", StringComparison.Ordinal) == true;

        private static string Pretty(Type type)
        {
            if (type.IsArray)
                return $"{Pretty(type.GetElementType()!)}[]";
            if (!type.IsGenericType)
                return type.Name;

            var name = type.Name[..type.Name.IndexOf('`')];
            return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(Pretty))}>";
        }
    }
}
