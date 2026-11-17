using System.Net;
using Jellyfin.Plugin.Trailhead.Tests.Acme;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Setup;

/// <summary>A setup service whose every step throws, with the key in the message, to see what the controller lets out.</summary>
internal sealed class ExplodingSetup : SetupService
{
    internal ExplodingSetup(ILogger logger, ServicesOverrides overrides)
        : base(logger, overrides)
    {
    }

    private static InvalidOperationException Boom() => new("boom with " + ApiTestBase.Sentinel + " in the message");

    internal override SetupState GetState() => throw Boom();

    internal override Task<KeyOutcome> SaveKeyAsync(string? key, CancellationToken ct) => throw Boom();

    internal override Task<NameOutcome> CheckNameAsync(string? label, string? parent, CancellationToken ct) => throw Boom();

    internal override Task<NameOutcome> ClaimNameAsync(string? label, string? parent, CancellationToken ct) => throw Boom();
}

[Collection(RegistryCollection.Name)]
public sealed class ControllerFailureTests : ApiTestBase
{
    internal override SetupService MakeSetup(ServicesOverrides overrides, CertificateServices? certificates) => new ExplodingSetup(Log, overrides);

    [Theory]
    [InlineData("GET", "/Trailhead/setup")]
    [InlineData("POST", "/Trailhead/dynu-key")]
    [InlineData("POST", "/Trailhead/name/check")]
    [InlineData("POST", "/Trailhead/name/claim")]
    public async Task An_unexpected_exception_becomes_a_500_with_a_fixed_body_and_the_type_is_all_that_is_logged(string method, string path)
    {
        using var response = await SendAsync(new HttpMethod(method), path, body: method == "POST" ? "{\"Key\":\"" + Sentinel + "\",\"Label\":\"mymedia\",\"Parent\":\"freeddns.org\"}" : null);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("internal", Code(response));
        Assert.Equal(UiText.English("error.internal"), body);
        Assert.Contains("InvalidOperationException", Log.All);
        Assert.DoesNotContain("boom", Log.All);
        Assert.DoesNotContain(Sentinel, Log.All);
        Assert.DoesNotContain(Log.Entries, e => e.Level == LogLevel.Error);
    }
}
