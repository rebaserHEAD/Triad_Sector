using Content.Server.Administration;
using Content.Server.EUI;
using Content.Shared.Administration;
using Robust.Shared.Console;
using Robust.Shared.IoC;

namespace Content.Server._Triad.Drydock.Admin;

[AdminCommand(AdminFlags.Admin)]
public sealed class DrydockAdminCommand : IConsoleCommand
{
    public string Command => "drydockadmin";
    public string Description => "Opens the drydock admin panel: stored ships, berths, history, restore.";
    public string Help => $"Usage: {Command}";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (shell.Player is not { } player)
        {
            shell.WriteError(Loc.GetString("shell-cannot-run-command-from-server"));
            return;
        }

        var eui = IoCManager.Resolve<EuiManager>();
        eui.OpenEui(new DrydockAdminEui(), player);
    }
}
