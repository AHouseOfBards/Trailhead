using Jellyfin.Plugin.Trailhead.Acme;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Acme;

public sealed class AcmeEndpointsTests
{
    [Fact]
    public void Staging_is_the_default()
    {
        Assert.Equal(AcmeEndpoints.Staging, AcmeEndpoints.Resolve());
        Assert.Equal(AcmeEndpoints.Staging, AcmeEndpoints.Resolve(production: false));
        Assert.Contains("staging", AcmeEndpoints.Staging.Host, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_only_when_asked_for()
    {
        var production = AcmeEndpoints.Resolve(production: true);

        Assert.Equal(AcmeEndpoints.Production, production);
        Assert.DoesNotContain("staging", production.Host, StringComparison.Ordinal);
        Assert.NotEqual(AcmeEndpoints.Staging, production);
    }

    [Fact]
    public void Both_are_https_directories()
    {
        Assert.All(new[] { AcmeEndpoints.Staging, AcmeEndpoints.Production }, u =>
        {
            Assert.Equal("https", u.Scheme);
            Assert.EndsWith("/directory", u.AbsolutePath, StringComparison.Ordinal);
        });
    }
}
