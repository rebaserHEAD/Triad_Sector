#nullable enable
using Content.IntegrationTests._Triad.PostgresPair;

namespace Content.IntegrationTests;

/// <summary>
/// Drops whatever scratch database this test run created and did not drop, once, after every test in the assembly's root
/// namespace. It sits in that namespace so it covers every test; a SetUpFixture applies only to its own namespace and those
/// below. Costs nothing when no PostgreSQL pair ran.
/// </summary>
[SetUpFixture]
public sealed class TriadPostgresTeardown
{
    [OneTimeTearDown]
    public async Task TearDown()
    {
        await TriadPostgres.Sweep();
    }
}
