using Jellyfin.Plugin.Trailhead.Dns;
using Jellyfin.Plugin.Trailhead.Tests.Acme;
using Jellyfin.Plugin.Trailhead.TestSupport;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Dns;

public sealed class NameClaimerTests : IAsyncLifetime
{
    private readonly ListLogger _log = new();
    private readonly List<string> _messages = new();
    private FakeDynu _dynu = null!;

    public async Task InitializeAsync() => _dynu = await FakeDynu.StartAsync();

    public async Task DisposeAsync() => await _dynu.DisposeAsync();

    // Real time with a tiny backoff: a retried call ends in milliseconds without a fake clock.
    private DynuClient Client(FakeDynu? dynu = null, string key = FakeDynu.Key)
        => DynuClient.Create(key, _log, TimeProvider.System, (dynu ?? _dynu).BaseUrl, new DynuClientOptions { MaxAttempts = 2, BackoffBase = TimeSpan.FromMilliseconds(1), RequestTimeout = TimeSpan.FromSeconds(10) });

    private NameClaimer Claimer(DynuClient client) => new(client, ParentDomains.Embedded(), _log);

    private async Task<NameClaim> Claim(string? label, string? parent, string key = FakeDynu.Key)
    {
        using var client = Client(key: key);
        var result = await Claimer(client).ClaimAsync(label, parent, CancellationToken.None);
        _messages.Add(result.Message);
        return result;
    }

    private async Task<NameClaim> Check(string? label, string? parent, string key = FakeDynu.Key)
    {
        using var client = Client(key: key);
        var result = await Claimer(client).CheckAsync(label, parent, CancellationToken.None);
        _messages.Add(result.Message);
        return result;
    }

    private int Creates => _dynu.Requests.Count(r => r.Method == "POST" && r.Path == "/v2/dns");

    [Fact]
    public async Task A_free_name_is_created_and_comes_back_Claimed_with_its_id()
    {
        var result = await Claim("MyMedia", "freeddns.org");

        Assert.Equal(ClaimKind.Claimed, result.Kind);
        Assert.Equal("mymedia.freeddns.org", result.Name);
        Assert.Equal("mymedia.freeddns.org", Assert.Single(_dynu.DomainNames));
        Assert.Equal(result.Domain!.Id, (await Client().ListDomainsAsync(CancellationToken.None)).Single().Id);
        Assert.Equal(1, Creates);
    }

    [Fact]
    public async Task The_create_asks_for_no_address()
    {
        await Claim("mymedia", "freeddns.org");

        var post = Assert.Single(_dynu.Requests, r => r.Method == "POST" && r.Path == "/v2/dns");
        using var body = System.Text.Json.JsonDocument.Parse(post.Body);
        Assert.Equal("mymedia.freeddns.org", body.RootElement.GetProperty("name").GetString());
        Assert.False(body.RootElement.GetProperty("ipv4").GetBoolean());
        Assert.False(body.RootElement.GetProperty("ipv6").GetBoolean());
    }

    [Fact]
    public async Task Claiming_again_returns_the_same_name_and_makes_no_second_one()
    {
        var first = await Claim("mymedia", "freeddns.org");
        var again = await Claim("mymedia", "freeddns.org");

        Assert.Equal(ClaimKind.Claimed, first.Kind);
        Assert.Equal(ClaimKind.Ours, again.Kind);
        Assert.Equal(first.Domain!.Id, again.Domain!.Id);
        Assert.Single(_dynu.DomainNames);
        Assert.Equal(1, Creates);
    }

    [Fact]
    public async Task A_name_already_on_the_account_is_Ours_without_any_create()
    {
        var id = _dynu.AddDomain("home.mywire.org");

        var result = await Claim("home", "mywire.org");

        Assert.Equal(ClaimKind.Ours, result.Kind);
        Assert.Equal(id, result.Domain!.Id);
        Assert.Equal(0, Creates);
    }

    [Fact]
    public async Task A_name_on_another_account_is_Taken_and_offers_the_same_label_under_the_other_parents()
    {
        _dynu.AddForeignDomain("popular.freeddns.org");

        var result = await Claim("popular", "freeddns.org");

        Assert.Equal(ClaimKind.Taken, result.Kind);
        Assert.Equal(505, result.Status);
        Assert.NotNull(result.Alternatives);
        Assert.Equal(8, result.Alternatives!.Count);
        Assert.DoesNotContain("popular.freeddns.org", result.Alternatives);
        Assert.Equal("popular.mywire.org", result.Alternatives[0]);
        Assert.All(result.Alternatives, a => Assert.StartsWith("popular.", a));
        Assert.Empty(_dynu.DomainNames);
    }

    [Fact]
    public async Task A_501_Argument_answer_is_DynuRefused_not_Taken()
    {
        // What the live account gave for random names on two parents (research/dynu-live-check.md).
        _dynu.EnqueueCreate(new FakeDynuCanned(200, FakeDynu.Envelope(501, "Argument Exception", "Invalid.")));

        var result = await Claim("mymedia", "freeddns.org");

        Assert.Equal(ClaimKind.DynuRefused, result.Kind);
        Assert.Equal(501, result.Status);
        Assert.Equal("Argument Exception", result.DynuType);
        Assert.Null(result.Alternatives);
        Assert.Contains("Dynu", result.Message);
        Assert.Equal(1, Creates);
    }

    [Fact]
    public async Task A_501_on_the_HTTP_status_line_is_DynuRefused_too()
    {
        _dynu.EnqueueCreate(new FakeDynuCanned(501, FakeDynu.Envelope(501, "Argument Exception", "Invalid.")));

        var result = await Claim("mymedia", "mywire.org");

        Assert.Equal(ClaimKind.DynuRefused, result.Kind);
        Assert.Equal(1, Creates);
    }

