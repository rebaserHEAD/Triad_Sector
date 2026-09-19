using System.Collections.Immutable;
using System.Reflection;
using Content.Shared.Power;

namespace Content.Server._Triad.Drydock.Codec;

/// <summary>When a manifest member is set on a loading entity, which is when nothing the load runs afterwards undoes it.</summary>
public enum DrydockApplyMoment
{
    /// <summary>With the rows, before any init: the member is only read later, by a handler that trusts it.</summary>
    BeforeInit,

    /// <summary>Between the entity's init and its startup (<c>EntityInitialized</c>): an init handler resets it.</summary>
    Seam,

    /// <summary>After <c>StartEntities</c>: a startup handler resets it.</summary>
    AfterStart,

    /// <summary>After the first power solve: a power-edge handler resets it.</summary>
    AfterPowerSolve,
}

/// <summary>How a manifest member travels.</summary>
public enum DrydockMemberKind
{
    /// <summary>Written through the serializer under the codec's context and set back as the value; an entity reference
    /// becomes a stable id on the way, as every reference in a row does.</summary>
    Field,

    /// <summary>An absolute game time, carried through the time-offset adapter the field pass uses.</summary>
    AbsoluteTime,

    /// <summary>Already carried by the codec (a data field, or the computed-field manifest); not written again, only set
    /// back at its moment to what the rows restored, because a handler overwrites it before then.</summary>
    ReapplyCarried,

    /// <summary>A reference kept on both sides (the receiver's provider and the provider's receiver list), set through the
    /// owning system rather than by a field write.</summary>
    ViaSystem,

    /// <summary>A <see cref="NetEntity"/> kept on a component, carried as the entity it names: written as that entity's
    /// stable id and set back as the loaded entity's NetEntity, or null when it names nothing on the image.</summary>
    Reference,

    /// <summary>Not carried: the loader works it out again from the loaded entity at its moment (a scuttle device's
    /// armed map, from its transform). The row holds only a marker, so the loader knows the member is owed.</summary>
    Rederive,

    /// <summary>One entry of a dictionary member that is otherwise not carried (a wire's state data), keyed by
    /// <see cref="DrydockManifestMember.EntryKey"/> and holding a <see cref="DrydockManifestMember.EntryType"/>. An entry
    /// the dictionary does not hold is not written, so the load leaves it absent as well.</summary>
    Entry,
}

/// <param name="Row">The census join's row id (resources/2026-09-17-census-join.tsv, column f33).</param>
/// <param name="Component">The component's registration name.</param>
/// <param name="Member">The field or property on the component or one of its base types.</param>
/// <param name="OnlyWith">A component the entity must also carry for the member to travel: a fried item's name is the
/// member, and every entity has a name.</param>
/// <param name="EntryKey">For an <see cref="DrydockMemberKind.Entry"/>, the dictionary key.</param>
/// <param name="EntryType">For an <see cref="DrydockMemberKind.Entry"/>, the type of the value under the key.</param>
/// <param name="OwedWith">The rebuild handler (resources/2026-09-18-rebuild-list.tsv) the member waits for. An owed member
/// is listed so the build-time test keeps guarding it, and is not written until the handler exists.</param>
/// <param name="SkipWhen">A bool member of the same component; while it is true the member is not written, so the loaded
/// entity keeps its default. A scuttle device that disarms on a map change comes back disarmed by never being armed.</param>
public sealed record DrydockManifestMember(
    int Row,
    string Component,
    string Member,
    DrydockApplyMoment Moment,
    DrydockMemberKind Kind,
    string? OnlyWith = null,
    Enum? EntryKey = null,
    Type? EntryType = null,
    string? OwedWith = null,
    string? SkipWhen = null)
{
    /// <summary>The member's key in a manifest row: <c>Component.Member</c>, and the entry's key after it for an entry.</summary>
    public string Key => EntryKey == null
        ? $"{Component}.{Member}"
        : $"{Component}.{Member}[{EntryKey.GetType().Name}.{EntryKey}]";
}

