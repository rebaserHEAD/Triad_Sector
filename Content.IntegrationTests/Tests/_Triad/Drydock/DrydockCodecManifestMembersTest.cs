#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Server._Triad.Drydock;
using Content.Server._Triad.Drydock.Codec;
using Content.Shared.Power;
using Robust.Shared.Audio;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Reflection;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Manager.Attributes;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// The build-time guard of <see cref="DrydockCodecManifestMembers"/>. A third of its entries are on components no sold
    /// hull carries, which no ladder rung can exercise, so for those this is the only check that a rename upstream has not
    /// quietly dropped one. Every entry has to name a registered component and a member that resolves on it, and has to be
    /// the kind it says: a member the manifest carries must not be a data field (the codec carries those already, and the
    /// entry would be a census error), a re-applied one must be carried already, and a time must be a time. And every
    /// member the manifest writes has to have a type that writes, whatever value it holds.
    /// </summary>
    [TestFixture]
    [TestOf(typeof(DrydockCodecManifestMembers))]
    public sealed class DrydockCodecManifestMembersTest
    {
        [Test]
        public async Task EveryManifestEntryResolvesAsItsKind()
        {
            await using var pair = await PoolManager.GetServerClient();
            var factory = pair.Server.ResolveDependency<IComponentFactory>();

            var wrong = new List<string>();
            var resolved = 0;

            foreach (var entry in DrydockCodecManifestMembers.Members)
            {
                if (Wrong(factory, entry) is { } reason)
                    wrong.Add($"row {entry.Row} {entry.Component}.{entry.Member}: {reason}");
                else
                    resolved++;
            }

            foreach (var entry in DrydockCodecManifestMembers.NotCarried)
            {
                if (!factory.TryGetRegistration(entry.Component, out var registration))
                    wrong.Add($"row {entry.Row} {entry.Component}.{entry.Member} (not carried): {entry.Component} is not a registered component");
                else if (DrydockCodecManifestMembers.Resolve(registration.Type, entry.Member) == null)
                    wrong.Add($"row {entry.Row} {entry.Component}.{entry.Member} (not carried): no such member");
            }

            foreach (var name in DrydockCodecManifestMembers.Stripped.Keys)
            {
                if (!factory.TryGetRegistration(name, out _))
                    wrong.Add($"stripped {name}: not a registered component");
            }

            // By (component, member) as well as by entry, because a dictionary's entries are several entries of one member,
            // and the census join counts members.
            var members = DrydockCodecManifestMembers.Members.Select(m => (m.Component, m.Member)).Distinct().Count();
            await TestContext.Out.WriteLineAsync(
                $"[manifest] {resolved} of {DrydockCodecManifestMembers.Members.Length} member entries resolved as their kind; "
                + $"{members} distinct (component, member) pairs, {DrydockCodecManifestMembers.Members.Count(m => m.OwedWith != null)} owed; "
                + $"{DrydockCodecManifestMembers.NotCarried.Length} not carried, {DrydockCodecManifestMembers.Stripped.Count} stripped components checked.");
            foreach (var line in wrong)
                await TestContext.Out.WriteLineAsync($"[manifest]   {line}");

            Assert.That(wrong, Is.Empty, "Every manifest entry has to resolve as the kind it says; the list above names each that does not.");

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Every member the manifest hands the serializer (<see cref="DrydockCodec.WrittenAs"/>) has a type that writes, owed
        /// members included, proved before any hull holds one; otherwise a store finds out, and a scuttle device's song was
        /// found that way. The manager has no query for whether it can write a type (its serializer provider is private,
        /// SerializationManager.SerializerProvider.cs:90, and its data-definition lookup internal, SerializationManager.cs:324),
        /// so this writes a sample of each type through the codec's own context and reads what the engine throws with the
        /// serializability audit's classifier, <see cref="DrydockSerializationGap.IsNoCoverage"/>.
        /// </summary>
        [Test]
        public async Task EveryMemberTheManifestWritesHasATypeThatWrites()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var factory = server.ResolveDependency<IComponentFactory>();

            var unwritable = new List<string>();
            var written = 0;
            var notWritten = 0;
            WritabilityProbe probe = default!;

            await server.WaitPost(() =>
            {
                probe = Probe(pair);
                foreach (var entry in DrydockCodecManifestMembers.Members)
                {
                    switch (Unwritable(factory, probe, entry))
                    {
                        case (false, _):
                            notWritten++;
                            break;
                        case (true, { } reason):
                            written++;
                            unwritable.Add($"row {entry.Row} {entry.Key}: {reason}");
                            break;
                        default:
                            written++;
                            break;
                    }
                }
            });

            await TestContext.Out.WriteLineAsync(
                $"[manifest] writability, by a sample of each type written through the codec's context: {written} member entries go through the serializer, "
                + $"{probe.Sampled} sample(s) written, {probe.Subtypes} of them a concrete type of a declaration that is not sealed; "
                + $"{notWritten} do not (a time through the adapter, a marker, a re-applied data field).");
            foreach (var line in unwritable)
                await TestContext.Out.WriteLineAsync($"[manifest]   UNWRITABLE {line}");

            Assert.That(unwritable, Is.Empty, "Every member the manifest writes has to have a type the codec's context can write; the list above names each that cannot.");

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The control: each check above is an absence, which a broken check reports too, so the same check is pointed at
        /// entries corrupted one way each.
        /// </summary>
        [Test]
        public async Task TheManifestCheckCatchesACorruptedEntry()
        {
            await using var pair = await PoolManager.GetServerClient();
            var factory = pair.Server.ResolveDependency<IComponentFactory>();
            var gravity = DrydockCodecManifestMembers.Members.Single(m => m.Component == "GravityGenerator");
            var fryer = DrydockCodecManifestMembers.Members.Single(m => m.Component == "DeepFryer");
            var cutWires = DrydockCodecManifestMembers.Members.Single(m => Equals(m.EntryKey, PowerWireActionKey.CutWires));

            // The writability check pointed at the member a store found unwritable, which resolves, and at collections one
            // level down, whose empty sample writes whatever they hold.
            var song = new DrydockManifestMember(507, "ScuttleDevice", "SelectedNukeSong", DrydockApplyMoment.BeforeInit, DrydockMemberKind.Field);
            (bool Written, string? Reason) songVerdict = default, gravityVerdict = default;
            string? songs = null, entities = "unset", anything = null;
            await pair.Server.WaitPost(() =>
            {
                var probe = Probe(pair);
                songVerdict = Unwritable(factory, probe, song);
                gravityVerdict = Unwritable(factory, probe, gravity);
                songs = probe.Unwritable(typeof(List<ResolvedSoundSpecifier>));
                entities = probe.Unwritable(typeof(Dictionary<string, List<EntityUid>>));
                anything = probe.Unwritable(typeof(object));
            });

            await TestContext.Out.WriteLineAsync($"[manifest] control: {song.Key}: {songVerdict.Reason}");
            await TestContext.Out.WriteLineAsync($"[manifest] control: List<ResolvedSoundSpecifier>: {songs}");

            Assert.Multiple(() =>
            {
                Assert.That(Wrong(factory, gravity), Is.Null, "The control's control: the real entry resolves.");
                Assert.That(Wrong(factory, gravity with { Component = "GravityGeneratorThatIsNot" }), Is.Not.Null,
                    "A component that is not registered went unreported.");
                Assert.That(Wrong(factory, gravity with { Member = "GravityActiveThatIsNot" }), Is.Not.Null,
                    "A member that does not exist went unreported.");
                Assert.That(Wrong(factory, fryer with { Kind = DrydockMemberKind.Field }), Is.Not.Null,
                    "A data field listed as carried by the manifest went unreported, and it would be written twice.");
                Assert.That(Wrong(factory, gravity with { Kind = DrydockMemberKind.ReapplyCarried }), Is.Not.Null,
                    "A member listed as re-applied that nothing carries went unreported.");
                Assert.That(Wrong(factory, gravity with { Kind = DrydockMemberKind.AbsoluteTime }), Is.Not.Null,
                    "A time that is not a TimeSpan went unreported.");

                Assert.That(Wrong(factory, cutWires), Is.Null, "The control's control: the real dictionary entry resolves.");
                Assert.That(Wrong(factory, cutWires with { EntryKey = null }), Is.Not.Null,
                    "A dictionary entry without its key went unreported.");
                Assert.That(Wrong(factory, gravity with { Kind = DrydockMemberKind.Entry, EntryKey = PowerWireActionKey.CutWires, EntryType = typeof(int) }), Is.Not.Null,
                    "An entry of a member that is not a dictionary went unreported.");
                Assert.That(Wrong(factory, gravity with { EntryKey = PowerWireActionKey.CutWires }), Is.Not.Null,
                    "An entry key on a member not listed as an entry went unreported.");
                Assert.That(Wrong(factory, gravity with { SkipWhen = "NoSuchFlag" }), Is.Not.Null,
                    "A skip flag that is not a member went unreported.");

                Assert.That(Wrong(factory, song), Is.Null, "The control's control: the song resolves as a member, so only its type can fail it.");
                Assert.That(gravityVerdict, Is.EqualTo((true, (string?) null)), "The control's control: a bool member is written, and writes.");
                Assert.That(entities, Is.Null, "The control's control: a dictionary of lists of entities writes.");
                Assert.That(songVerdict.Written, Is.True, "The song goes through the serializer, so it has to be checked.");
                Assert.That(songVerdict.Reason, Does.Contain(nameof(ResolvedSoundSpecifier)),
                    "A member declared as ResolvedSoundSpecifier, which no serializer writes, went unreported or unnamed.");
                Assert.That(songs, Does.Contain(nameof(ResolvedSoundSpecifier)),
                    "A collection of a type no serializer writes went unreported: its empty sample writes, so what it holds has to be sampled on its own.");
                Assert.That(anything, Is.Not.Null, "A member declared as object went unreported.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Whether the manifest hands a member's value to the serializer, and if it does, why that value's type cannot be
        /// written, or null when it can. A member that does not resolve is the resolution test's, and counts as not written.
        /// </summary>
        private static (bool Written, string? Reason) Unwritable(IComponentFactory factory, WritabilityProbe probe, DrydockManifestMember entry)
        {
            if (!factory.TryGetRegistration(entry.Component, out var registration)
                || DrydockCodecManifestMembers.Resolve(registration.Type, entry.Member) is not { } member
                || DrydockCodec.WrittenAs(entry, member) is not { } type)
            {
                return (false, null);
            }

            return (true, probe.Unwritable(type));
        }

        /// <summary>
        /// A probe under the codec's own context, with nothing on an image, so every reference writes as severed: the
        /// question is only whether a type writes. Its candidate types are those in the server's own assemblies.
        /// </summary>
        private static WritabilityProbe Probe(TestPair pair)
        {
            var server = pair.Server;
            var serialization = server.ResolveDependency<ISerializationManager>();
            return new WritabilityProbe(
                serialization,
                new DrydockCodecContext(serialization, server.EntMan, _ => null, _ => EntityUid.Invalid),
                server.ResolveDependency<IReflectionManager>().Assemblies,
                server.ResolveDependency<IPrototypeManager>());
        }

        /// <summary>
        /// Writes a sample of a type, and of everything a value declared as that type can be, through a serializer under a
        /// context, as the manifest writes a member (<see cref="DrydockCodec.WrittenAs"/>, alwaysWrite). A declaration that
        /// is not sealed is written as the value's own type (SerializationManager.Writing.cs:208-212), so every concrete
        /// type assignable to it is sampled, and a declaration of object fails outright. A collection's sample is empty and
        /// writes whatever it holds, so its element, key and value types are sampled too, and theirs in turn. Not covered:
        /// an open generic subtype, which is never closed to be sampled, and a data definition's fields past its sample's
        /// defaults.
        /// </summary>
        private sealed class WritabilityProbe
        {
            private readonly ISerializationManager _serialization;
            private readonly ISerializationContext _context;
            private readonly IPrototypeManager _prototypes;
            private readonly Lazy<Type[]> _loaded;

            public WritabilityProbe(ISerializationManager serialization, ISerializationContext context, IEnumerable<Assembly> assemblies, IPrototypeManager prototypes)
            {
                _serialization = serialization;
                _context = context;
                _prototypes = prototypes;
                _loaded = new Lazy<Type[]>(() => assemblies
                    .SelectMany(assembly =>
                    {
                        try
                        {
                            return assembly.GetTypes();
                        }
                        catch (ReflectionTypeLoadException e)
                        {
                            return e.Types.OfType<Type>().ToArray();
                        }
                    })
                    .Where(type => !type.IsAbstract && !type.IsInterface && !type.ContainsGenericParameters)
                    .ToArray());
            }

            /// <summary>Samples written.</summary>
            public int Sampled { get; private set; }

            /// <summary>Of those, samples of a concrete type of a declaration that is not sealed.</summary>
            public int Subtypes { get; private set; }

            /// <summary>Why a value declared as <paramref name="declared"/> cannot be written, or null when every type it can be writes.</summary>
            public string? Unwritable(Type declared) => Unwritable(declared, new HashSet<Type>());

            private string? Unwritable(Type declared, HashSet<Type> seen)
            {
                var type = Nullable.GetUnderlyingType(declared) ?? declared;
                if (!seen.Add(type))
                    return null;

                if (type == typeof(object))
                    return "declared as object, which a value of any type can be";

                foreach (var concrete in Concrete(type))
                {
                    var what = concrete == type ? Name(type) : $"{Name(concrete)}, a {Name(type)}";
                    if (Sample(concrete) is not { } sample)
                        return $"no sample of {what} could be made";

                    Sampled++;
                    if (concrete != type)
                        Subtypes++;

                    try
                    {
                        _serialization.WriteValue(declared, sample, alwaysWrite: true, context: _context);
                    }
                    catch (Exception e)
                    {
                        return DrydockSerializationGap.IsNoCoverage(e)
                            ? $"{what} has no serializer or data definition ({e.GetType().Name}: {e.Message})"
                            : $"a sample {what} threw {e.GetType().Name}, which says nothing of the type; give it a better sample ({e.Message})";
                    }
                }

                foreach (var element in Elements(type))
                {
                    if (Unwritable(element, seen) is { } reason)
                        return $"{reason}, held in {Name(type)}";
                }

                return null;
            }

            private IEnumerable<Type> Concrete(Type type)
            {
                if (type.IsValueType || type.IsSealed || type.IsArray)
                    return new[] { type };

                var subtypes = _loaded.Value.Where(t => t != type && type.IsAssignableFrom(t)).ToList();
                return type.IsAbstract || type.IsInterface ? subtypes : subtypes.Prepend(type);
            }

            private static IEnumerable<Type> Elements(Type type)
            {
                if (type.IsArray)
                    return new[] { type.GetElementType()! };

                return type.IsGenericType && typeof(IEnumerable).IsAssignableFrom(type) ? type.GetGenericArguments() : Array.Empty<Type>();
            }

            /// <summary>
            /// An instance to write. A prototype is a registered one, since a member holding a prototype holds a real one and a
            /// constructed prototype has a null id the game never has (a lathe's current recipe, a swab's seed). Anything else
            /// through a constructor where there is one, which runs the field initialisers and so a data definition's
            /// defaults, and otherwise with every field at its zero value.
            /// </summary>
            private object? Sample(Type type)
            {
                if (type == typeof(string))
                    return string.Empty;

                if (type.IsArray)
                    return Array.CreateInstance(type.GetElementType()!, 0);

                if (typeof(IPrototype).IsAssignableFrom(type)
                    && _prototypes.TryGetKindFrom(type, out _)
                    && _prototypes.EnumeratePrototypes(type).FirstOrDefault() is { } registered)
                {
                    return registered;
                }

                try
                {
                    return Activator.CreateInstance(type, nonPublic: true);
                }
                catch (Exception)
                {
                    try
                    {
                        return RuntimeHelpers.GetUninitializedObject(type);
                    }
                    catch (Exception)
                    {
                        return null;
                    }
                }
            }

            private static string Name(Type type) => type.IsGenericType
                ? $"{type.Name[..type.Name.IndexOf('`')]}<{string.Join(", ", type.GetGenericArguments().Select(Name))}>"
                : type.Name;
        }

        private static string? Wrong(IComponentFactory factory, DrydockManifestMember entry)
        {
            if (!factory.TryGetRegistration(entry.Component, out var registration))
                return $"{entry.Component} is not a registered component";

            if (DrydockCodecManifestMembers.Resolve(registration.Type, entry.Member) is not { } member)
                return $"no member {entry.Member} on {registration.Type.Name} or its bases";

            var dataField = member.GetCustomAttribute<DataFieldBaseAttribute>() != null;
            var computed = DrydockCodecManifest.ComputedFields.Any(c => c.Component == registration.Type && c.BackingMember == entry.Member);
            var type = Nullable.GetUnderlyingType(DrydockCodecManifestMembers.MemberType(member)) ?? DrydockCodecManifestMembers.MemberType(member);

            if (entry.Kind != DrydockMemberKind.Entry && (entry.EntryKey != null || entry.EntryType != null))
                return "carries an entry key or type, but is not listed as an entry";

            if (entry.SkipWhen is { } flag
                && (DrydockCodecManifestMembers.Resolve(registration.Type, flag) is not { } flagMember
                    || DrydockCodecManifestMembers.MemberType(flagMember) != typeof(bool)))
            {
                return $"skipped when {flag}, which is not a bool member of {registration.Type.Name}";
            }

            return entry.Kind switch
            {
                DrydockMemberKind.ReapplyCarried when !dataField && !computed =>
                    "listed as re-applied, but nothing carries it: it is neither a data field nor a computed-field backing member",
                DrydockMemberKind.Field or DrydockMemberKind.AbsoluteTime or DrydockMemberKind.ViaSystem or DrydockMemberKind.Reference
                    or DrydockMemberKind.Entry or DrydockMemberKind.Rederive when dataField =>
                    "a data field, which the codec carries already: a census error, or the entry is a re-apply",
                DrydockMemberKind.AbsoluteTime when type != typeof(TimeSpan) =>
                    $"listed as a time, but it is a {type.Name}",
                DrydockMemberKind.Reference when type != typeof(NetEntity) =>
                    $"listed as a network reference, but it is a {type.Name}",
                DrydockMemberKind.Entry when entry.EntryKey == null || entry.EntryType == null =>
                    "listed as a dictionary entry without its key or its value type",
                DrydockMemberKind.Entry when !typeof(IDictionary).IsAssignableFrom(type) =>
                    $"listed as a dictionary entry, but it is a {type.Name}",
                _ => null,
            };
        }
    }
}
