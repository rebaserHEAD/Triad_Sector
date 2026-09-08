using System;
using System.Collections.Generic;
using System.IO;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Content.Server._HL.Shipyard;

/// <summary>
/// The inbound scrub for legacy ship files: strips nodes a current build would never write, on the
/// file's way in. Load-side only, the save half having gone with the system that called it.
/// </summary>
public static class ShipSaveYamlSanitizer
{
    // Triad: serialized entity references outside ContainerContainer and Storage, which are the only
    // two the prune below originally knew about. The old save path dropped whole entities on its way
    // out, such as a mob filtered out for carrying HumanoidAppearance while still buckled to a chair,
    // and left their uids behind in these fields; the loader then reported an invalid EntityUid
    // reference for that component. Legacy files still carry those uids, so the prune stays.
    //
    // Unordered sets and lists: the entry is removed outright.
    private static readonly Dictionary<string, string> UidSetFields = new(StringComparer.Ordinal)
    {
        ["EmbeddedContainer"] = "embeddedObjects",
        ["Strap"] = "buckledEntities",
        ["ShipGuestAccess"] = "guestIdCards",
        ["FactionException"] = "ignored",
        ["TargetSeekerAlertGrid"] = "alerters",
    };

    // Components carrying a second uid collection, since the table above is keyed by component name.
    private static readonly Dictionary<string, string> UidSetFieldsSecondary = new(StringComparer.Ordinal)
    {
        ["ShipGuestAccess"] = "guestCyborgs",
        ["FactionException"] = "hostiles",
    };

    // Positional sequences: the entry is nulled in place, never removed. A revolver's ammo slots are
    // indexed by cylinder position and the component asserts the count matches its capacity on init,
    // so shortening the sequence would rotate every remaining round onto the wrong chamber.
    private static readonly Dictionary<string, string> UidSlotFields = new(StringComparer.Ordinal)
    {
        ["RevolverAmmoProvider"] = "ammoSlots",
    };

    // Single optional references: the field is nulled. Only nullable fields belong here.
    // Action entities live in the acting mob's container, and the mob is not what gets saved, so these
    // are dangling by construction on any ship that had someone holding the item when it was saved.
    private static readonly Dictionary<string, string[]> UidScalarFields = new(StringComparer.Ordinal)
    {
        ["EmbeddableProjectile"] = ["embeddedIntoUid"],
        ["CombatMode"] = ["combatToggleActionEntity"],
        ["HealOnBuckle"] = ["sleepAction"],
        ["HandheldLight"] = ["toggleActionEntity", "selfToggleActionEntity"],
        ["ShuttleConsoleJobSlots"] = ["owningStation"],
        ["StoreRefund"] = ["storeEntity"],
        ["Jukebox"] = ["audioStream"],
        ["Organ"] = ["body", "originalBody"],
        // Triad: an ore silo is station infrastructure, so a lathe saved with a ship always points at
        // a silo that stays behind. Nullable, so the client just loads unlinked and re-links on use.
        ["OreSiloClient"] = ["silo"],
    };

    // Non-nullable references, where nulling the field would just trade one load error for another.
    // The reference is the component's whole reason to exist, so if it points at nothing the component
    // is meaningless and goes with it. VendingMachinePurchase records which grid bought from a machine;
    // a purchase belonging to a grid that is not in the save cannot be acted on.
    private static readonly Dictionary<string, string> UidRequiredFieldsDropComponent = new(StringComparer.Ordinal)
    {
        ["VendingMachinePurchase"] = "purchaseGrid",
    };

    // Entity prototypes removed by the 2026-08 xenoarchaeology rework whose entities are dropped from
    // a ship file on load. Only the legacy grant action lives here: the artifact bodies themselves are
    // renamed to their node-graph replacements by triad_migration.yml instead, which this scrub must
    // not duplicate. Dropping the group here (rather than leaving it to the migration's null entry)
    // lets the reference prune below clean up the container slots that pointed at it.
    private static readonly HashSet<string> LegacyRemovedEntityPrototypes = new(StringComparer.Ordinal)
    {
        "ActionArtifactActivate",
    };

