namespace Content.Server._Triad.Market.Components;

/// <summary>
/// Marks a station (a managed point of interest) whose commerce collects the sector purchase tax:
/// vendors placed on its grids add the tax on top of their price and pay it into the sector pot
/// (see BankSystem.SectorTax.cs). Player ships and unmanaged grids never carry this, so their
/// vendors charge list price only.
/// </summary>
[RegisterComponent]
public sealed partial class SectorPurchaseTaxComponent : Component;
