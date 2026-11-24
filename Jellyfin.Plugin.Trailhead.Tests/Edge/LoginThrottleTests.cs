using System.Diagnostics;
using System.Net;
using System.Text;
using Campground.Shared.Registry;
using Jellyfin.Plugin.Trailhead.Edge;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Edge;

/// <summary>The login throttle (T7): failures slow down, nobody is locked out, other users on one address go free, a success clears.</summary>
public sealed class LoginThrottleTests
{
    private static readonly IPAddress Home = IPAddress.Parse("198.51.100.7");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero));

    private LoginThrottle Throttle(ThrottleOptions? options = null) => new(options ?? new ThrottleOptions(), _time);

    // One try: wait it out (on the fake clock), then finish it.
    private static TimeSpan Try(LoginThrottle throttle, FakeTimeProvider time, IPAddress from, string user, SignInOutcome outcome)
    {
        var ticket = throttle.Enter(from, user);
        Assert.False(ticket.IsRefused);
        time.Advance(ticket.Wait);
        ticket.Finish(outcome);
        return ticket.Wait;
    }

    [Fact]
    public void Failures_past_the_free_ones_wait_longer_each_time_up_to_the_ceiling()
    {
        var throttle = Throttle();

        var waits = Enumerable.Range(0, 10).Select(_ => Try(throttle, _time, Home, "alice", SignInOutcome.Failure).TotalSeconds).ToArray();

        Assert.Equal(new double[] { 0, 0, 0, 1, 2, 4, 8, 16, 30, 30 }, waits);
    }

    [Fact]
    public void Slowed_is_never_refused_one_try_at_a_time_and_the_right_password_still_gets_in()
    {
        var throttle = Throttle();
        for (var i = 0; i < 40; i++)
        {
            Try(throttle, _time, Home, "alice", SignInOutcome.Failure);
        }

        var ticket = throttle.Enter(Home, "alice");

        Assert.False(ticket.IsRefused);
        Assert.Equal(TimeSpan.FromSeconds(30), ticket.Wait);
    }

    [Fact]
    public void Another_user_on_the_same_address_isnt_slowed_by_someone_elses_failures()
    {
        var throttle = Throttle();
        for (var i = 0; i < 8; i++)
        {
            Try(throttle, _time, Home, "alice", SignInOutcome.Failure);
        }

        Assert.Equal(TimeSpan.Zero, throttle.Enter(Home, "bob").Wait);
    }

    [Fact]
    public void A_success_clears_that_pair()
    {
        var throttle = Throttle();
        for (var i = 0; i < 5; i++)
        {
            Try(throttle, _time, Home, "alice", SignInOutcome.Failure);
        }

        Try(throttle, _time, Home, "alice", SignInOutcome.Success);

        Assert.Equal(TimeSpan.Zero, Try(throttle, _time, Home, "alice", SignInOutcome.Failure));
    }

    [Fact]
    public void Names_are_compared_without_case_or_spaces()
    {
        var throttle = Throttle();
        for (var i = 0; i < 4; i++)
        {
            Try(throttle, _time, Home, i % 2 == 0 ? "Alice" : " alice ", SignInOutcome.Failure);
        }

        Assert.True(throttle.Enter(Home, "ALICE").Wait > TimeSpan.Zero);
    }

    [Fact]
    public void The_address_ceiling_slows_a_guesser_who_tries_many_names()
    {
        var throttle = Throttle(new ThrottleOptions { FreePerAddress = 5 });
        for (var i = 0; i < 5; i++)
        {
            Try(throttle, _time, Home, "user" + i, SignInOutcome.Failure);
        }

        Assert.True(throttle.Enter(Home, "brand-new-name").Wait > TimeSpan.Zero);
        Assert.Equal(TimeSpan.Zero, throttle.Enter(IPAddress.Parse("198.51.100.8"), "brand-new-name").Wait);
    }

    [Fact]
    public void An_IPv6_home_counts_as_one_by_its_64()
    {
        var throttle = Throttle();
        for (var i = 1; i <= 4; i++)
        {
            Try(throttle, _time, IPAddress.Parse("2001:db8:1:2::" + i), "alice", SignInOutcome.Failure);
        }

        Assert.True(throttle.Enter(IPAddress.Parse("2001:db8:1:2:ffff::9"), "alice").Wait > TimeSpan.Zero);
        Assert.Equal(TimeSpan.Zero, throttle.Enter(IPAddress.Parse("2001:db8:1:3::1"), "alice").Wait);
        Assert.Equal("2001:db8:1:2::/64", LoginThrottle.AddressKey(IPAddress.Parse("2001:db8:1:2:aaaa:bbbb:cccc:dddd")));
        Assert.Equal("198.51.100.7", LoginThrottle.AddressKey(IPAddress.Parse("::ffff:198.51.100.7")));
    }

    [Fact]
    public void Tries_in_flight_count_so_a_burst_cant_all_go_at_once()
    {
        var throttle = Throttle();
        for (var i = 0; i < 3; i++)
        {
            Try(throttle, _time, Home, "alice", SignInOutcome.Failure);
        }

        var burst = Enumerable.Range(0, 4).Select(_ => throttle.Enter(Home, "alice")).ToList();
        var fifth = throttle.Enter(Home, "alice");

        Assert.Equal(new double[] { 1, 3, 7, 15 }, burst.Select(t => t.Wait.TotalSeconds).ToArray());
        Assert.True(fifth.IsRefused);
        Assert.True(fifth.Wait >= TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void A_try_that_was_neither_doesnt_count_as_a_failure()
    {
        var throttle = Throttle();
        for (var i = 0; i < 5; i++)
        {
            Try(throttle, _time, Home, "alice", SignInOutcome.Neither);
        }

        Assert.Equal(TimeSpan.Zero, throttle.Enter(Home, "alice").Wait);
    }

    [Fact]
    public void An_idle_key_is_forgotten()
    {
        var throttle = Throttle();
        for (var i = 0; i < 6; i++)
        {
            Try(throttle, _time, Home, "alice", SignInOutcome.Failure);
        }

        _time.Advance(TimeSpan.FromMinutes(31));

        Assert.Equal(TimeSpan.Zero, throttle.Enter(Home, "alice").Wait);
    }

    [Fact]
    public void At_the_key_cap_a_slowed_key_is_kept_and_a_quiet_one_goes()
    {
        var throttle = Throttle(new ThrottleOptions { MaxKeys = 2 });
        for (var i = 0; i < 4; i++)
        {
            Try(throttle, _time, Home, "alice", SignInOutcome.Failure);
        }

        Try(throttle, _time, Home, "bob", SignInOutcome.Neither);
        Try(throttle, _time, Home, "carol", SignInOutcome.Neither);

        Assert.True(throttle.Enter(Home, "alice").Wait > TimeSpan.Zero);
        Assert.Equal(2, throttle.PairCount);
    }

    [Theory]
    [InlineData("POST", "/Users/AuthenticateByName", "", "ByName")]
    [InlineData("POST", "/users/authenticatebyname/", "", "ByName")]
    [InlineData("POST", "/jf/Users/AuthenticateByName", "/jf", "ByName")]
    [InlineData("POST", "/Users/AuthenticateWithQuickConnect", "", "QuickConnect")]
    [InlineData("POST", "/Users/0f2c4a0e5b6d4c8f9a1b2c3d4e5f6a7b/Authenticate", "", "ById")]
    [InlineData("GET", "/Users/AuthenticateByName", "", "None")]
    [InlineData("POST", "/Users/Me", "", "None")]
    [InlineData("POST", "/QuickConnect/Initiate", "", "None")]
    public void The_routes_are_Jellyfins_sign_in_routes(string method, string path, string baseUrl, string expected)
    {
        Assert.Equal(expected, SignInRoutes.Match(method, path, baseUrl, out _).ToString());
    }

    [Theory]
    [InlineData(200, "Success")]
    [InlineData(401, "Failure")]
    [InlineData(403, "Failure")]
    [InlineData(404, "Failure")]
    [InlineData(400, "Neither")]
    [InlineData(500, "Neither")]
    public void Jellyfins_answers_mean_what_they_mean(int status, string expected)
    {
        Assert.Equal(expected, SignInRoutes.Outcome(status).ToString());
    }
}

/// <summary>The throttle in Edge's real pipeline, against a stand-in Jellyfin that says 401 to a wrong password.</summary>
[Collection(RegistryCollection.Name)]
public sealed class LoginThrottlePipelineTests : IAsyncLifetime
{
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(400);
    private EdgeRig _rig = null!;

    public async Task InitializeAsync()
    {
        _rig = await EdgeRig.StartAsync();
        await _rig.StartEdgeAsync(_rig.Options(throttle: new ThrottleOptions { FreePerPair = 2, FirstDelay = Step, MaxDelay = TimeSpan.FromSeconds(2) }));
        Assert.True(_rig.Service!.Gate.IsOpen, _rig.Service.Gate.Reason);
    }

    public async Task DisposeAsync()
    {
        await _rig.DisposeAsync();
        CampgroundLifetime.ResetForTests();
    }

    private async Task<(HttpResponseMessage Response, TimeSpan Took)> SignInAsync(EdgeClient client, string user, string pw)
    {
        var clock = Stopwatch.StartNew();
        var response = await client.Http.PostAsync("Users/AuthenticateByName", new StringContent("{\"Username\":\"" + user + "\",\"Pw\":\"" + pw + "\"}", Encoding.UTF8, "application/json"));
        return (response, clock.Elapsed);
    }

    [Fact]
    public async Task Failures_slow_down_other_users_go_straight_through_and_a_success_clears()
    {
        using var client = _rig.Client("127.0.0.7");
        for (var i = 0; i < 2; i++)
        {
            var (r, _) = await SignInAsync(client, "alice", "wrong");
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        }

        // Two free failures, then each next try waits: Step, then twice that.
        var (third, slow) = await SignInAsync(client, "alice", "wrong");
        var (bob, bobTook) = await SignInAsync(client, "bob", "wrong");
        var (right, slower) = await SignInAsync(client, "alice", "right");
        var (afterSuccess, afterTook) = await SignInAsync(client, "alice", "wrong");

        Assert.Equal(HttpStatusCode.Unauthorized, third.StatusCode);
        Assert.True(slow >= Step - TimeSpan.FromMilliseconds(50), "the third try waited only " + slow);
        Assert.True(bobTook < Step, "bob waited " + bobTook);
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
        Assert.True(slower >= (2 * Step) - TimeSpan.FromMilliseconds(50), "the fourth try waited only " + slower);
        Assert.True(afterTook < Step, "after a success alice still waited " + afterTook);
        Assert.Equal(6, _rig.Jellyfin.SignIns);

        // The body still reached Jellyfin whole after Edge read the name from it.
        Assert.Equal(HttpStatusCode.Unauthorized, afterSuccess.StatusCode);
    }
}
