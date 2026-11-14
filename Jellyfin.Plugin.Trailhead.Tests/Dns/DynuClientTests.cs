using System.Text.Json;
using Jellyfin.Plugin.Trailhead.Acme;
using Jellyfin.Plugin.Trailhead.Dns;
using Jellyfin.Plugin.Trailhead.Tests.Acme;
using Jellyfin.Plugin.Trailhead.TestSupport;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Dns;

public sealed class DynuClientTests : IAsyncLifetime
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));
    private readonly ListLogger _log = new();
    private FakeDynu _dynu = null!;

    public async Task InitializeAsync() => _dynu = await FakeDynu.StartAsync();

    public async Task DisposeAsync() => await _dynu.DisposeAsync();

    // Runs the fake clock while the call is in flight. A call the code retries would otherwise wait on a clock that never
    // moves and hang the test; this way it ends, and the assertions say what went wrong.
    private async Task<DynuException> Fails(Func<Task> call)
    {
        try
        {
            await Clock.PumpAsync(call(), _time);
        }
        catch (DynuException e)
        {
            return e;
        }

        throw new Xunit.Sdk.XunitException("Expected a DynuException, but the call succeeded.");
    }

    // A long request timeout, so the fake clock running during a backoff can't time out a request that is merely slow to start.
    private DynuClient Client(string key = FakeDynu.Key, TimeSpan? requestTimeout = null, TimeSpan? backoff = null)
        => DynuClient.Create(key, _log, _time, _dynu.BaseUrl, new DynuClientOptions { RequestTimeout = requestTimeout ?? TimeSpan.FromHours(1), BackoffBase = backoff ?? TimeSpan.FromMilliseconds(500) });

    [Fact]
    public async Task Listing_sends_the_key_and_accept_headers_and_returns_ids_and_names()
    {
        var id = _dynu.AddDomain("home.example.org");
        using var client = Client();

        var domains = await client.ListDomainsAsync(CancellationToken.None);

        Assert.Equal(new DynuDomain(id, "home.example.org"), Assert.Single(domains));
        var request = Assert.Single(_dynu.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal("/v2/dns", request.Path);
        Assert.Equal(FakeDynu.Key, request.ApiKey);
        Assert.Contains("application/json", request.Accept);
    }

    [Fact]
    public async Task Adding_a_TXT_record_posts_the_node_text_and_a_short_TTL_and_returns_the_record_id()
    {
        var domain = _dynu.AddDomain("home.example.org");
        using var client = Client();

        var recordId = await client.AddTxtAsync(domain, "_acme-challenge", "the-value", CancellationToken.None);

        var stored = Assert.Single(_dynu.Records);
        Assert.Equal(stored.Id, recordId);
        Assert.Equal(("_acme-challenge", "the-value", 120), (stored.NodeName, stored.Text, stored.Ttl));
        var request = Assert.Single(_dynu.Requests);
        Assert.Equal($"/v2/dns/{domain}/record", request.Path);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("TXT", body.RootElement.GetProperty("recordType").GetString());
        Assert.True(body.RootElement.GetProperty("state").GetBoolean());
    }

    [Fact]
    public async Task Deleting_a_record_removes_it()
    {
        var domain = _dynu.AddDomain("home.example.org");
        using var client = Client();
        var recordId = await client.AddTxtAsync(domain, "_acme-challenge", "v", CancellationToken.None);

        await client.DeleteRecordAsync(domain, recordId, CancellationToken.None);

        Assert.Empty(_dynu.Records);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Deleting_a_record_that_is_already_gone_is_success(int how)
    {
        var domain = _dynu.AddDomain("home.example.org");
        _dynu.DeleteMissing = (DeleteMissingAs)how;
        using var client = Client();

        await Clock.PumpAsync(client.DeleteRecordAsync(domain, 424242, CancellationToken.None), _time);

        // One request: 404 and 501 are answers, not outages, so nothing is retried either.
        Assert.Single(_dynu.Requests);
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(5, 0)]
    [InlineData(-3, 5)]
    [InlineData(5, -3)]
    public async Task An_id_of_zero_or_less_is_refused_before_anything_is_sent(long domain, long record)
    {
        using var client = Client();

        var error = await Fails(() => client.DeleteRecordAsync(domain, record, CancellationToken.None));

        Assert.Equal(FailureKind.Permanent, error.Kind);
        Assert.Empty(_dynu.Requests);
    }

    [Fact]
    public async Task An_id_of_zero_is_refused_for_an_add_too()
    {
        using var client = Client();

        await Fails(() => client.AddTxtAsync(0, "_acme-challenge", "v", CancellationToken.None));

        Assert.Empty(_dynu.Requests);
    }

    [Fact]
    public async Task An_error_envelope_inside_a_200_is_raised()
    {
        using var client = Client();
        var domain = _dynu.AddDomain("home.example.org");
        _dynu.Enqueue(new FakeDynuCanned(200, FakeDynu.Envelope(505, "Validation Exception", "Hostname is taken.")));

        var error = await Fails(() => client.AddTxtAsync(domain, "_acme-challenge", "v", CancellationToken.None));

        Assert.Equal(505, error.Status);
        Assert.Equal(FailureKind.Permanent, error.Kind);
        Assert.Contains("Validation Exception", error.Message);
        Assert.Empty(_dynu.Records);
    }

    [Theory]
    [InlineData(501)]
    [InlineData(502)]
    [InlineData(505)]
    public async Task Dynus_application_errors_in_the_envelope_are_not_retried(int status)
    {
        using var client = Client();
        _dynu.Enqueue(new FakeDynuCanned(200, FakeDynu.Envelope(status, "Some Exception")));

        var error = await Fails(() => client.ListDomainsAsync(CancellationToken.None));

        Assert.Equal(FailureKind.Permanent, error.Kind);
        Assert.Single(_dynu.Requests);
    }

    [Fact]
    public async Task A_429_and_then_a_500_are_retried_with_a_backoff_until_it_works()
    {
        _dynu.AddDomain("home.example.org");
        _dynu.Enqueue(new FakeDynuCanned(429, FakeDynu.Envelope(429, "Too Many Requests")));
        _dynu.Enqueue(new FakeDynuCanned(500, "oops"));
        using var client = Client();

        var task = client.ListDomainsAsync(CancellationToken.None);
        var domains = await Clock.PumpAsync(task, _time);

        Assert.Single(domains);
        Assert.Equal(3, _dynu.Requests.Count);
        Assert.Contains("attempt 3", _log.All);
    }

    [Fact]
    public async Task The_backoff_waits_on_the_clock_it_was_given()
    {
        _dynu.Enqueue(new FakeDynuCanned(500, "oops"));
        // A 10 ms backoff: a delay on the real clock would be over long before the assertion below.
        using var client = Client(backoff: TimeSpan.FromMilliseconds(10));

        var task = client.ListDomainsAsync(CancellationToken.None);
        await Clock.UntilAsync(() => _dynu.Requests.Count == 1, "the first request");
        await Task.Delay(400);

        // The fake clock hasn't moved, so no second request: the delay isn't a real-time one.
        Assert.Single(_dynu.Requests);
        await Clock.PumpAsync(task, _time);
        Assert.Equal(2, _dynu.Requests.Count);
    }

    [Fact]
    public async Task It_gives_up_after_three_attempts_as_a_transient_failure()
    {
        using var client = Client();
        for (var i = 0; i < 5; i++)
        {
            _dynu.Enqueue(new FakeDynuCanned(503, "busy"));
        }

        var error = await Fails(() => Clock.PumpAsync(client.ListDomainsAsync(CancellationToken.None), _time));

        Assert.Equal(FailureKind.Transient, error.Kind);
        Assert.Equal(3, _dynu.Requests.Count);
    }

    [Fact]
    public async Task A_refused_key_is_permanent_and_not_retried()
    {
        using var client = Client("WRONG-FAKE-KEY");

        var error = await Fails(() => client.ListDomainsAsync(CancellationToken.None));

        Assert.True(error.AuthRejected);
        Assert.Equal(FailureKind.Permanent, error.Kind);
        Assert.Equal(401, error.Status);
        Assert.Single(_dynu.Requests);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task Checking_the_key_says_no_for_401_and_403(int status)
    {
        using var client = Client();
        _dynu.Enqueue(new FakeDynuCanned(status, FakeDynu.Envelope(status, "Authentication Exception")));

        Assert.False(await client.CheckKeyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Checking_the_key_says_yes_for_a_good_key_and_throws_when_Dynu_cannot_say()
    {
        using var client = Client();
        Assert.True(await client.CheckKeyAsync(CancellationToken.None));

        for (var i = 0; i < 3; i++)
        {
            _dynu.Enqueue(new FakeDynuCanned(500, "oops"));
        }

        // A server error says nothing about the key, so it must not come back as "wrong key".
        await Fails(() => Clock.PumpAsync(client.CheckKeyAsync(CancellationToken.None), _time));
    }

    [Fact]
    public async Task Neither_the_key_nor_a_hostname_token_reaches_a_log_line_or_an_exception_message()
    {
        var domain = _dynu.AddDomain("home.example.org");
        using var good = Client();
        using var bad = Client("WRONG-FAKE-KEY-" + FakeDynu.Token);
        var thrown = new List<Exception>();

        // The list response carries the token; an error that echoes the key is the worst case for the message.
        await good.ListDomainsAsync(CancellationToken.None);
        var record = await good.AddTxtAsync(domain, "_acme-challenge", "v", CancellationToken.None);
        await good.DeleteRecordAsync(domain, record, CancellationToken.None);
        _dynu.Enqueue(new FakeDynuCanned(200, FakeDynu.Envelope(501, "Argument Exception", "Rejected " + FakeDynu.Key + " and " + FakeDynu.Token)));
        thrown.Add(await Fails(() => good.AddTxtAsync(domain, "_acme-challenge", "v", CancellationToken.None)));
        thrown.Add(await Fails(() => bad.ListDomainsAsync(CancellationToken.None)));
        for (var i = 0; i < 3; i++)
        {
            _dynu.Enqueue(new FakeDynuCanned(200, "<html>" + FakeDynu.Key + "</html>"));
        }

        thrown.Add(await Fails(() => Clock.PumpAsync(good.ListDomainsAsync(CancellationToken.None), _time)));

        Assert.NotEmpty(_log.Entries);
        foreach (var secret in new[] { FakeDynu.Key, FakeDynu.Token })
        {
            Assert.DoesNotContain(secret, _log.All);
            foreach (var e in thrown)
            {
                Assert.DoesNotContain(secret, e.Message);
                Assert.DoesNotContain(secret, e.ToString());
            }
        }
    }

    [Fact]
    public async Task Log_lines_name_the_method_the_path_with_ids_the_status_and_the_attempt()
    {
        var domain = _dynu.AddDomain("home.example.org");
        using var client = Client();

        var id = await client.AddTxtAsync(domain, "_acme-challenge", "secret-looking-text", CancellationToken.None);

        var line = Assert.Single(_log.Entries).Text;
        Assert.Contains($"POST dns/{domain}/record -> 200 (attempt 1)", line);
        Assert.DoesNotContain("secret-looking-text", line);
        Assert.True(id > 0);
    }

    [Fact]
    public async Task A_body_that_never_arrives_times_out_as_a_transient_failure()
    {
        using var client = Client(requestTimeout: TimeSpan.FromSeconds(30));
        for (var i = 0; i < 3; i++)
        {
            _dynu.Enqueue(new FakeDynuCanned(200, string.Empty, StallBody: true));
        }

        var error = await Fails(() => Clock.PumpAsync(client.ListDomainsAsync(CancellationToken.None), _time));

        Assert.Equal(FailureKind.Transient, error.Kind);
        Assert.Contains("took longer", error.Message);
        Assert.Equal(3, _dynu.Requests.Count);
    }

    [Fact]
    public async Task Cancelling_is_a_cancellation_and_not_a_Dynu_failure()
    {
        using var client = Client();
        _dynu.Enqueue(new FakeDynuCanned(200, string.Empty, StallBody: true));
        using var cts = new CancellationTokenSource();

        var task = client.ListDomainsAsync(cts.Token);
        await Clock.UntilAsync(() => _dynu.Requests.Count == 1, "the request");
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        // A cancel is not a failed attempt: nothing is logged for it, and nothing waits to retry it.
        Assert.DoesNotContain("-> 0", _log.All);
        Assert.Single(_dynu.Requests);
    }

    [Fact]
    public async Task A_response_over_the_size_limit_is_refused()
    {
        using var client = Client();
        _dynu.Enqueue(new FakeDynuCanned(200, "{\"statusCode\":200,\"pad\":\"" + new string('a', 4 * 1024 * 1024) + "\"}"));

        var error = await Fails(() => client.ListDomainsAsync(CancellationToken.None));

        Assert.Equal(FailureKind.Permanent, error.Kind);
        Assert.Contains("larger than", error.Message);
        Assert.Single(_dynu.Requests);
    }

    [Fact]
    public async Task A_200_that_is_not_JSON_is_a_transient_failure()
    {
        using var client = Client();
        for (var i = 0; i < 3; i++)
        {
            _dynu.Enqueue(new FakeDynuCanned(200, "<html>maintenance</html>"));
        }

        var error = await Fails(() => Clock.PumpAsync(client.ListDomainsAsync(CancellationToken.None), _time));

        Assert.Equal(FailureKind.Transient, error.Kind);
    }

    [Fact]
    public async Task A_new_record_answer_without_an_id_is_a_failure_not_a_zero()
    {
        var domain = _dynu.AddDomain("home.example.org");
        using var client = Client();
        _dynu.Enqueue(new FakeDynuCanned(200, FakeDynu.Envelope(200)));

        var error = await Fails(() => client.AddTxtAsync(domain, "_acme-challenge", "v", CancellationToken.None));

        Assert.Equal(FailureKind.Permanent, error.Kind);
    }

    [Fact]
    public async Task A_server_that_cannot_be_reached_is_a_transient_failure()
    {
        // Port 1 on loopback: nothing listens, so the connection is refused at once.
        using var client = DynuClient.Create(FakeDynu.Key, _log, _time, new Uri("http://127.0.0.1:1/v2/"), new DynuClientOptions { RequestTimeout = TimeSpan.FromHours(1) });

        var error = await Fails(() => Clock.PumpAsync(client.ListDomainsAsync(CancellationToken.None), _time));

        Assert.Equal(FailureKind.Transient, error.Kind);
        Assert.Contains("could not be reached", error.Message);
    }

    [Fact]
    public void A_client_without_a_key_cannot_be_made()
    {
        Assert.Throws<ArgumentException>(() => DynuClient.Create(" ", _log, _time));
    }

    [Fact]
    public async Task A_server_with_no_domains_gives_no_domains_and_refuses_a_record()
    {
        // The harness given nothing: it has to say so, not pretend there was a domain.
        using var client = Client();

        Assert.Empty(await client.ListDomainsAsync(CancellationToken.None));
        var error = await Fails(() => client.AddTxtAsync(1000, "_acme-challenge", "v", CancellationToken.None));
        Assert.Equal(501, error.Status);
    }

    [Fact]
    public void Dynu_errors_classify_by_their_kind_and_anything_else_stays_transient()
    {
        Assert.Equal(FailureKind.Permanent, FailureBudget.Classify(new DynuException(FailureKind.Permanent, "no")));
        Assert.Equal(FailureKind.Transient, FailureBudget.Classify(new DynuException(FailureKind.Transient, "later")));
        Assert.Equal(FailureKind.Transient, FailureBudget.Classify(new DnsWaitException("not yet")));
    }
}
