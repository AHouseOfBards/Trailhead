using System.Net;
using Campground.Shared.Storage;
using Jellyfin.Plugin.Trailhead.Acme;
using Jellyfin.Plugin.Trailhead.Dns;
using Jellyfin.Plugin.Trailhead.Tests.Acme;
using Jellyfin.Plugin.Trailhead.Tests.Renewal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Setup;

[Collection(RegistryCollection.Name)]
public sealed class SetupStateTests : ApiTestBase
{
    private readonly MutableSettings _settings = new();
    private CertificateServices? _services;

    internal override CertificateServices? Certificates => _services ??= RenewalTestKit.Services(Log, _settings, new ServicesOverrides
    {
        Box = Box,
        RenewerFactory = () => new CannedRenewer(new ReconcileResult(ReconcileOutcome.Issued, null, null, null)),
    });

    [Fact]
    public async Task A_fresh_install_has_no_key_no_name_the_parents_in_order_and_has_not_run_yet()
    {
        using var response = await GetAsync("/Trailhead/setup");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await JsonAsync(response);
        var root = json.RootElement;
        Assert.Equal("None", root.GetProperty("DynuKey").GetString());
        Assert.Equal(string.Empty, root.GetProperty("Hostname").GetString());
        var parents = root.GetProperty("Parents").EnumerateArray().Select(p => (p.GetProperty("Name").GetString(), p.GetProperty("IsDefault").GetBoolean())).ToList();
        var expected = ParentDomains.Embedded().Offered.Select(p => (p.Name, p.IsDefault)).ToList();
        Assert.NotEmpty(expected);
        Assert.Equal(expected, parents);
        Assert.Equal(1, parents.Count(p => p.Item2));
        Assert.Equal("NotRunYet", root.GetProperty("Renewal").GetProperty("State").GetString());
        Assert.Equal(JsonValueKindNull, root.GetProperty("Renewal").GetProperty("At").ValueKind);
    }

    private const System.Text.Json.JsonValueKind JsonValueKindNull = System.Text.Json.JsonValueKind.Null;

    [Fact]
    public async Task A_key_that_opens_is_Saved_and_one_that_does_not_is_Reenter()
    {
        SeedKey();
        using var saved = await GetAsync("/Trailhead/setup");
        ConfigGate.Update(c => c.DynuKey = new DynuKey(RenewalTestKit.Box(9)).Seal(Sentinel));
        using var elsewhere = await GetAsync("/Trailhead/setup");

        using var a = await JsonAsync(saved);
        using var b = await JsonAsync(elsewhere);
        Assert.Equal("Saved", a.RootElement.GetProperty("DynuKey").GetString());
        Assert.Equal("Reenter", b.RootElement.GetProperty("DynuKey").GetString());
    }

    [Fact]
    public async Task The_last_renewal_shows_with_its_outcome_and_when_it_finished()
    {
        _settings.Value = new ServiceSettings("mymedia.freeddns.org", RenewalTestKit.Sealed(1, Sentinel));
        var before = DateTimeOffset.UtcNow.AddSeconds(-5);
        Assert.Equal(RenewalState.Ran, (await Certificates!.RenewAsync(CancellationToken.None)).State);

        using var response = await GetAsync("/Trailhead/setup");

        using var json = await JsonAsync(response);
        var renewal = json.RootElement.GetProperty("Renewal");
        Assert.Equal("Ran", renewal.GetProperty("State").GetString());
        Assert.Equal("Issued", renewal.GetProperty("Outcome").GetString());
        Assert.True(renewal.GetProperty("At").GetDateTimeOffset() >= before);
    }

    [Fact]
    public async Task A_renewal_with_nothing_to_renew_shows_NotSetUp_and_no_outcome()
    {
        await Certificates!.RenewAsync(CancellationToken.None);

        using var response = await GetAsync("/Trailhead/setup");

        using var json = await JsonAsync(response);
        var renewal = json.RootElement.GetProperty("Renewal");
        Assert.Equal("NotSetUp", renewal.GetProperty("State").GetString());
        Assert.Equal(JsonValueKindNull, renewal.GetProperty("Outcome").ValueKind);
    }

    [Fact]
    public async Task The_services_remember_the_last_report_in_memory_and_nothing_before_the_first_run()
    {
        Assert.Null(Certificates!.LastReport);

        await Certificates.RenewAsync(CancellationToken.None);

        Assert.NotNull(Certificates.LastReport);
        Assert.Equal(RenewalState.NotSetUp, Certificates.LastReport!.Report.State);
    }
}

/// <summary>The setup service and controller built the way Jellyfin builds them, through the real registrator.</summary>
[Collection(RegistryCollection.Name)]
public sealed class SetupRegistrationTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public SetupRegistrationTests()
    {
        RenewalTestKit.ForgetPlugin();
        CampgroundPaths.OverrideForTests(_folder.File("campground"));
    }

    public void Dispose()
    {
        RenewalTestKit.ForgetPlugin();
        _folder.Dispose();
    }

    private ServiceProvider Compose()
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton<IHostApplicationLifetime>(new NoLifetime());
        collection.AddSingleton(RenewalTestKit.Paths(_folder.File("jellyfin")));
        new PluginServiceRegistrator().RegisterServices(collection, null!);
        return collection.BuildServiceProvider();
    }

    private string[] Snapshot() => Directory.EnumerateFileSystemEntries(_folder.Path, "*", SearchOption.AllDirectories).Order().ToArray();

    [Fact]
    public void The_setup_service_and_the_controller_resolve_and_do_no_work()
    {
        _ = new Plugin(RenewalTestKit.Paths(_folder.File("jellyfin")), new RenewalTestKit.XmlStub());
        using var provider = Compose();
        var before = Snapshot();

        var service = provider.GetRequiredService<SetupService>();
        var controller = ActivatorUtilities.CreateInstance<Api.TrailheadController>(provider);

        Assert.Same(service, provider.GetRequiredService<SetupService>());
        Assert.NotNull(controller);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void The_setup_service_is_not_made_in_a_way_that_reads_the_key_store_at_construction()
    {
        using var provider = Compose();
        var before = Snapshot();

        _ = provider.GetRequiredService<SetupService>();

        Assert.Empty(before.Where(f => f.Contains("campground", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(before, Snapshot());
    }

    private sealed class NoLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }
}
