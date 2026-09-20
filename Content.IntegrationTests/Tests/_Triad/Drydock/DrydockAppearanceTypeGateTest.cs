#nullable enable

using System.Collections.Generic;
using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Server._Triad.Drydock.Loader;
using Content.Shared.APC;
using Content.Shared.Damage;
using Content.Shared.Doors.Components;
using Robust.Shared.Reflection;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// The one gate a stored appearance type name passes: the name comes out of a row, so it may pick a type only when it is
    /// an exact key of the closed table, or resolves through the reflection manager to an enum or a type networked by
    /// attribute. Anything else, above all a type from the framework, is refused and never instantiated.
    /// </summary>
    [TestFixture]
    [TestOf(typeof(DrydockAppearanceTypes))]
    public sealed class DrydockAppearanceTypeGateTest
    {
        /// <summary>Every C# integer and floating-point primitive, bool and string: all inert, all in the table.</summary>
        private static readonly System.Type[] AdmittedPrimitives =
        {
            typeof(bool), typeof(byte), typeof(sbyte), typeof(short), typeof(ushort), typeof(int), typeof(uint),
            typeof(long), typeof(ulong), typeof(float), typeof(double), typeof(string),
        };

        /// <summary>Types that are not in the table, and a few a row might try.</summary>
        private static readonly System.Type[] RefusedTypes =
        {
            typeof(char), typeof(decimal), typeof(System.IntPtr), typeof(System.UIntPtr), typeof(object),
            typeof(System.DateTime), typeof(System.Guid), typeof(System.Type), typeof(System.Delegate),
            typeof(System.Diagnostics.Process), typeof(System.IO.FileInfo),
            typeof(Dictionary<string, object>), typeof(Dictionary<string, Dictionary<string, string>>),
            typeof(Dictionary<int, string>), typeof(Dictionary<string, int>), typeof(List<string>),
        };

        [Test]
        public async Task ANameThatIsNotInTheTableNorAnEnumNorNetworkedIsRefused()
        {
            await using var pair = await PoolManager.GetServerClient();
            var reflection = pair.Server.ResolveDependency<IReflectionManager>();

            Assert.Multiple(() =>
            {
                foreach (var refused in RefusedTypes)
                    Assert.That(DrydockAppearanceTypes.TryResolve(reflection, DrydockAppearanceTypes.KeyOf(refused), out _), Is.False, $"{refused} is not admitted and must be refused.");

                // The assembly-qualified form is not read: nothing writes it, and a second accepted form is one more thing to hold.
                foreach (var type in DrydockAppearanceTypes.Table)
                    Assert.That(DrydockAppearanceTypes.TryResolve(reflection, type.AssemblyQualifiedName!, out _), Is.False, $"The assembly-qualified {type} must be refused.");

                Assert.That(DrydockAppearanceTypes.TryResolve(reflection, typeof(Dictionary<string, string>).AssemblyQualifiedName!, out _), Is.False,
                    "The assembly-qualified dictionary must be refused.");
                Assert.That(DrydockAppearanceTypes.TryResolve(reflection, "System.Collections.Generic.Dictionary`2[System.String,System.Object]", out _), Is.False,
                    "A dictionary of anything but two strings must be refused.");
                Assert.That(DrydockAppearanceTypes.TryResolve(reflection, "System.Diagnostics.Process, System.Diagnostics.Process", out _), Is.False, "A name with a comma must be refused.");
                Assert.That(DrydockAppearanceTypes.TryResolve(reflection, "No.Such.Type", out _), Is.False, "An unknown name must be refused.");
                Assert.That(DrydockAppearanceTypes.TryResolve(reflection, string.Empty, out _), Is.False, "An empty name must be refused.");
            });

            await pair.CleanReturnAsync();
        }

        [Test]
        public async Task AnEnumANetworkedTypeAndEveryTableKeyResolve()
        {
            await using var pair = await PoolManager.GetServerClient();
            var reflection = pair.Server.ResolveDependency<IReflectionManager>();

            Assert.Multiple(() =>
            {
                Assert.That(DrydockAppearanceTypes.TryResolve(reflection, DrydockAppearanceTypes.KeyOf(typeof(ApcChargeState)), out var apc), Is.True, "An appearance enum resolves.");
                Assert.That(apc, Is.EqualTo(typeof(ApcChargeState)));

                Assert.That(DrydockAppearanceTypes.TryResolve(reflection, DrydockAppearanceTypes.KeyOf(typeof(DoorState)), out var door), Is.True);
                Assert.That(door, Is.EqualTo(typeof(DoorState)));

                Assert.That(DrydockAppearanceTypes.TryResolve(reflection, DrydockAppearanceTypes.KeyOf(typeof(DamageVisualizerGroupData)), out var networked), Is.True,
                    "A class networked by attribute resolves.");
                Assert.That(networked, Is.EqualTo(typeof(DamageVisualizerGroupData)));

                // Every table key round-trips: the key the store writes for a table type is a hit on load, which guards a
                // ToString format surprise in one assertion.
                Assert.That(DrydockAppearanceTypes.Table, Is.SupersetOf(AdmittedPrimitives), "Every primitive is in the table.");
                Assert.That(DrydockAppearanceTypes.Table, Does.Contain(typeof(Robust.Shared.Maths.Color)));
                Assert.That(DrydockAppearanceTypes.Table, Does.Contain(typeof(Dictionary<string, string>)), "The one closed generic is in the table.");
                foreach (var type in DrydockAppearanceTypes.Table)
                {
                    Assert.That(DrydockAppearanceTypes.TryResolve(reflection, DrydockAppearanceTypes.KeyOf(type), out var resolved), Is.True, $"{type}'s key resolves.");
                    Assert.That(resolved, Is.EqualTo(type));
                }

                Assert.That(DrydockAppearanceTypes.KeyOf(typeof(Dictionary<string, string>)), Does.Not.Contain("Version"), "A key carries no assembly clause.");
                Assert.That(DrydockAppearanceTypes.IsAdmissible(typeof(ApcChargeState)), Is.True);
                Assert.That(DrydockAppearanceTypes.IsAdmissible(typeof(System.Diagnostics.Process)), Is.False);
            });

            await pair.CleanReturnAsync();
        }
    }
}