/// <param name="Reason">Why it is not carried, which is the whole point of listing it.</param>
public sealed record DrydockNotCarried(int Row, string Component, string Member, string Reason);

/// <summary>
/// The members of a component that are not data fields and that a restored ship has to have back (finding F33), each
/// with the moment it survives being set at. Built from the census join's f33 column, one entry per member, the row id
/// beside it; the column means exactly "in this list", and a member leaving or joining changes both. The build-time test
/// resolves every entry against its component and fails on a member that is gone, one that has become a data field while
/// listed as carried by this manifest, or a component that is no longer registered.
///
/// <para>Out by scope (the image carries no mobs): rows 316, 449, 450, 452, 454, 505 and 508. Out by verdict: rows 104,
/// 399, 400, and 498 (a worn stethoscope goes ashore with its wearer), and row 507's music state, listed in
/// <see cref="NotCarried"/>. Out as unreachable: row 506, whose console no prototype carries. Out as not reproduced:
/// row 475, a disposal unit's next flush, which only a power-off edge would lose (SharedDisposalUnitSystem.cs:249-252)
/// and the load path has none, while the on edge's ManualEngage keeps the smaller of the carried time and a fresh one
/// (:686). Row 453 is a data field list the codec carries.</para>
/// </summary>
public static class DrydockCodecManifestMembers
{
    private const DrydockApplyMoment Before = DrydockApplyMoment.BeforeInit;
    private const DrydockMemberKind Field = DrydockMemberKind.Field;
    private const DrydockMemberKind Time = DrydockMemberKind.AbsoluteTime;

