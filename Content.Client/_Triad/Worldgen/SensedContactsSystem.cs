using Content.Shared._Triad.Worldgen;
using Robust.Shared.Timing;

namespace Content.Client._Triad.Worldgen;

public sealed class SensedContactsSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;

    private static readonly TimeSpan RequestThrottle = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(5);
    private static readonly SensedContactData[] EmptyContacts = Array.Empty<SensedContactData>();

    private TimeSpan _lastRequestTime = TimeSpan.Zero;

    private readonly Dictionary<NetEntity, Dictionary<int, SensedContactData>> _contacts = new();
    private readonly Dictionary<NetEntity, TimeSpan> _lastUpdated = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<SensedContactsDeltaEvent>(OnDelta);
    }

    private void OnDelta(SensedContactsDeltaEvent ev)
    {
        if (!_contacts.TryGetValue(ev.Console, out var consoleContacts))
        {
            consoleContacts = new Dictionary<int, SensedContactData>();
            _contacts[ev.Console] = consoleContacts;
        }

        if (ev.FullReset)
            consoleContacts.Clear();

        foreach (var id in ev.Removes)
        {
            consoleContacts.Remove(id);
        }

        foreach (var contact in ev.Adds)
        {
            consoleContacts[contact.Id] = contact;
        }

        _lastUpdated[ev.Console] = _timing.CurTime;
    }

    /// <summary>
    /// Requests a refresh of sensed contacts for the given console, throttled to one network
    /// send per <see cref="RequestThrottle"/> across the whole system.
    /// </summary>
    public void RequestContacts(EntityUid console)
    {
        if (!Exists(console))
            return;

        if (_timing.CurTime - _lastRequestTime < RequestThrottle)
            return;

        _lastRequestTime = _timing.CurTime;

        var netConsole = GetNetEntity(console);
        RaiseNetworkEvent(new RequestSensedContactsEvent(netConsole));
    }

    /// <summary>
    /// Gets the current sensed contacts for the given console. Returns an empty collection if
    /// the console has no known contacts or its data hasn't been refreshed in a while (contacts
    /// are static, so a generous staleness window is used to avoid flicker).
    /// </summary>
    public IReadOnlyCollection<SensedContactData> GetContacts(EntityUid console)
    {
        var netConsole = GetNetEntity(console);

        if (!_lastUpdated.TryGetValue(netConsole, out var lastUpdate)
            || _timing.CurTime - lastUpdate > StaleAfter
            || !_contacts.TryGetValue(netConsole, out var consoleContacts))
        {
            return EmptyContacts;
        }

        return consoleContacts.Values;
    }
}
