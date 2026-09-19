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
    /// The build-time guard of <see cref="DrydockCodecManifestMembers"/>. A third of its entries are on components no sold
    /// hull carries, which no ladder rung can exercise, so for those this is the only check that a rename upstream has not
    /// quietly dropped one. Every entry has to name a registered component and a member that resolves on it, and has to be
    /// the kind it says: a member the manifest carries must not be a data field (the codec carries those already, and the
    /// entry would be a census error), a re-applied one must be carried already, and a time must be a time.
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

            await TestContext.Out.WriteLineAsync(
                $"[manifest] {resolved} of {DrydockCodecManifestMembers.Members.Length} member entries resolved as their kind; "
                + $"{DrydockCodecManifestMembers.NotCarried.Length} not carried, {DrydockCodecManifestMembers.Stripped.Count} stripped components checked.");
            foreach (var line in wrong)
                await TestContext.Out.WriteLineAsync($"[manifest]   {line}");

            Assert.That(wrong, Is.Empty, "Every manifest entry has to resolve as the kind it says; the list above names each that does not.");

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
            });

            await pair.CleanReturnAsync();
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

            return entry.Kind switch
            {
                DrydockMemberKind.ReapplyCarried when !dataField && !computed =>
                    "listed as re-applied, but nothing carries it: it is neither a data field nor a computed-field backing member",
                DrydockMemberKind.Field or DrydockMemberKind.AbsoluteTime or DrydockMemberKind.ViaSystem when dataField =>
                    "a data field, which the codec carries already: a census error, or the entry is a re-apply",
                DrydockMemberKind.AbsoluteTime when type != typeof(TimeSpan) =>
                    $"listed as a time, but it is a {type.Name}",
                _ => null,
            };
        }
    }
}
