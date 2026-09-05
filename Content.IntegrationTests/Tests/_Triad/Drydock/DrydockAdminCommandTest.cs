#nullable enable

using System.Threading.Tasks;
using Robust.Shared.Console;
using Robust.Shared.Toolshed;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// The Admin menu's Drydock button runs <c>drydockadmin</c> by name, so the command has to be
    /// registered on the server and announced to the client, or the button is a dead end. It is a
    /// Toolshed command whose description rides the command attribute rather than a locale key,
    /// which is what the second assertion reads back.
    /// </summary>
    [TestFixture]
    public sealed class DrydockAdminCommandTest
    {
        [Test]
        public async Task TheCommandIsRegisteredAndCarriesItsDescription()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
            await pair.RunTicksSync(5);

            var toolshed = pair.Server.ResolveDependency<ToolshedManager>();
            var clientConsole = pair.Client.ResolveDependency<IConsoleHost>();

            Assert.Multiple(() =>
            {
                Assert.That(toolshed.DefaultEnvironment.TryGetCommand("drydockadmin", out var command), Is.True,
                    "The server registers drydockadmin as a Toolshed command.");
                Assert.That(command?.Description(null),
                    Is.EqualTo("Opens the drydock admin panel: stored ships, berths, history, restore."),
                    "The description comes from the command attribute, not from a locale entry.");
                Assert.That(clientConsole.AvailableCommands.ContainsKey("drydockadmin"), Is.True,
                    "The client learned the command from the server, so the Admin menu button has a target.");

                // Control: a name nothing registers is absent on both sides.
                Assert.That(toolshed.DefaultEnvironment.TryGetCommand("drydocknotacommand", out _), Is.False);
                Assert.That(clientConsole.AvailableCommands.ContainsKey("drydocknotacommand"), Is.False);
            });

            await pair.CleanReturnAsync();
        }
    }
}
