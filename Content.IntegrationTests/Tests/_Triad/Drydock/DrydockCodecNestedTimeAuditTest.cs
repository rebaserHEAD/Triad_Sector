#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Content.Server._Triad.Drydock.Codec;
using Robust.Shared.GameObjects;
using Robust.Shared.Serialization.Manager.Attributes;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// The F27 family, by construction: every time-offset field the codec's pass corrects below the
    /// component level, found by walking the pass's own classification over every registered
    /// component rather than by reading source. A text enumeration reported <c>DoAfter</c> as the
    /// only such definition while <c>UseDelayInfo</c> sat in the tree, and a list built from the same
    /// code the pass runs cannot drift from what the pass does.
    ///
    /// <para>It also cannot see what the pass cannot see, and it says so: the list is exactly as
    /// complete as the pass's reach. The two deltas it prints are the size of the latest widening of
    /// that reach, the types the generator's decision admits that the attribute alone refused, and the
    /// component members walked only because a polymorphic declared type counts as reachable.</para>
    ///
    /// <para>One thing only a runtime walk knows is which concrete type a polymorphic member holds.
    /// For those the audit takes every concrete server-side data definition the member could hold and
    /// reports what any of them would carry, so a polymorphic line is a type that can be reached, not
    /// one that was.</para>
    ///
    /// <para>The total is printed and not asserted, because the family grows with content and
    /// nothing is wrong when it does. The controls are three instances known by hand, one per shape:
    /// a definition in a dictionary, the same with two fields, and a record struct in a dictionary.</para>
    /// </summary>
    [TestFixture]
    public sealed class DrydockCodecNestedTimeAuditTest
    {
        [Test]
        public async Task TheNestedTimeFamilyIsWhatThePassCorrects()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var factory = server.ResolveDependency<IComponentFactory>();

            var types = ServerTypes().ToList();
            var definitions = types
                .Where(type => !type.IsAbstract
                               && !type.IsInterface
                               && !type.ContainsGenericParameters
                               && DrydockCodecFieldPass.IsDataDefinition(type))
                .ToList();

            var concrete = new Dictionary<Type, List<Type>>();
            IEnumerable<Type> Concrete(Type declared)
            {
                if (!concrete.TryGetValue(declared, out var found))
                    concrete[declared] = found = definitions.Where(declared.IsAssignableFrom).ToList();

                return found;
            }

            var audit = new DrydockCodecFieldPass.NestedTimeAudit(Concrete);
            var family = new SortedSet<string>(StringComparer.Ordinal);
            var flags = new SortedSet<string>(StringComparer.Ordinal);
            var polymorphic = new List<MemberInfo>();
            var refused = new List<string>();
            var components = 0;

            foreach (var component in factory.AllRegisteredTypes.OrderBy(type => type.Name, StringComparer.Ordinal))
            {
                components++;

                try
                {
                    foreach (var (from, corrected) in audit.For(component))
                    {
                        var line = $"{component.Name}.{from.Name} -> {corrected.DeclaringType?.Name}.{corrected.Name}";
                        if (DrydockCodecFieldPass.IsFlagSerializer(corrected.GetCustomAttribute<DataFieldBaseAttribute>()?.CustomTypeSerializer))
                            flags.Add(line);
                        else
                            family.Add(line);
                    }

                    polymorphic.AddRange(DrydockCodecFieldPass.WalkedOnlyForPolymorphism(component));
                }
                catch (Exception e)
                {
                    // A component whose classification throws would throw on every store of it, so
                    // it is a finding with its name rather than a reason to stop the audit.
                    refused.Add($"{component.Name}: {e.GetType().Name}: {e.Message}");
                }
            }

            // The first delta: types the generator treats as data definitions that asking for the
            // attribute refused, because it is not inherited and records and implicit inheritors
            // carry something else.
            var widened = types
                .Where(type => DrydockCodecFieldPass.IsDataDefinition(type)
                               && type.GetCustomAttribute<DataDefinitionAttribute>() == null)
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .ToList();

            var output = TestContext.Out;
            await output.WriteLineAsync(
                $"[nested-time] {components} registered component(s) walked, {definitions.Count} concrete server-side data definition(s) as polymorphic candidates; "
                + $"{family.Count} (component member -> time field) pair(s) in the F27 family; {refused.Count} component(s) whose classification threw.");

            foreach (var line in family)
                await output.WriteLineAsync($"[nested-time] {line}");

            foreach (var line in refused)
                await output.WriteLineAsync($"[nested-time] refused: {line}");

            // F35's members below the component level, which ride the same walk: the pass rewrites each
            // because the engine's flag writer loses bit 0.
            await output.WriteLineAsync($"[nested-flags] {flags.Count} (component member -> flag field) pair(s) the walk reaches below the component level.");
            foreach (var line in flags)
                await output.WriteLineAsync($"[nested-flags] {line}");

            await output.WriteLineAsync(
                $"[nested-time] delta 1: {widened.Count} type(s) the generator treats as data definitions and the [DataDefinition] attribute alone refused.");
            foreach (var group in widened
                         .GroupBy(type => type.Assembly.GetName().Name ?? "?")
                         .OrderByDescending(group => group.Count()))
            {
                await output.WriteLineAsync($"[nested-time]     {group.Key}: {group.Count()}");
            }

            await output.WriteLineAsync(
                $"[nested-time] delta 2: {polymorphic.Count} component member(s) walked only because a polymorphic declared type counts as reachable, by declared type:");
            foreach (var group in polymorphic
                         .GroupBy(member => Describe(MemberType(member)))
                         .OrderByDescending(group => group.Count())
                         .ThenBy(group => group.Key, StringComparer.Ordinal))
            {
                await output.WriteLineAsync($"[nested-time]     {group.Key} x{group.Count()}");
            }

            Assert.Multiple(() =>
            {
                Assert.That(family, Is.Not.Empty,
                    "The control: an empty family would mean the audit reached nothing, not that there is nothing to reach.");

                Assert.That(family, Does.Contain("DoAfterComponent.DoAfters -> DoAfter.StartTime"),
                    "The control: F27's own instance, a definition held in a dictionary.");

                Assert.That(family, Does.Contain("UseDelayComponent.Delays -> UseDelayInfo.StartTime"),
                    "The control: the instance the corpus found after F27, every item cooldown on a ship.");

                Assert.That(family, Does.Contain("UseDelayComponent.Delays -> UseDelayInfo.EndTime"),
                    "The control: and its second field, so a definition is reported per field rather than once.");

                Assert.That(family, Does.Contain("RoboticsConsoleComponent.Cyborgs -> CyborgControlData.Timeout"),
                    "The control: a record struct, which the attribute-only predicate could not reach at all.");

                Assert.That(family, Does.Contain("DoAfterComponent.DoAfters -> DoAfter.CancelledTime"),
                    "The control: the second time field on F27's own definition, which a list by hand missed.");

                Assert.That(family, Does.Contain("WeatherComponent.Weather -> WeatherData.StartTime")
                                    .And.Contain("WeatherComponent.Weather -> WeatherData.EndTime"),
                    "The control: a definition keyed by a prototype id, whose key the walk never descends.");

                Assert.That(family.Where(line => line.StartsWith("ArtifactCrusherComponent.", StringComparison.Ordinal)), Is.Empty,
                    "A tuple names a component it holds only as a handle, and the walk never descends a tuple; "
                    + "reaching through one is the classification describing a walk that does not happen.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The types a server can hold: everything the content and engine assemblies declare, less the
        /// client's, which never reach a store.
        /// </summary>
        private static IEnumerable<Type> ServerTypes()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = assembly.GetName().Name ?? string.Empty;
                if (!(name.StartsWith("Content.", StringComparison.Ordinal) || name.StartsWith("Robust.", StringComparison.Ordinal))
                    || name.Contains(".Client", StringComparison.Ordinal)
                    || name.Contains("Tests", StringComparison.Ordinal)
                    || name.Contains("Benchmarks", StringComparison.Ordinal))
                {
                    continue;
                }

                Type[] declared;
                try
                {
                    declared = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    declared = e.Types.Where(type => type != null).ToArray()!;
                }

                foreach (var type in declared)
                {
                    yield return type;
                }
            }
        }

        private static Type? MemberType(MemberInfo member) => member switch
        {
            FieldInfo field => field.FieldType,
            PropertyInfo property => property.PropertyType,
            _ => null,
        };

        /// <summary>A declared type named the way a reader would write it, generics included.</summary>
        private static string Describe(Type? type)
        {
            if (type == null)
                return "?";

            if (!type.IsGenericType)
                return type.Name;

            var name = type.Name[..type.Name.IndexOf('`')];
            return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(Describe))}>";
        }
    }
}
