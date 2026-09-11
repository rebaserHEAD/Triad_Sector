// Triad: legacy import. Drains the old ship-save system into the drydock. Old saves are files on the
// player's own disk, so this is a conversation: the client offers a manifest of what it holds, the
// server says which of those it will accept, and the payload for the one ship being imported crosses
// the wire only when the player presses the button.
//
// The mode decides consequences, not mechanics. Under enforce the hash is burned, the account's
// import budget is spent and the file is retired to backup; off and notify run the identical import
// and leave the player's disk untouched, so a non-enforcing server can rehearse the real path without
// eating a save that still has to work somewhere else.

using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Content.Server._Triad.Drydock;
using Content.Server._Triad.Shipyard;
using Content.Server.Database;
using Content.Shared._NF.Shipyard;
using Content.Shared._NF.Shipyard.BUI;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared._NF.Shipyard.Events;
using Content.Shared._Triad.CCVar;
using Content.Shared._Triad.Shipyard.Save;
using Content.Shared._Triad.ShipSize;
using Robust.Shared.Map.Components;
using Robust.Shared.Network;
using Robust.Shared.Player;

namespace Content.Server._NF.Shipyard.Systems;

public sealed partial class ShipyardSystem
{
    /// <summary>
    /// The client's inventory of local saves. Judged and cached; the answer reaches the player as the
    /// import list on the drydock tab, never as a per-file verdict, so a modified client cannot use
    /// this to ask which of a pile of forgeries would pass.
    /// </summary>
    private void OnImportManifestMessage(EntityUid uid, ShipyardConsoleComponent component, ShipyardConsoleImportManifestMessage args)
    {
        if (args.Actor is not { Valid: true } player)
            return;

        component.CachedImportables = new();
        component.OfferedImports = new();
        component.ImportOfferAccount = null;

        if (!_configManager.GetCVar(TriadCCVars.DrydockEnabled)
            || !_configManager.GetCVar(TriadCCVars.DrydockImportEnabled))
        {
            RefreshDrydockUi(uid, component, player, (ShipyardConsoleUiKey)args.UiKey);
            return;
        }

        if (!TryGetOperatorAccount(player, out var operatorAccount)
            || DrydockBarsOperator(player, component)
            || !_mind.TryGetMind(player, out _, out var mind)
            || mind.UserId == null)
        {
            RefreshDrydockUi(uid, component, player, (ShipyardConsoleUiKey)args.UiKey);
            return;
        }

        _ = OfferImportsAsync(uid, component, player, operatorAccount, mind.UserId.Value, args.Candidates, (ShipyardConsoleUiKey)args.UiKey);
    }

    /// <summary>
    /// Filters the manifest and publishes what is left. The budget is read once for the whole
    /// manifest rather than per candidate: it bounds how many imports an account may spend, and a
    /// player at their limit is offered nothing rather than offered a list that refuses on click.
    /// </summary>
    private async Task OfferImportsAsync(
        EntityUid uid,
        ShipyardConsoleComponent component,
        EntityUid player,
        Guid operatorAccount,
        NetUserId userId,
        List<DrydockImportCandidate> candidates,
        ShipyardConsoleUiKey uiKey)
    {
        var enforcing = _tamperPolicy.IsEnforcing();
        var remaining = int.MaxValue;

        if (enforcing)
        {
            var budget = _configManager.GetCVar(TriadCCVars.DrydockImportBudget);
            var spent = await _consumedStore.CountForPlayerAsync(operatorAccount, default);
            remaining = Math.Max(0, budget - spent);
        }

        var offered = new List<DrydockImportShipInfo>();
        var accepted = new Dictionary<string, DrydockImportCandidate>();

        foreach (var candidate in candidates)
        {
            if (offered.Count >= remaining)
                break;

            if (string.IsNullOrWhiteSpace(candidate.FileId) || accepted.ContainsKey(candidate.FileId))
                continue;

            if (!_tamperPolicy.ShouldOfferForImport(candidate.PublicKey, userId))
                continue;

            // An already-spent file cannot be filtered out here, because that needs its hash and the
            // manifest carries none. It is refused at import instead, and in practice never reaches
            // that: an enforcing server retires the file to backup, which takes it off this list.
            accepted[candidate.FileId] = candidate;
            offered.Add(new DrydockImportShipInfo(candidate.FileId, candidate.Name, candidate.Appraisal));
        }

        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return;

        component.CachedImportables = offered;
        component.OfferedImports = accepted;
        component.ImportOfferAccount = operatorAccount;

        RefreshDrydockUi(uid, component, player, uiKey);
    }