    // Component types the 2026-08 xenoarchaeology rework deleted from the codebase. A ship saved on
    // the legacy build can carry these nodes on its (now renamed) artifacts; the deserializer skips an
    // unregistered component anyway, this just does it without an error line per node. Four legacy
    // names were re-registered by the rework with new data shapes (AnalysisConsole, ArtifactAnalyzer,
    // NodeScanner, SuppressArtifactContainer) and are deliberately absent: those are live components
    // on current saves and their nodes must reach the loader.
    private static readonly HashSet<string> LegacyRemovedComponentTypes = new(StringComparer.Ordinal)
    {
        "ActiveArtifactAnalyzer",
        "ActiveScannedArtifact",
        "Artifact",
        "ArtifactAnchorTrigger",
        "ArtifactDamageTrigger",
        "ArtifactDeathTrigger",
        "ArtifactElectricityTrigger",
        "ArtifactExamineTrigger",
        "ArtifactGasTrigger",
        "ArtifactHeatTrigger",
        "ArtifactInteractionTrigger",
        "ArtifactLandTrigger",
        "ArtifactMagnetTrigger",
        "ArtifactMicrowaveTrigger",
        "ArtifactMusicTrigger",
        "ArtifactPressureTrigger",
        "ArtifactTimerTrigger",
        "BiasedArtifact",
        "ChargeBatteryArtifact",
        "ChemicalPuddleArtifact",
        "DamageNearbyArtifact",
        "EmpArtifact",
        "FoamArtifact",
        "GasArtifact",
        "IgniteArtifact",
        "KnockArtifact",
        "LightFlickerArtifact",
        "PhasingArtifact",
        "PolyOthersArtifact",
        "PortalArtifact",
        "RandomInstrumentArtifact",
        "RandomTeleportArtifact",
        "ShuffleArtifact",
        "SpawnArtifact",
        "TelepathicArtifact",
        "TemperatureArtifact",
        "ThrowArtifact",
        "TraversalDistorter",
        "TriggerArtifact",
    };

    /// <summary>
    /// Strips stale nodes from a ship file on its way in: entities of removed legacy prototypes,
    /// component nodes of removed legacy component types, and dangling entity references. Returns how
    /// many nodes were removed.
    /// </summary>
    // Triad: ship files live on the player's machine and come back to us at load time, so inbound is
    // the only place an old one can be fixed. Files months old are still being loaded; a file the
    // current build wrote has nothing to remove and returns 0.
    //
    // The prune's dangling test is "uid is not present in this file", which already covers the literal
    // 'invalid' the writer emits for a reference it could not resolve, since that is never a uid the
    // file defines. Nothing removed here was reachable: the deserializer resolves both cases to
    // EntityUid.Invalid regardless, it just logs an error per occurrence on the way.
    //
    // The legacy strip runs first so the uids of dropped legacy entities feed the same prune, clearing
    // the container slots that held them (a legacy artifact's grant action sits in its 'actions'
    // container). Prototype RENAMES are deliberately not handled here: triad_migration.yml applies
    // them inside the engine's deserializer, for this path and every other map load alike.
    public static int ScrubShipLoadNode(MappingDataNode root)
    {
        if (!root.TryGet("entities", out SequenceDataNode? protoSeq) || protoSeq == null)
            return 0;

        var removedEntityUids = new HashSet<string>(StringComparer.Ordinal);
        var scrubbed = StripLegacyRemovedNodes(protoSeq, removedEntityUids);
        return scrubbed + PruneContainerReferencesToRemovedEntities(protoSeq, removedEntityUids);
    }

    /// <summary>
    /// Removes whole entity groups whose prototype no longer exists and component nodes whose type no
    /// longer exists, collecting the removed entities' uids so the caller can prune references to
    /// them. Returns how many nodes were removed.
    /// </summary>
    private static int StripLegacyRemovedNodes(SequenceDataNode protoSeq, HashSet<string> removedEntityUids)
    {
        var scrubbed = 0;

        for (var protoIdx = protoSeq.Count - 1; protoIdx >= 0; protoIdx--)
        {
            if (protoSeq[protoIdx] is not MappingDataNode protoMap)
                continue;

            if (!protoMap.TryGet("entities", out SequenceDataNode? entitiesSeq) || entitiesSeq == null)
                continue;

            if (protoMap.TryGet("proto", out ValueDataNode? protoIdNode)
                && protoIdNode != null
                && !protoIdNode.IsNull
                && LegacyRemovedEntityPrototypes.Contains(protoIdNode.Value))
            {
                foreach (var entityNode in entitiesSeq)
                {
                    if (entityNode is MappingDataNode entMap
                        && entMap.TryGet("uid", out ValueDataNode? uidNode)
                        && uidNode != null
                        && !uidNode.IsNull)
                    {
                        removedEntityUids.Add(uidNode.Value);
                    }

                    scrubbed++;
                }

                protoSeq.RemoveAt(protoIdx);
                continue;
            }

            foreach (var entityNode in entitiesSeq)
            {
                if (entityNode is not MappingDataNode entMap)
                    continue;

                if (!entMap.TryGet("components", out SequenceDataNode? comps) || comps == null)
                    continue;

                for (var compIdx = comps.Count - 1; compIdx >= 0; compIdx--)
                {
                    if (comps[compIdx] is not MappingDataNode compMap)
                        continue;

                    if (!compMap.TryGet("type", out ValueDataNode? typeNode) || typeNode == null)
                        continue;

                    if (!LegacyRemovedComponentTypes.Contains(typeNode.Value))
                        continue;

                    comps.RemoveAt(compIdx);
                    scrubbed++;
                }
            }
        }

        return scrubbed;
    }

