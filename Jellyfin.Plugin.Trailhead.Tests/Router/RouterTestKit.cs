using System.Net;
using Jellyfin.Plugin.Trailhead.Router;
using Jellyfin.Plugin.Trailhead.TestSupport;
using Jellyfin.Plugin.Trailhead.Tests.Acme;
using Microsoft.Extensions.Time.Testing;

namespace Jellyfin.Plugin.Trailhead.Tests.Router;

/// <summary>A <see cref="RouterMapper"/> wired to a <see cref="FakeIgd"/> through the real finder and SOAP client.</summary>
internal sealed class RouterRig : IAsyncDisposable
{
    internal const string Lan = "192.168.1.50";

    private readonly TempFolder _folder = new();
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private RouterRig(FakeIgd igd)
    {
        Igd = igd;
        Igd.Clock = () => Time.GetUtcNow();
        Store = new ForwardsStore(_folder.File("forwards.json"));
        Finder = new RouterFinder(_http, () => new[] { igd.Plan }, TimeSpan.FromMilliseconds(700));
        Mapper = new RouterMapper(Finder, _ => LanAddress, s => new IgdClient(_http, s), Store, Time, new SemaphoreSlim(1, 1), Log);
    }

    internal FakeIgd Igd { get; }

    internal FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero));

    internal ListLogger Log { get; } = new();

    internal ForwardsStore Store { get; }

    internal RouterFinder Finder { get; }

    internal RouterMapper Mapper { get; }

    /// <summary>This machine's address on the router's subnet. DHCP moves it when a test says so; null is "none".</summary>
    internal IPAddress? LanAddress { get; set; } = IPAddress.Parse(Lan);

    internal HttpClient Http => _http;

    internal static async Task<RouterRig> StartAsync(IPAddress? ssdpAddress = null) => new(await FakeIgd.StartAsync(ssdpAddress));

    public async ValueTask DisposeAsync()
    {
        await Igd.DisposeAsync();
        _http.Dispose();
        _folder.Dispose();
    }
}