    public static readonly ImmutableArray<DrydockManifestMember> Members = ImmutableArray.Create(
        // clobber:init, at the seam.
        new DrydockManifestMember(184, "Crayon", "SelectedState", DrydockApplyMoment.Seam, Field),
        new DrydockManifestMember(183, "Crayon", "Charges", DrydockApplyMoment.Seam, Field),
        new DrydockManifestMember(223, "ExpendableLight", "CurrentState", DrydockApplyMoment.Seam, Field),
        // The fry handler takes the current name as the original and prefixes it again (DeepFryerSystem.cs:746-751).
        new DrydockManifestMember(448, "DeepFried", "OriginalName", DrydockApplyMoment.Seam, DrydockMemberKind.ReapplyCarried),
        new DrydockManifestMember(448, "MetaData", "EntityName", DrydockApplyMoment.Seam, DrydockMemberKind.ViaSystem, OnlyWith: "DeepFried"),
        // Init also re-arms a Timer.Spawn at the full duration (CargoSystem.TradeCrates.cs:77-80), which this does not
        // re-arm; the remedy is a marked edit leaving a stored deadline alone, after which before-init is right.
        new DrydockManifestMember(456, "TradeCrate", "ExpressDeliveryTime", DrydockApplyMoment.Seam, Time),

        // clobber:power-edge, after the first power solve. All three are carried already and only re-applied.
        new DrydockManifestMember(476, "CargoTelepad", "CurrentState", DrydockApplyMoment.AfterPowerSolve, DrydockMemberKind.ReapplyCarried),
        new DrydockManifestMember(474, "DeepFryer", "NextFryTime", DrydockApplyMoment.AfterPowerSolve, DrydockMemberKind.ReapplyCarried),
        new DrydockManifestMember(204, "Door", "NextStateChange", DrydockApplyMoment.AfterPowerSolve, DrydockMemberKind.ReapplyCarried),

        // clobber:startup, after StartEntities.
        new DrydockManifestMember(477, "ExtensionCableReceiver", "Provider", DrydockApplyMoment.AfterStart, DrydockMemberKind.ViaSystem),

        // reads-prior, before init: a handler reads the prior value on its edge.
        new DrydockManifestMember(33, "AtmosAlarmable", "LastAlarmState", Before, Field),
        new DrydockManifestMember(208, "EmergencyLight", "State", Before, Field),
        new DrydockManifestMember(265, "GravityGenerator", "GravityActive", Before, Field),
        new DrydockManifestMember(380, "Shuttle", "Enabled", Before, Field),
        new DrydockManifestMember(418, "Thruster", "OriginalLoad", Before, Field),
        new DrydockManifestMember(423, "UpgradePowerDraw", "BaseLoad", Before, Field),
        new DrydockManifestMember(424, "UpgradePowerSupplier", "BaseSupplyRate", Before, Field),
        new DrydockManifestMember(425, "UpgradePowerSupplyRamping", "BaseRampRate", Before, Field),

        // gap, before init: nothing else would restore it.
        new DrydockManifestMember(499, "ActiveCrematorium", "Accumulator", Before, Field),
        new DrydockManifestMember(479, "ActiveMicrowave", "CookTimeRemaining", Before, Field),
        new DrydockManifestMember(479, "ActiveMicrowave", "TotalTime", Before, Field),
        new DrydockManifestMember(479, "ActiveMicrowave", "PortionedRecipe", Before, Field),
        new DrydockManifestMember(496, "ActiveReagentGrinder", "EndTime", Before, Time),
        new DrydockManifestMember(496, "ActiveReagentGrinder", "Program", Before, Field),
        new DrydockManifestMember(64, "AddAccentClothing", "IsActive", Before, Field),
        new DrydockManifestMember(65, "AddAccentClothing", "Wearer", Before, Field),
        new DrydockManifestMember(71, "Airlock", "AutoCloseDelayModifier", Before, Field),
        new DrydockManifestMember(483, "Anomaly", "Stability", Before, Field),
        new DrydockManifestMember(483, "Anomaly", "Severity", Before, Field),
        new DrydockManifestMember(483, "Anomaly", "Health", Before, Field),
        new DrydockManifestMember(483, "Anomaly", "ConnectedVessel", Before, Field),
        new DrydockManifestMember(483, "Anomaly", "PointsEarned", Before, Field),
        new DrydockManifestMember(85, "Apc", "LastChargeState", Before, Field),
        new DrydockManifestMember(492, "ApcNetSwitch", "State", Before, Field),
        new DrydockManifestMember(103, "AtmosAlarmable", "IgnoreAlarms", Before, Field),
        new DrydockManifestMember(107, "AtmosAlertsComputer", "SilencedDevices", Before, Field),
        new DrydockManifestMember(125, "BiomassReclaimer", "BloodReagent", Before, Field),
        new DrydockManifestMember(126, "BiomassReclaimer", "CurrentExpectedYield", Before, Field),
        new DrydockManifestMember(127, "BiomassReclaimer", "ProcessingTimer", Before, Field),
        new DrydockManifestMember(128, "BiomassReclaimer", "RandomMessTimer", Before, Field),
        new DrydockManifestMember(129, "BiomassReclaimer", "SpawnedEntities", Before, Field),
        new DrydockManifestMember(487, "BotanySwab", "SeedData", Before, Field),
        new DrydockManifestMember(142, "Cartridge", "InstallationStatus", Before, Field),
        new DrydockManifestMember(478, "Charging", "ChargerUid", Before, Field),
        new DrydockManifestMember(164, "Conveyor", "State", Before, Field),
        new DrydockManifestMember(464, "Defusable", "Usable", Before, Field),
        new DrydockManifestMember(465, "Defusable", "ProceedWireCut", Before, Field),
        new DrydockManifestMember(501, "DiskConsolePrinting", "FinishTime", Before, Time),
        new DrydockManifestMember(494, "DisposalHolder", "StartingTime", Before, Field),
        new DrydockManifestMember(494, "DisposalHolder", "TimeLeft", Before, Field),
        new DrydockManifestMember(494, "DisposalHolder", "PreviousTube", Before, Field),
        new DrydockManifestMember(494, "DisposalHolder", "PreviousDirection", Before, Field),
        new DrydockManifestMember(494, "DisposalHolder", "CurrentTube", Before, Field),
        new DrydockManifestMember(494, "DisposalHolder", "CurrentDirection", Before, Field),
        new DrydockManifestMember(494, "DisposalHolder", "IsExitingDisposals", Before, Field),
        new DrydockManifestMember(494, "DisposalHolder", "Tags", Before, Field),
        new DrydockManifestMember(211, "Emitter", "IsOn", Before, Field),
        new DrydockManifestMember(224, "ExpendableLight", "StateExpiryTime", Before, Field),
        new DrydockManifestMember(482, "ForensicPad", "Used", Before, Field),
        new DrydockManifestMember(482, "ForensicPad", "Sample", Before, Field),
        new DrydockManifestMember(495, "ForensicScanner", "LastScannedName", Before, Field),
        new DrydockManifestMember(495, "ForensicScanner", "PrintReadyAt", Before, Time),
        new DrydockManifestMember(242, "GasDepositExtractor", "LastState", Before, Field),
        new DrydockManifestMember(244, "GasOutletInjector", "Enabled", Before, Field),
        new DrydockManifestMember(258, "GasVolumePump", "Overclocked", Before, Field),
        new DrydockManifestMember(484, "GuardianCreator", "Used", Before, Field),
        new DrydockManifestMember(276, "Headset", "IsEquipped", Before, Field),
        new DrydockManifestMember(291, "KitchenSpike", "InUse", Before, Field),
        new DrydockManifestMember(291, "KitchenSpike", "MeatSource0", Before, Field),
        new DrydockManifestMember(291, "KitchenSpike", "MeatSource1p", Before, Field),
        new DrydockManifestMember(291, "KitchenSpike", "PrototypesToSpawn", Before, Field),
        new DrydockManifestMember(291, "KitchenSpike", "Victim", Before, Field),
        new DrydockManifestMember(293, "Lathe", "CurrentRecipe", Before, Field),
        new DrydockManifestMember(502, "LinkedLifecycleGridChild", "LinkedUid", Before, Field),
        new DrydockManifestMember(503, "LinkedLifecycleGridParent", "LinkedEntities", Before, Field),
        new DrydockManifestMember(481, "Mail", "IsEnabled", Before, Field),
        new DrydockManifestMember(309, "Microwave", "Broken", Before, Field),
        new DrydockManifestMember(310, "Microwave", "CurrentCookTimeButtonIndex", Before, Field),
        new DrydockManifestMember(319, "MultipleTool", "CurrentEntry", Before, Field),
        new DrydockManifestMember(466, "ParticleAcceleratorControlBox", "InterfaceDisabled", Before, Field),
        new DrydockManifestMember(472, "ParticleAcceleratorControlBox", "SelectedStrength", Before, Field),
        new DrydockManifestMember(471, "ParticleAcceleratorControlBox", "Enabled", Before, Field),
        new DrydockManifestMember(470, "ParticleAcceleratorControlBox", "Assembled", Before, Field),
        new DrydockManifestMember(467, "ParticleAcceleratorControlBox", "MaxStrength", Before, Field),
        new DrydockManifestMember(468, "ParticleAcceleratorControlBox", "StrengthLocked", Before, Field),
        new DrydockManifestMember(469, "ParticleAcceleratorControlBox", "CanBeEnabled", Before, Field),
        new DrydockManifestMember(500, "ParticleAcceleratorPart", "Master", Before, Field),
        new DrydockManifestMember(331, "Pinpointer", "IsActive", Before, Field),
        // Through SetTarget (SharedPinpointerSystem.cs:82-95), which also sets the target's name and, when active, the
        // direction; both read the target's started components, so after startup.
        new DrydockManifestMember(332, "Pinpointer", "Target", DrydockApplyMoment.AfterStart, DrydockMemberKind.ViaSystem),
        new DrydockManifestMember(336, "PneumaticCannon", "Power", Before, Field),
        new DrydockManifestMember(438, "PortableScrubber", "Enabled", Before, Field),
        new DrydockManifestMember(480, "PreventCrisping", "Cycles", Before, Field),
        new DrydockManifestMember(360, "RCD", "UseMirrorPrototype", Before, Field),
        new DrydockManifestMember(360, "RCD", "_constructionDirection", Before, Field),
        new DrydockManifestMember(361, "ReagentDispenser", "AutoLabel", Before, Field),
        new DrydockManifestMember(362, "ReagentDispenser", "DispenseAmount", Before, Field),
        new DrydockManifestMember(509, "RechargeableBlocking", "Discharged", Before, Field),
        new DrydockManifestMember(371, "RotatingLight", "Enabled", Before, Field),
        // Storage is a map change. A device that asked to be disarmed by one comes back disarmed, by never being armed:
        // DisarmBomb on its first tick would announce a disarm nobody made (ScuttleDeviceSystem.cs:177, :269-276).
        // At the seam: ComponentInit resets it to the full timer (ScuttleDeviceSystem.cs:65-68), which the workbench caught.
        new DrydockManifestMember(507, "ScuttleDevice", "RemainingTime", DrydockApplyMoment.Seam, Field, SkipWhen: "DisarmOnMapChange"),
        new DrydockManifestMember(507, "ScuttleDevice", "CooldownTime", Before, Field),
        new DrydockManifestMember(507, "ScuttleDevice", "Armed", Before, Field, SkipWhen: "DisarmOnMapChange"),
        // One that does not keeps its countdown, and its armed map is the map it loads onto. Its music state is not carried.
        new DrydockManifestMember(507, "ScuttleDevice", "ArmedMap", DrydockApplyMoment.AfterStart, DrydockMemberKind.Rederive),
        new DrydockManifestMember(507, "ScuttleDevice", "PlayedAlertSound", Before, Field),
        new DrydockManifestMember(39, "Smes", "LastChargeState", Before, Field),
        new DrydockManifestMember(38, "Smes", "LastChargeLevel", Before, Field),
        new DrydockManifestMember(389, "SpaceVillainArcade", "RewardAmount", Before, Field),
        new DrydockManifestMember(398, "SuitSensor", "User", Before, Field),
        new DrydockManifestMember(486, "Summonable", "AlreadySummoned", Before, Field),
        new DrydockManifestMember(486, "Summonable", "Summon", Before, Field),
        new DrydockManifestMember(429, "VendingMachine", "Broken", Before, Field),
        new DrydockManifestMember(432, "Wieldable", "OldInhandPrefix", Before, Field),
        new DrydockManifestMember(433, "Wires", "SerialNumber", Before, Field),
        new DrydockManifestMember(433, "Wires", "WireSeed", Before, Field),
        // Before init, so H12's layout rebuild finds the count in place: the power wire seeds it only when it is absent
        // (PowerWireAction.cs:180-182).
        new DrydockManifestMember(435, "Wires", "StateData", Before, DrydockMemberKind.Entry,
            EntryKey: PowerWireActionKey.CutWires, EntryType: typeof(int)),
        // A pulse is cleared only by its timer (PowerWireAction.cs:269), which no store keeps, so a carried pulse would
        // hold the wire for ever; it travels once H12 re-arms the timer.
        new DrydockManifestMember(435, "Wires", "StateData", Before, DrydockMemberKind.Entry,
            EntryKey: PowerWireActionKey.Pulsed, EntryType: typeof(bool), OwedWith: "H12"),
        new DrydockManifestMember(437, "WiresPanel", "Visible", Before, Field),
        new DrydockManifestMember(510, "Pda", "ContainedId", Before, Field),
        new DrydockManifestMember(511, "GhostRoleMobSpawner", "CurrentTakeovers", Before, Field),
        new DrydockManifestMember(513, "ActiveHotPotato", "TargetTime", Before, Time),
        new DrydockManifestMember(517, "ContainmentFieldGenerator", "_powerBuffer", Before, Field),
        new DrydockManifestMember(517, "ContainmentFieldGenerator", "IsConnected", Before, Field),
        new DrydockManifestMember(519, "DeployableTurretController", "LinkedTurrets", Before, Field),
        new DrydockManifestMember(520, "Dispenser", "Dispensing", Before, Field),
        new DrydockManifestMember(520, "Dispenser", "DispensingItemId", Before, Field),
        new DrydockManifestMember(520, "Dispenser", "DispenseTimer", Before, Field),
        new DrydockManifestMember(523, "GasTurbineMonitor", "turbine", Before, DrydockMemberKind.Reference),
        new DrydockManifestMember(524, "NuclearReactorMonitor", "reactor", Before, DrydockMemberKind.Reference));