    /// <summary>
    /// Parses a ship file, strips removed legacy entities and components plus dangling entity
    /// references, and re-emits it. Returns the text unchanged when there is nothing to remove or
    /// when it cannot be parsed.
    /// </summary>
    public static string ScrubShipLoadYaml(string yaml, out int scrubbed)
    {
        scrubbed = 0;

        try
        {
            using var reader = new StringReader(yaml);

            DataNode? parsed = null;
            var documents = 0;
            foreach (var document in DataNodeParser.ParseYamlStream(reader))
            {
                parsed = document.Root;
                // Ship files are single-document. Anything else is a shape this never produced, so
                // leave it for the loader to deal with rather than re-emitting a guess at it.
                if (++documents > 1)
                    return yaml;
            }

            if (documents != 1 || parsed is not MappingDataNode root)
                return yaml;

            scrubbed = ScrubShipLoadNode(root);
            if (scrubbed == 0)
                return yaml;

            using var writer = new StringWriter();
            var stream = new YamlStream { new YamlDocument(root.ToYaml()) };
            stream.Save(new YamlMappingFix(new Emitter(writer)), false);
            return writer.ToString();
        }
        catch
        {
            // A ship the owner cannot load is far worse than the log noise this removes, so any parse
            // or emit failure hands the loader the original text and gives up on the scrub.
            scrubbed = 0;
            return yaml;
        }
    }

    /// <summary>
    /// Drops dangling entries from a serialized sequence of entity uids. Returns how many were dropped.
    /// </summary>
    private static int PruneUidSequence(MappingDataNode compMap, string field, Func<string, bool> isDangling)
    {
        if (!compMap.TryGet(field, out SequenceDataNode? seq) || seq == null)
            return 0;

        var pruned = 0;
        for (var idx = seq.Count - 1; idx >= 0; idx--)
        {
            if (seq[idx] is ValueDataNode entry && !entry.IsNull && isDangling(entry.Value))
            {
                seq.RemoveAt(idx);
                pruned++;
            }
        }

        return pruned;
    }

    /// <summary>
    /// Collects every entity uid that actually survives into the exported file.
    /// </summary>
    private static HashSet<string> CollectPresentEntityUids(SequenceDataNode protoSeq)
    {
        var present = new HashSet<string>(StringComparer.Ordinal);

        foreach (var protoNode in protoSeq)
        {
            if (protoNode is not MappingDataNode protoMap)
                continue;

            if (!protoMap.TryGet("entities", out SequenceDataNode? entitiesSeq) || entitiesSeq == null)
                continue;

            foreach (var entityNode in entitiesSeq)
            {
                if (entityNode is MappingDataNode entMap
                    && entMap.TryGet("uid", out ValueDataNode? uidNode)
                    && uidNode != null
                    && !uidNode.IsNull)
                {
                    present.Add(uidNode.Value);
                }
            }
        }

        return present;
    }