    [Fact]
    public async Task The_quota_answer_is_AccountFull()
    {
        _dynu.EnqueueCreate(new FakeDynuCanned(200, FakeDynu.Envelope(503, "Exception", "Limit reached.")));

        var result = await Claim("mymedia", "freeddns.org");

        Assert.Equal(ClaimKind.AccountFull, result.Kind);
        Assert.Equal(503, result.Status);
        Assert.Equal(1, Creates);
    }

    [Fact]
    public async Task A_gateway_503_is_Unavailable_not_AccountFull()
    {
        _dynu.EnqueueCreate(new FakeDynuCanned(503, "Service Unavailable"));
        _dynu.EnqueueCreate(new FakeDynuCanned(503, "Service Unavailable"));

        var result = await Claim("mymedia", "freeddns.org");

        Assert.Equal(ClaimKind.Unavailable, result.Kind);
        Assert.Equal(2, Creates);
    }

    [Fact]
    public async Task A_wrong_key_is_BadKey()
    {
        var result = await Claim("mymedia", "freeddns.org", key: "a-different-key-0000");

        Assert.Equal(ClaimKind.BadKey, result.Kind);
        Assert.Equal(0, Creates);
    }

    [Fact]
    public async Task A_server_that_cannot_be_reached_is_Unavailable()
    {
        var gone = await FakeDynu.StartAsync();
        await gone.DisposeAsync();
        using var client = Client(gone);

        var result = await Claimer(client).ClaimAsync("mymedia", "freeddns.org", CancellationToken.None);

        Assert.Equal(ClaimKind.Unavailable, result.Kind);
    }

    [Fact]
    public async Task A_create_that_is_accepted_but_not_listed_is_Unavailable_so_the_next_run_can_find_it()
    {
        _dynu.EnqueueCreate(new FakeDynuCanned(200, FakeDynu.Envelope(200)));

        var result = await Claim("mymedia", "freeddns.org");

        Assert.Equal(ClaimKind.Unavailable, result.Kind);
    }

    [Theory]
    [InlineData("", "freeddns.org")]
    [InlineData("ab", "freeddns.org")]
    [InlineData("a.b.c", "freeddns.org")]
    [InlineData("mymedia", "dynu.com")]
    [InlineData("mymedia", "example.com")]
    [InlineData("mymedia", null)]
    public async Task An_unusable_label_or_parent_is_Invalid_and_sends_nothing(string label, string? parent)
    {
        var claim = await Claim(label, parent);
        var check = await Check(label, parent);

        Assert.Equal(ClaimKind.Invalid, claim.Kind);
        Assert.Equal(ClaimKind.Invalid, check.Kind);
        Assert.False(string.IsNullOrWhiteSpace(claim.Message));
        Assert.Empty(_dynu.Requests);
    }

    [Fact]
    public async Task Check_says_Ours_for_a_name_on_the_account_and_sends_no_create()
    {
        _dynu.AddDomain("home.mywire.org");

        var result = await Check("home", "mywire.org");

        Assert.Equal(ClaimKind.Ours, result.Kind);
        Assert.Equal(0, Creates);
    }

    [Fact]
    public async Task Check_does_not_call_a_name_someone_else_has_free()
    {
        // Live: www.freeddns.org is held elsewhere, and only the claim (505) said so; the check had said "looks free".
        _dynu.AddForeignDomain("www.freeddns.org");

        var result = await Check("www", "freeddns.org");

        Assert.Equal(ClaimKind.Unchecked, result.Kind);
        Assert.DoesNotContain("free", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, Creates);
    }

    [Fact]
    public async Task Check_of_a_name_not_on_the_account_says_the_claim_will_tell_and_creates_nothing()
    {
        var result = await Check("mymedia", "freeddns.org");

        Assert.Equal(ClaimKind.Unchecked, result.Kind);
        Assert.Contains("Continue", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, Creates);
        Assert.Empty(_dynu.DomainNames);
    }

    [Fact]
    public async Task Check_with_a_wrong_key_is_BadKey()
    {
        Assert.Equal(ClaimKind.BadKey, (await Check("mymedia", "freeddns.org", key: "a-different-key-0000")).Kind);
    }

    [Fact]
    public async Task A_canceled_call_throws_instead_of_answering_Unavailable()
    {
        using var client = Client();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Claimer(client).ClaimAsync("mymedia", "freeddns.org", cts.Token));
    }

    [Fact]
    public async Task No_key_and_no_token_reaches_a_log_line_a_message_or_a_result()
    {
        _dynu.AddDomain("home.mywire.org");
        _dynu.AddForeignDomain("popular.freeddns.org");
        await Claim("mymedia", "freeddns.org");
        await Claim("mymedia", "freeddns.org");
        await Claim("home", "mywire.org");
        await Claim("popular", "freeddns.org");
        await Check("popular", "freeddns.org");
        await Check("home", "mywire.org");

        // Dynu errors whose type and message carry the key: neither may get through.
        _dynu.EnqueueCreate(new FakeDynuCanned(200, FakeDynu.Envelope(501, FakeDynu.Key, FakeDynu.Key + " " + FakeDynu.Token)));
        var odd = await Claim("another", "opik.net");
        _dynu.EnqueueCreate(new FakeDynuCanned(200, FakeDynu.Envelope(505, "Token " + FakeDynu.Token, FakeDynu.Token)));
        var odd2 = await Claim("another", "gleeze.com");

        var everything = string.Join("\n", _log.Entries.Select(e => e.Text).Concat(_messages)) + "\n" + odd + "\n" + odd2;
        Assert.DoesNotContain(FakeDynu.Key, everything);
        Assert.DoesNotContain(FakeDynu.Token, everything);
        Assert.NotEmpty(_log.Entries);
    }
}