    private async void OnImportMessage(EntityUid uid, ShipyardConsoleComponent component, ShipyardConsoleImportMessage args)
    {
        if (args.Actor is not { Valid: true } player)
            return;

        // Taken before the first await and given back however this ends, so a second press while the
        // database is being read is refused rather than run alongside the first.
        if (component.ImportInProgress)
        {
            Refuse(uid, component, player, "shipyard-console-import-in-progress");
            return;
        }

        component.ImportInProgress = true;
        try
        {
            await TryImportLegacyShip(uid, component, player, args.FileId, args.YamlData, (ShipyardConsoleUiKey)args.UiKey);
        }
        catch (Exception e)
        {
            _sawmill.Error($"Legacy import threw {e.GetType().Name}: {e.Message}");
        }
        finally
        {
            // Unconditional: a console left marked busy by a throw would refuse every later import
            // with no way back short of a restart.
            component.ImportInProgress = false;
        }
    }

    /// <summary>
    /// Imports one legacy save: stage it, measure it, give it a berth that fits, and file it.
    ///
    /// <para>Order matters and is not arbitrary. The hash is burned only after the store has
    /// succeeded, because a burn before a failure would cost the player the ship and the save both.
    /// The berth is granted before the store because a store needs somewhere to go, and released
    /// again if the store refuses, so a failed import leaves no empty berth behind.</para>
    /// </summary>
    internal async Task<bool> TryImportLegacyShip(
        EntityUid uid,
        ShipyardConsoleComponent component,
        EntityUid player,
        string fileId,
        string yamlData,
        ShipyardConsoleUiKey uiKey)
    {
        if (!_configManager.GetCVar(TriadCCVars.DrydockEnabled)
            || !_configManager.GetCVar(TriadCCVars.DrydockImportEnabled))
        {
            return Refuse(uid, component, player, "shipyard-console-import-disabled");
        }

        if (!TryGetOperatorAccount(player, out var operatorAccount) || DrydockBarsOperator(player, component))
            return Refuse(uid, component, player, "shipyard-console-drydock-faction");

        // The offer is the gate: a file the server did not just list cannot be imported, and a list
        // built for another captain is not this one's to spend.
        if (string.IsNullOrWhiteSpace(fileId)
            || component.ImportOfferAccount != operatorAccount
            || !component.OfferedImports.TryGetValue(fileId, out var candidate))
        {
            return Refuse(uid, component, player, "shipyard-console-import-not-offered");
        }

        if (!_mind.TryGetMind(player, out _, out var mind) || mind.UserId == null)
            return Refuse(uid, component, player, "shipyard-console-import-failed");

        if (!_player.TryGetSessionByEntity(player, out var session))
            return Refuse(uid, component, player, "shipyard-console-import-failed");

        if (string.IsNullOrWhiteSpace(yamlData))
            return Refuse(uid, component, player, "shipyard-tamper-blocked-empty-payload");

        var envelope = AuthenticatedShipFile.FromShipFile(yamlData);
        var hash = envelope.GetHash();

        // The grid document, unwrapped. The loader takes this and not the file: the envelope wraps
        // it alongside the signature and the appraisal, so handing over the whole file makes the
        // loader look for `meta` at the root and throw. This is what the old load path passed too.
        var shipYaml = envelope.ShipYamlString();

        var shipName = shipYaml is { } yaml ? ExtractShipNameFromYaml(yaml) : null;
        if (string.IsNullOrWhiteSpace(shipName))
            shipName = candidate.Name;
        if (string.IsNullOrWhiteSpace(shipName))
            shipName = $"ImportedShip_{DateTime.Now:yyyyMMdd_HHmmss}";

        // The authority, with the payload finally in hand. The manifest filter only decided what to
        // draw; this is the same call the old load path made.
        LoadDecision decision;
        try
        {
            decision = _tamperPolicy.EvaluateLoad(envelope, mind.UserId.Value, shipName);
        }
        catch (Exception ex)
        {
            _sawmill.Warning($"Import EvaluateLoad threw {ex.GetType().Name}; refusing: {ex.Message}");
            decision = new LoadDecision(false, TriadShipyardEventType.LoadRejectedInvalidSignature, "shipyard-tamper-blocked-invalid-signature");
        }

        if (!decision.Allow)
        {
            _ = _tamperPolicy.RecordLoadAsync(
                envelope, mind.UserId.Value, session.Name, shipName, decision.ResolvedEvent,
                loadTimeAppraisal: null,
                roundId: DrydockRoundId, serverName: null, vesselId: null, mapId: null,
                sourceFilePath: fileId, deedHolderEntity: null);
            return Refuse(uid, component, player, decision.PopupReasonLocId ?? "shipyard-console-load-blocked-tamper");
        }

        var enforcing = _tamperPolicy.IsEnforcing();

        // Replay protection is not the tamper policy's business. The gate above is: it decides
        // whether a document is trusted enough to load at all. These two decide whether a file that
        // has already bought a ship can buy another, and whether this account has any import
        // allowance left, and both answers hold whatever mode the signature check is in.
        //
        // Gating them behind enforcement is what let a notify-mode server hand out the same hull
        // over and over: the console drops a spent file from its own cache, but the client re-sends
        // its whole manifest every time the UI opens, so the file came straight back and imported
        // again for free. Reported from the 2026-09-07 play test with the same cruiser listed four
        // times over.
        if (await _consumedStore.IsConsumedAsync(hash, default))
            return await RefuseAsync(uid, component, player, uiKey, "shipyard-console-import-already-imported");

        var budget = _configManager.GetCVar(TriadCCVars.DrydockImportBudget);
        if (await _consumedStore.CountForPlayerAsync(operatorAccount, default) >= budget)
            return await RefuseAsync(uid, component, player, uiKey, "shipyard-console-import-budget-spent");

        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return false;

        // Stage it the way a purchase does. This is the only way to learn the hull's size: a class is
        // its built tile count, and nothing in the envelope carries one.
        if (string.IsNullOrWhiteSpace(shipYaml)
            || !TryPurchaseShuttleFromYamlData(uid, shipYaml, out var shuttleUid)
            || shuttleUid is not { } grid)
        {
            return Refuse(uid, component, player, "shipyard-console-import-load-failed");
        }

        if (!TryComp<MapGridComponent>(grid, out var mapGrid))
        {
            QueueDel(grid);
            return Refuse(uid, component, player, "shipyard-console-import-load-failed");
        }

        // A file that already carries a drydock identity describes a hull the drydock has filed.
        // Importing it would file a revision onto that row and, for a hull that is out, mark the
        // row stored while the original flies: the duplicate the design refuses outright.
        if (HasComp<DrydockIdentityComponent>(grid))
        {
            QueueDel(grid);
            return Refuse(uid, component, player, "shipyard-console-import-drydock-identity");
        }

        var sizeClass = _drydockSizes.GetSizeClass((grid, mapGrid));

        var berthId = await _drydockStore.AddBerth(
            operatorAccount, sizeClass, DrydockBerthKind.Granted, 0, operatorAccount, DrydockRoundId);

        var result = await _drydock.TryStoreShip(grid, operatorAccount, DrydockRoundId, berthId);

        if (result.Result != DrydockStoreResult.Success)
        {
            // The store refused, so the grid is still ours to clean up and the berth was never used.
            if (!TerminatingOrDeleted(grid))
                QueueDel(grid);
            await _drydockStore.TryRemoveBerth(berthId, operatorAccount, DrydockAuditAction.BerthDelete, operatorAccount, DrydockRoundId);

            if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
                return false;

            return await RefuseAsync(uid, component, player, uiKey, StoreRefusalLoc(result.Result));
        }

        // Filed, so the save is spent. Unconditionally: the ship this produced is real and
        // retrievable whatever the tamper mode, so the file that bought it has to be marked gone.
        if (!await _consumedStore.TryConsumeAsync(hash, operatorAccount, result.ShipId, shipName, DrydockRoundId, default))
        {
            // Another import of the same file won the race. The ship is already filed under that
            // one, so this is a log line rather than a rollback: the unique index did its job.
            _sawmill.Warning($"Import of '{shipName}' lost the consume race; the hash was already spent.");
        }

        // Retire the local copy the way the old load path did, so the menu stops offering a file
        // that can no longer be imported. The ledger above is the authority and refuses a replay on
        // its own; this only keeps the player from being shown a file that would now be refused.
        if (!TerminatingOrDeleted(player))
            RaiseNetworkEvent(new DeleteLocalShipFileMessage(fileId), session);

        _ = _tamperPolicy.RecordLoadAsync(
            envelope, mind.UserId.Value, session.Name, shipName, decision.ResolvedEvent,
            loadTimeAppraisal: null,
            roundId: DrydockRoundId, serverName: null, vesselId: null, mapId: null,
            sourceFilePath: fileId, deedHolderEntity: null);

        // The drydock's own timeline records the import whatever the tamper mode, because the tamper
        // audit stops writing entirely when the check is off and an import must still leave a trace.
        await _drydockStore.WriteAudit(new DrydockAudit
        {
            CreatedAt = DateTime.UtcNow,
            Action = DrydockAuditAction.Imported,
            ShipGuid = result.ShipId,
            ShipName = shipName,
            ActorUserId = operatorAccount,
            BerthId = berthId,
            RoundId = DrydockRoundId,
            Reason = enforcing ? null : "tamper check not enforcing",
        });

        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return true;

        // The file is spent, so drop it from the offer before the refresh redraws the list.
        component.OfferedImports.Remove(fileId);
        component.CachedImportables = component.CachedImportables.Where(i => i.FileId != fileId).ToList();

        ConsolePopup(player, Loc.GetString("shipyard-console-import-success", ("ship", shipName)));
        PlayConfirmSound(player, uid, component);
        await RefreshDrydockState(uid, component, player, uiKey);
        return true;
    }

    private bool Refuse(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, string locId)
    {
        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return false;

        ConsolePopup(player, Loc.GetString(locId));
        PlayDenySound(player, uid, component);
        return false;
    }

    /// <summary>A refusal that also redraws, for the paths that have already read the database.</summary>
    private async Task<bool> RefuseAsync(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, ShipyardConsoleUiKey uiKey, string locId)
    {
        Refuse(uid, component, player, locId);
        await RefreshAfterRefusal(uid, component, player, uiKey);
        return false;
    }
}