    private static int PruneContainerReferencesToRemovedEntities(SequenceDataNode protoSeq, HashSet<string> removedEntityUids)
    {
        var scrubbed = 0;

        // Triad: this used to prune only what the sanitizer itself dropped, which missed the larger
        // half of the problem. Most dangling references point at something that was never a candidate
        // for the file at all: the station a console is registered to, the mob whose container held an
        // action entity, an audio stream, a grid on the other side of the sector. Asking "is this uid
        // in the finished file" covers both cases at once, and the removed set is a subset of it.
        var presentUids = CollectPresentEntityUids(protoSeq);

        // The removed set is already a subset of "not present", since dropping an entity takes its uid
        // entry with it. It stays in the check so the sanitizer's own removals remain explicit rather
        // than depending on that reasoning holding for every future removal path.
        bool IsDangling(string uid) => removedEntityUids.Contains(uid) || !presentUids.Contains(uid);

        // Remove dangling references from both ContainerContainer and Storage serialized structures.
        foreach (var protoNode in protoSeq)
        {
            if (protoNode is not MappingDataNode protoMap)
                continue;

            if (!protoMap.TryGet("entities", out SequenceDataNode? entitiesSeq) || entitiesSeq == null)
                continue;

            foreach (var entityNode in entitiesSeq)
            {
                if (entityNode is not MappingDataNode entMap)
                    continue;

                if (!entMap.TryGet("components", out SequenceDataNode? comps) || comps == null)
                    continue;

                // Triad: descending, because the required-reference case removes the component itself.
                for (var compIdx = comps.Count - 1; compIdx >= 0; compIdx--)
                {
                    if (comps[compIdx] is not MappingDataNode compMap)
                        continue;

                    if (!compMap.TryGet("type", out ValueDataNode? typeNode) || typeNode == null)
                        continue;

                    var componentType = typeNode.Value;

                    if (componentType == "ContainerContainer")
                    {
                        if (!compMap.TryGet("containers", out MappingDataNode? containersMap) || containersMap == null)
                            continue;

                        foreach (var (_, containerNode) in containersMap)
                        {
                            if (containerNode is not MappingDataNode containerMap)
                                continue;

                            if (containerMap.TryGet("ents", out SequenceDataNode? entsNode) && entsNode != null)
                            {
                                for (var idx = entsNode.Count - 1; idx >= 0; idx--)
                                {
                                    if (entsNode[idx] is not ValueDataNode entValue || entValue.IsNull)
                                        continue;

                                    if (IsDangling(entValue.Value))
                                    {
                                        entsNode.RemoveAt(idx);
                                        scrubbed++;
                                    }
                                }
                            }

                            if (containerMap.TryGet("ent", out ValueDataNode? entNode) && entNode != null && !entNode.IsNull)
                            {
                                if (IsDangling(entNode.Value))
                                {
                                    containerMap["ent"] = ValueDataNode.Null();
                                    scrubbed++;
                                }
                            }
                        }

                        continue;
                    }

                    if (componentType == "Storage" && compMap.TryGet("storedItems", out MappingDataNode? storedItemsMap) && storedItemsMap != null)
                    {
                        var removeKeys = new List<string>();
                        foreach (var (itemUid, _) in storedItemsMap)
                        {
                            if (IsDangling(itemUid))
                                removeKeys.Add(itemUid);
                        }

                        foreach (var key in removeKeys)
                        {
                            storedItemsMap.Remove(key);
                            scrubbed++;
                        }

                        continue;
                    }

                    // Triad: the reference shapes described above the field tables.
                    if (UidRequiredFieldsDropComponent.TryGetValue(componentType, out var requiredField)
                        && compMap.TryGet(requiredField, out ValueDataNode? requiredNode)
                        && requiredNode != null
                        && !requiredNode.IsNull
                        && IsDangling(requiredNode.Value))
                    {
                        comps.RemoveAt(compIdx);
                        scrubbed++;
                        continue;
                    }

                    if (UidSetFields.TryGetValue(componentType, out var setField))
                        scrubbed += PruneUidSequence(compMap, setField, IsDangling);

                    if (UidSetFieldsSecondary.TryGetValue(componentType, out var setField2))
                        scrubbed += PruneUidSequence(compMap, setField2, IsDangling);

                    if (UidSlotFields.TryGetValue(componentType, out var slotField)
                        && compMap.TryGet(slotField, out SequenceDataNode? slotSeq)
                        && slotSeq != null)
                    {
                        for (var idx = 0; idx < slotSeq.Count; idx++)
                        {
                            if (slotSeq[idx] is ValueDataNode entry && !entry.IsNull && IsDangling(entry.Value))
                            {
                                slotSeq[idx] = ValueDataNode.Null();
                                scrubbed++;
                            }
                        }
                    }

                    if (!UidScalarFields.TryGetValue(componentType, out var scalarFields))
                        continue;

                    foreach (var scalarField in scalarFields)
                    {
                        if (compMap.TryGet(scalarField, out ValueDataNode? scalarNode)
                            && scalarNode != null
                            && !scalarNode.IsNull
                            && IsDangling(scalarNode.Value))
                        {
                            compMap[scalarField] = ValueDataNode.Null();
                            scrubbed++;
                        }
                    }
                }
            }
        }

        return scrubbed;
    }
}
