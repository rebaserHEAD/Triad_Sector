#nullable enable

using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Server._Triad.Drydock.Loader;
using Content.Shared.APC;
using Content.Shared.Doors.Components;
using Robust.Shared.Reflection;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// The one gate a stored appearance type name passes: the name comes out of a row, so it may pick a type only when it is
    /// an enum, is networked by attribute, or is one of the plain value types the gate names. Anything else, above all a
    /// type from the framework, is refused and never instantiated.
    /// </summary>
    [TestFixture]
    [TestOf(typeof(DrydockAppearanceTypes))]
    public sealed class DrydockAppearanceTypeGateTest
    {
        [Test]
        public async Task ANameThatIsNotAnEnumNorNetworkedNorNamedIsRefused()
        {
            await using var pair = await PoolManager.GetServerClient();
            var reflection = pair.Server.ResolveDependency<IReflectionManager>();

            Assert.Multiple(() =>
            {
                Assert.That(DrydockAppearanceTypes.TryResolve(reflection, typeof(System.Diagnostics.Process).AssemblyQualifiedName!, out _), Is.False,
                    "A framework type, one that runs a process, must be refused.");
                Assert.That(DrydockAppearanceTypes.TryResolve(reflection, "System.IO.FileInfo", out _), Is.False, "A bare framework name must be refused.");
                Assert.That(DrydockAppearanceTypes.TryResolve(reflection, typeof(System.Collections.Generic.Dictionary<string, object>).AssemblyQualifiedName!, out _), Is.False,
                    "A dictionary of anything but two strings must be refused.");
                Assert.That(DrydockAppearanceTypes.TryResolve(reflection, typeof(System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, string>>).AssemblyQualifiedName!, out _), Is.False,
                    "A dictionary nested in a dictionary must be refused.");
                Assert.That(DrydockAppearanceTypes.TryResolve(reflection, typeof(System.Collections.Generic.Dictionary<int, string>).AssemblyQualifiedName!, out _), Is.False,
                    "A dictionary with another key type must be refused.");
                Assert.That(DrydockAppearanceTypes.TryResolve(reflection, typeof(System.Collections.Generic.List<string>).AssemblyQualifiedName!, out _), Is.False,
                    "Another generic must be refused.");
                Assert.That(DrydockAppearanceTypes.TryResolve(reflection, "No.Such.Type", out _), Is.False, "An unknown name must be refused.");
                Assert.That(DrydockAppearanceTypes.TryResolve(reflection, string.Empty, out _), Is.False, "An empty name must be refused.");
            });

            await pair.CleanReturnAsync();
        }

        [Test]
        public async Task AnEnumANetworkedTypeAndANamedValueTypeResolve()
        {
            await using var pair = await PoolManager.GetServerClient();
            var reflection = pair.Server.ResolveDependency<IReflectionManager>();

            Assert.Multiple(() =>
            {
                Assert.That(DrydockAppearanceTypes.TryResolve(reflection, typeof(ApcChargeState).AssemblyQualifiedName!, out var apc), Is.True, "An appearance enum resolves.");
                Assert.That(apc, Is.EqualTo(typeof(ApcChargeState)));

                Assert.That(DrydockAppearanceTypes.TryResolve(reflection, typeof(DoorState).AssemblyQualifiedName!, out var door), Is.True);
                Assert.That(door, Is.EqualTo(typeof(DoorState)));

                Assert.That(DrydockAppearanceTypes.TryResolve(reflection, typeof(bool).AssemblyQualifiedName!, out var boolean), Is.True, "A primitive the baseline stored resolves.");
                Assert.That(boolean, Is.EqualTo(typeof(bool)));

                Assert.That(DrydockAppearanceTypes.TryResolve(reflection, typeof(Robust.Shared.Maths.Color).AssemblyQualifiedName!, out var color), Is.True, "Color, which is not networked by attribute, resolves by name.");
                Assert.That(color, Is.EqualTo(typeof(Robust.Shared.Maths.Color)));

                Assert.That(DrydockAppearanceTypes.TryResolve(reflection, typeof(System.Collections.Generic.Dictionary<string, string>).AssemblyQualifiedName!, out var layers), Is.True,
                    "The one closed generic, a dictionary of two strings, resolves.");
                Assert.That(layers, Is.EqualTo(typeof(System.Collections.Generic.Dictionary<string, string>)));

                Assert.That(DrydockAppearanceTypes.IsAdmissible(typeof(ApcChargeState)), Is.True);
                Assert.That(DrydockAppearanceTypes.IsAdmissible(typeof(System.Diagnostics.Process)), Is.False);
            });

            await pair.CleanReturnAsync();
        }
    }
}