    private const string MusicState = "music state, re-picked at the next arm; no serializer for ResolvedSoundSpecifier";

    /// <summary>
    /// What the manifest leaves out on purpose, member by member, because each is state a load rebuilds, a value that
    /// means nothing in another round, or presentation the next use picks again. A member here is never written by the codec.
    /// </summary>
    public static readonly ImmutableArray<DrydockNotCarried> NotCarried = ImmutableArray.Create(
        new DrydockNotCarried(457, "TargetSeekerAlertGrid", "Alerters",
            "derived on the power edge; the handler appends without a check, so a carried list grows by one per restore (F36)"),
        new DrydockNotCarried(100, "Appearance", "AppearanceDataInit",
            "the prototype's initial data; the live dictionary travels in the ~appearance row instead"),
        new DrydockNotCarried(483, "Anomaly", "LastTickPointsEarned", "a GameTick, which means nothing in another round"),
        new DrydockNotCarried(478, "Charging", "ChargerComponent", "derived from ChargerUid"),
        new DrydockNotCarried(494, "DisposalHolder", "Container", "the container itself, rebuilt by the container system"),
        new DrydockNotCarried(495, "ForensicScanner", "CancelToken", "a live CancellationTokenSource"),
        new DrydockNotCarried(481, "Mail", "PriorityCancelToken", "a live CancellationTokenSource"),
        new DrydockNotCarried(507, "ScuttleDevice", "AlertAudioStream", "a playing audio entity"),
        // The song and its length are picked at the arm (ScuttleDeviceSystem.cs:231-235), the countdown plays nothing without
        // a song (:185), and a disarm clears the played flag (:275), so the three go together.
        new DrydockNotCarried(507, "ScuttleDevice", "SelectedNukeSong", MusicState),
        new DrydockNotCarried(507, "ScuttleDevice", "NukeSongLength", MusicState),
        new DrydockNotCarried(507, "ScuttleDevice", "PlayedNukeSong", MusicState),
        new DrydockNotCarried(435, "Wires", "StateData",
            "boxed values, most of them live CancellationTokenSources; PowerWireActionKey.CutWires travels as an entry of its own, and .Pulsed is owed with H12"));

