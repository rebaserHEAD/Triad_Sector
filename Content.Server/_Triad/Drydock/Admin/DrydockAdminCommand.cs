using Content.Server.Administration;
using Content.Server.EUI;
using Content.Shared.Administration;
using Robust.Shared.Toolshed;
using Robust.Shared.Toolshed.Errors;

namespace Content.Server._Triad.Drydock.Admin;

/// <summary>
/// Opens the drydock admin panel for the admin who ran it. The Admin menu's Drydock button runs
/// this by name; the description and help ride the command attributes, so there is no locale
/// entry to keep in step with them.
/// </summary>
[ToolshedCommand(Name = "drydockadmin"), AdminCommand(AdminFlags.Admin)]
public sealed partial class DrydockAdminCommand : ToolshedCommand
{
    [Dependency] private EuiManager _eui = default!;

    [CommandImplementation]
    [CommandDescription("Opens the drydock admin panel: stored ships, berths, history, restore.")]
    [CommandHelp("Usage: drydockadmin")]
    public void Open([CommandInvocationContext] IInvocationContext ctx)
    {
        if (ctx.Session is not { } player)
        {
            ctx.ReportError(new NotForServerConsoleError());
            return;
        }

        _eui.OpenEui(new DrydockAdminEui(), player);
    }
}
