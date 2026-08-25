// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Robust.Shared.GameObjects;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.Serialization.TypeSerializers.Interfaces;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// THE DENOMINATOR: walks every registered component's [DataField]s and reports every
    /// field whose type the engine YAML serializer cannot write — the complete, authoritative
    /// set of state the drydock must capture/strip/rehydrate itself. Static and exhaustive:
    /// no ship-by-ship discovery, no waiting for a populated grid to trip it. The empirical
    /// roster sweep test (ported with the drydock systems) is the cross-check on this predictor.
    ///
    /// Writability models the engine's own write dispatch (SerializationManager.Writing):
    /// primitives / string / enum / ISelfSerialize / arrays / generic collections
    /// (recurse the args — a Dictionary&lt;Type,object&gt; HAS a serializer but its args
    /// don't) / registered ITypeWriter types / [DataDefinition] (recurse its fields).
    /// Fields carrying a customTypeSerializer are handled by that serializer, so they're skipped.
    /// </summary>
    [TestFixture]
    public sealed class DrydockSerializabilityAuditTest
    {
        [Test]
        public async Task AuditUnserializableComponentFields()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var compFactory = server.ResolveDependency<IComponentFactory>();
            var serialization = server.ResolveDependency<ISerializationManager>();

            var serializerTypes = CollectRegisteredWriterTypes();

            // STEP 1 (static): the predictor's candidate leaves. Over-reports — it can't
            // see every path Robust makes a type writable (implicit-data-def-for-inheritors,
            // polymorphic/abstract serializers), so it must be filtered empirically.
            var candidates = new Dictionary<Type, List<string>>();
            foreach (var compType in compFactory.AllRegisteredTypes)
            {
                foreach (var (memberName, memberType) in DataFields(compType))
                {
                    var leaf = FindUnwritableLeaf(memberType, serializerTypes, new HashSet<Type>());
                    if (leaf == null)
                        continue;
                    if (!candidates.TryGetValue(leaf, out var list))
                        candidates[leaf] = list = new List<string>();
                    list.Add($"{compType.Name}.{memberName}");
                }
            }

            // STEP 2 (empirical): confirm each candidate by actually asking the live
            // serializer to write an instance. Only a real "No data definition found" throw
            // is a true gap. Abstract/interface leaves are polymorphic-serializable (they're
            // abstract *because* the hierarchy has a serializer), so they drop out.
            var byLeaf = new Dictionary<Type, List<string>>();
            await server.WaitPost(() =>
            {
                foreach (var (leaf, sites) in candidates)
                {
                    if (IsConfirmedUnwritable(serialization, leaf))
                        byLeaf[leaf] = sites;
                }
            });

            TestContext.Out.WriteLine($"[serializability-audit] {candidates.Count} static candidate leaves -> {byLeaf.Count} EMPIRICALLY CONFIRMED unserializable.");

            var lines = new List<string> { $"{byLeaf.Count} unserializable leaf type(s) across component [DataField]s:" };
            foreach (var (leaf, sites) in byLeaf.OrderByDescending(kv => kv.Value.Count))
            {
                lines.Add($"{leaf.FullName}  ({sites.Count} field(s))");
                lines.Add($"    {string.Join(", ", sites.OrderBy(s => s))}");
            }
            var outPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ship_serializability_audit.txt");
            System.IO.File.WriteAllLines(outPath, lines);
            foreach (var l in lines)
                TestContext.Out.WriteLine($"[serializability-audit] {l}");

            await pair.CleanReturnAsync();

            // Enumeration pass: always report, never fail. Becomes a gate (assert against a
            // categorized allow-list) once each leaf is bucketed capture/strip/rehydrate.
            Assert.Pass($"Audit complete: {byLeaf.Count} unserializable leaf types. See [serializability-audit] output.");
        }

        // Ground truth: does the LIVE serializer actually fail to write this type? Only a
        // "No data definition found" throw counts (an NRE on an uninitialized instance is a
        // bad sample, not a serializability gap). Uninstantiable/abstract -> not confirmable
        // -> assume serializable (polymorphic).
        private static bool IsConfirmedUnwritable(ISerializationManager serialization, Type type)
        {
            if (type.IsAbstract || type.IsInterface)
                return false;

            object instance;
            try { instance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type); }
            catch { return false; }

            try
            {
                serialization.WriteValue(type, instance, alwaysWrite: true);
                return false; // wrote fine -> serializable.
            }
            // Engine 287 throws the gap through two doors: the generated data-definition
            // path (InvalidOperationException) and WriteNoSerializer's no-path fallback
            // (ArgumentException). Both wordings are the exact gap; anything else is a bad
            // sample, not a serializability gap.
            catch (InvalidOperationException e) when (e.Message.Contains("No data definition found"))
            {
                return true;
            }
            catch (ArgumentException e) when (e.Message.Contains("No type serializer or data definition found"))
            {
                return true;
            }
            catch
            {
                return false;
            }
        }

        // All [DataField]/[DataDefinition] members on a type, excluding those whose attribute
        // names a customTypeSerializer (that serializer makes an otherwise-unwritable type fine).
        private static IEnumerable<(string, Type)> DataFields(Type type)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (var m in t.GetFields(flags).Cast<MemberInfo>().Concat(t.GetProperties(flags)))
                {
                    var attr = m.GetCustomAttribute<DataFieldBaseAttribute>();
                    if (attr == null || attr.CustomTypeSerializer != null)
                        continue;
                    var mt = m is FieldInfo fi ? fi.FieldType : ((PropertyInfo)m).PropertyType;
                    yield return (m.Name, mt);
                }
            }
        }

        // Returns the specific leaf type that can't be written, or null if the whole type writes.
        private static Type? FindUnwritableLeaf(Type type, HashSet<Type> writerTypes, HashSet<Type> seen)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (!seen.Add(type))
                return null; // cycle: assume fine.

            if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal) || type.IsEnum)
                return null;
            if (typeof(ISelfSerialize).IsAssignableFrom(type))
                return null;

            if (type.IsArray)
                return FindUnwritableLeaf(type.GetElementType()!, writerTypes, seen);

            // Generic collection: the engine collection serializer recurses into the args, so
            // its writability is the AND of its args — regardless of the dict/list serializer.
            if (type.IsGenericType && typeof(IEnumerable).IsAssignableFrom(type))
            {
                foreach (var arg in type.GetGenericArguments())
                {
                    var leaf = FindUnwritableLeaf(arg, writerTypes, seen);
                    if (leaf != null)
                        return leaf;
                }
                return null;
            }

            if (HasWriter(type, writerTypes))
                return null;

            if (type.GetCustomAttribute<DataDefinitionAttribute>() != null)
            {
                foreach (var (_, memberType) in DataFields(type))
                {
                    var leaf = FindUnwritableLeaf(memberType, writerTypes, seen);
                    if (leaf != null)
                        return leaf;
                }
                return null;
            }

            return type; // no path to write this type.
        }

        private static bool HasWriter(Type type, HashSet<Type> writerTypes)
        {
            if (writerTypes.Contains(type))
                return true;
            return type.IsGenericType && writerTypes.Contains(type.GetGenericTypeDefinition());
        }

        // Every type T for which some class implements ITypeWriter<T> (closed types + open
        // generic definitions like Dictionary<,>).
        private static HashSet<Type> CollectRegisteredWriterTypes()
        {
            var result = new HashSet<Type>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray()!; }

                foreach (var type in types)
                {
                    if (type is null || type.IsAbstract || type.IsInterface)
                        continue;
                    foreach (var iface in type.GetInterfaces())
                    {
                        if (!iface.IsGenericType || iface.GetGenericTypeDefinition() != typeof(ITypeWriter<>))
                            continue;
                        var target = iface.GetGenericArguments()[0];
                        result.Add(target.IsGenericType ? target.GetGenericTypeDefinition() : target);
                    }
                }
            }
            return result;
        }
    }
}