    /// <summary>
    /// Components the store strips, by registration name, with the reason: each is either tied to the round's station or
    /// has its other end on a mob the store evicts. ShipRepairData is not here: the repair baseline is carried.
    /// </summary>
    public static readonly ImmutableDictionary<string, string> Stripped = new Dictionary<string, string>
    {
        ["StationMember"] = "membership of a station that exists in this round only",
        ["ShuttleConsoleJobSlots"] = "the round's job slots",
        ["ShipGuestAccess"] = "access granted to this round's players",
        ["FTL"] = "an FTL jump in progress, which the store does not freeze",
        ["Familiar"] = "its other end is a mob the store evicts",
        ["BeingCarried"] = "its other end is a mob the store evicts",
        ["Carrying"] = "its other end is a mob the store evicts",
        ["BeingCloned"] = "its other end is a mob the store evicts",
        ["WearingStethoscope"] = "its other end is a mob the store evicts",
    }.ToImmutableDictionary();

    /// <summary>
    /// A member by name on a component type or any of its bases, public or not, field or property: several members live on
    /// a shared base (a crayon's state on SharedCrayonComponent) or are private (the RCD's direction).
    /// </summary>
    public static MemberInfo? Resolve(Type type, string member)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        for (var declaring = type; declaring != null && declaring != typeof(object); declaring = declaring.BaseType)
        {
            if (((MemberInfo?) declaring.GetField(member, flags) ?? declaring.GetProperty(member, flags)) is { } found)
                return found;
        }

        return null;
    }

    public static Type MemberType(MemberInfo member) => member switch
    {
        FieldInfo field => field.FieldType,
        PropertyInfo property => property.PropertyType,
        _ => throw new InvalidOperationException($"Drydock codec: {member.Name} is neither a field nor a property."),
    };
}
