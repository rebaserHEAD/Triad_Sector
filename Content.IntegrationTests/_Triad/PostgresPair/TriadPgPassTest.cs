#nullable enable

namespace Content.IntegrationTests._Triad.PostgresPair;

/// <summary>
/// The pgpass reader against the entry shapes libpq accepts. The request is the pair's: localhost, a port, a database
/// name no entry can know in advance, and the dedicated role, so only a <c>*</c> database field can match it.
/// </summary>
[TestFixture]
[TestOf(typeof(TriadPgPass))]
public sealed class TriadPgPassTest
{
    private const string Database = "triad_test_0123abcd_p1";

    private static string? Find(params string[] lines) =>
        TriadPgPass.Find(lines, "localhost", 5432, Database, "triad_test");

    [Test]
    public void AWildcardDatabaseLineMatches()
    {
        Assert.That(Find("localhost:5432:*:triad_test:secret"), Is.EqualTo("secret"));
    }

    [Test]
    public void EveryFirstFourFieldTakesTheWildcard()
    {
        Assert.That(Find("*:*:*:*:anything"), Is.EqualTo("anything"));
    }

    [Test]
    public void LinesThatDifferInOneFieldDoNotMatch()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Find("otherhost:5432:*:triad_test:x"), Is.Null, "host");
            Assert.That(Find("localhost:5433:*:triad_test:x"), Is.Null, "port");
            Assert.That(Find("localhost:5432:postgres:triad_test:x"), Is.Null, "a named database is not the pair's");
            Assert.That(Find("localhost:5432:*:postgres:x"), Is.Null, "user");
        });
    }

    [Test]
    public void TheFirstMatchingLineWinsAndANonMatchingControlBeforeItIsPassedOver()
    {
        var found = Find(
            "localhost:5432:postgres:triad_test:control",
            "localhost:5432:*:triad_test:first",
            "localhost:5432:*:triad_test:second");

        Assert.That(found, Is.EqualTo("first"));
    }

    [Test]
    public void EscapesAreReadInThePassword()
    {
        Assert.That(Find(@"localhost:5432:*:triad_test:pa\:ss\\wo\rd"), Is.EqualTo(@"pa:ss\word"));
    }

    [Test]
    public void AnEscapedColonDoesNotSplitAField()
    {
        Assert.That(Find(@"local\:host:5432:*:triad_test:x"), Is.Null, "the host field is \"local:host\", not \"local\"");
        Assert.That(TriadPgPass.Find([@"local\:host:5432:*:triad_test:x"], "local:host", 5432, Database, "triad_test"), Is.EqualTo("x"));
    }

    [Test]
    public void AnEscapedStarIsALiteralNotTheWildcard()
    {
        Assert.That(Find(@"localhost:5432:\*:triad_test:literal"), Is.Null);
    }

    [Test]
    public void ThePasswordEndsAtTheNextUnescapedColon()
    {
        Assert.That(Find("localhost:5432:*:triad_test:secret:trailing"), Is.EqualTo("secret"));
    }

    [Test]
    public void CommentsBlankAndShortLinesAreSkipped()
    {
        var found = Find(
            "#localhost:5432:*:triad_test:commented",
            "",
            "localhost:5432:*",
            "localhost:5432:*:triad_test:real");

        Assert.That(found, Is.EqualTo("real"));
    }

    [Test]
    public void ATrailingCarriageReturnIsNotPartOfThePassword()
    {
        Assert.That(Find("localhost:5432:*:triad_test:crlf\r"), Is.EqualTo("crlf"));
    }

    [Test]
    public void NoLineIsNull()
    {
        Assert.That(Find(), Is.Null);
    }
}
