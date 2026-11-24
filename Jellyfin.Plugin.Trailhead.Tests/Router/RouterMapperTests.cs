using System.Net;
using Jellyfin.Plugin.Trailhead.Router;
using Jellyfin.Plugin.Trailhead.TestSupport;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Router;

/// <summary>Setup's router step, against a fake IGD on real sockets.</summary>
public sealed class RouterMapperTests
{
    [Fact]
    public async Task Maps_outside_443_to_8443_permanently_and_reports_what_the_router_read_back()
    {
        await using var rig = await RouterRig.StartAsync();

        var result = await rig.Mapper.MapAsync(443, 8443, "TCP", CancellationToken.None);

        Assert.Equal(MapOutcome.Mapped, result.Outcome);
        var m = Assert.Single(rig.Igd.Mappings);
        Assert.Equal((443, 8443, RouterRig.Lan, IgdClient.Description, 0), (m.External, m.Internal, m.Client, m.Description, m.Lease));
        Assert.NotNull(result.Granted);
        Assert.True(result.Granted!.Permanent);
        var record = Assert.Single(rig.Store.Load());
        Assert.Equal((443, 8443, RouterRig.Lan, ForwardStates.Active), (record.ExternalPort, record.InternalPort, record.InternalClient, record.State));
        Assert.NotNull(record.ConfirmedAt);
    }

    [Fact]
    public async Task A_router_that_says_OK_and_stores_nothing_is_not_mapped_and_nothing_stays_recorded()
    {
        await using var rig = await RouterRig.StartAsync();
        rig.Igd.StoreNothing = true;

        var result = await rig.Mapper.MapAsync(443, 8443, "TCP", CancellationToken.None);

        Assert.Equal(MapOutcome.NotConfirmed, result.Outcome);
        Assert.Empty(rig.Store.Load());
    }

    [Theory]
    [InlineData(UpnpCodes.OnlyPermanentLeases)]
    [InlineData(UpnpCodes.InvalidArgs)]
    [InlineData(UpnpCodes.ActionFailed)]
    public async Task A_refused_permanent_lease_is_asked_again_for_a_week(int code)
    {
        await using var rig = await RouterRig.StartAsync();
        rig.Igd.RefusePermanentWith = code;

        var result = await rig.Mapper.MapAsync(443, 8443, "TCP", CancellationToken.None);

        Assert.Equal(MapOutcome.MappedWithLease, result.Outcome);
        Assert.Equal(new[] { "0", "604800" }, rig.Igd.Calls.Where(c => c.Action == "AddPortMapping").Select(c => c.Args["NewLeaseDuration"]));
        Assert.Equal(RouterMapper.WeekLease, result.Granted!.LeaseSeconds);
        Assert.Equal(RouterMapper.WeekLease, rig.Store.Load().Single().LeaseSeconds);
    }

    [Fact]
    public async Task A_router_that_caps_leases_below_a_week_gets_an_hour()
    {
        await using var rig = await RouterRig.StartAsync();
        rig.Igd.MaxLease = 7200;

        var result = await rig.Mapper.MapAsync(443, 8443, "TCP", CancellationToken.None);

        Assert.Equal(MapOutcome.MappedWithLease, result.Outcome);
        Assert.Equal(RouterMapper.HourLease, result.Granted!.LeaseSeconds);
    }

    [Fact]
    public async Task The_lease_reported_is_the_one_granted_not_the_one_asked_for()
    {
        await using var rig = await RouterRig.StartAsync();
        rig.Igd.RefusePermanentWith = UpnpCodes.OnlyPermanentLeases;
        rig.Igd.GrantLease = 1800;

        var result = await rig.Mapper.MapAsync(443, 8443, "TCP", CancellationToken.None);

        Assert.Equal(MapOutcome.MappedWithLease, result.Outcome);
        Assert.Equal(1800, result.Granted!.LeaseSeconds);
        Assert.Equal(1800, rig.Store.Load().Single().LeaseSeconds);
    }

    [Fact]
    public async Task A_same_port_only_router_724_is_remapped_to_outside_8443_and_the_record_holds_8443()
    {
        await using var rig = await RouterRig.StartAsync();
        rig.Igd.SamePortRequired = true;

        var result = await rig.Mapper.MapAsync(443, 8443, "TCP", CancellationToken.None);

        Assert.Equal(MapOutcome.Remapped, result.Outcome);
        Assert.Equal(FallbackReason.SamePortRequired, result.Reason);
        Assert.Equal(8443, result.Forward!.ExternalPort);
        Assert.Equal(8443, result.Granted!.ExternalPort);
        var m = Assert.Single(rig.Igd.Mappings);
        Assert.Equal((8443, 8443), (m.External, m.Internal));
        Assert.Equal(8443, Assert.Single(rig.Store.Load()).ExternalPort);
    }

    [Fact]
    public async Task Outside_443_going_to_another_device_is_left_alone_and_8443_is_mapped_instead()
    {
        await using var rig = await RouterRig.StartAsync();
        rig.Igd.Seed(443, 443, "192.168.1.77", "NAS");

        var result = await rig.Mapper.MapAsync(443, 8443, "TCP", CancellationToken.None);

        Assert.Equal(MapOutcome.Remapped, result.Outcome);
        Assert.Equal(FallbackReason.TakenByOtherDevice, result.Reason);
        Assert.Contains(rig.Igd.Mappings, m => m is { External: 443, Client: "192.168.1.77", Description: "NAS" });
        Assert.DoesNotContain(rig.Igd.Calls, c => c.Action is "AddPortMapping" or "DeletePortMapping" && c.Args["NewExternalPort"] == "443");
        Assert.Equal(new[] { 8443 }, rig.Store.Load().Select(r => r.ExternalPort));
    }

    [Fact]
    public async Task A_conflict_on_a_port_with_no_fallback_is_reported_and_never_taken_over()
    {
        await using var rig = await RouterRig.StartAsync();

        // Unreadable entries, so only the add's 718 says the port is taken.
        rig.Igd.Seed(9000, 9000, "192.168.1.77", "Game");
        rig.Igd.ReadsFail = true;

        var result = await rig.Mapper.MapAsync(9000, 9000, "TCP", CancellationToken.None);

        Assert.Equal(MapOutcome.Conflict, result.Outcome);
        Assert.Equal(UpnpCodes.Conflict, result.UpnpCode);
        Assert.Contains(rig.Igd.Mappings, m => m is { External: 9000, Client: "192.168.1.77" });
        Assert.Equal(0, rig.Igd.Deletes);
        Assert.Empty(rig.Store.Load());
    }

    [Fact]
    public async Task With_443_and_8443_both_taken_it_is_a_conflict_and_nothing_is_recorded()
    {
        await using var rig = await RouterRig.StartAsync();
        rig.Igd.Seed(443, 443, "192.168.1.77");
        rig.Igd.Seed(8443, 8443, "192.168.1.78");

        var result = await rig.Mapper.MapAsync(443, 8443, "TCP", CancellationToken.None);

        Assert.Equal(MapOutcome.Conflict, result.Outcome);
        Assert.Equal(0, rig.Igd.Adds);
        Assert.Empty(rig.Store.Load());
    }

    [Fact]
    public async Task A_forward_to_this_machine_that_Trailhead_did_not_make_is_not_recorded_or_touched()
    {
        await using var rig = await RouterRig.StartAsync();
        rig.Igd.Seed(443, 8443, RouterRig.Lan, "Admin's own forward");

        var result = await rig.Mapper.MapAsync(443, 8443, "TCP", CancellationToken.None);
        var released = await rig.Mapper.ReleaseAsync(null, CancellationToken.None);

        Assert.Equal(MapOutcome.AlreadyForwarded, result.Outcome);
        Assert.Empty(rig.Store.Load());
        Assert.Empty(released);
        Assert.Equal(0, rig.Igd.Adds);
        Assert.Equal(0, rig.Igd.Deletes);
        Assert.Single(rig.Igd.Mappings);
    }

    [Fact]
    public async Task Running_the_step_again_keeps_the_mapping_instead_of_adding_a_second_time()
    {
        await using var rig = await RouterRig.StartAsync();

        var first = await rig.Mapper.MapAsync(443, 8443, "TCP", CancellationToken.None);
        var again = await rig.Mapper.MapAsync(443, 8443, "TCP", CancellationToken.None);

        Assert.Equal(MapOutcome.Mapped, first.Outcome);
        Assert.Equal(MapOutcome.Mapped, again.Outcome);
        Assert.Equal(1, rig.Igd.Adds);
        Assert.Equal(first.Forward!.Id, again.Forward!.Id);
        Assert.Single(rig.Store.Load());
    }

    [Fact]
    public async Task The_forward_is_recorded_before_the_router_is_asked()
    {
        await using var rig = await RouterRig.StartAsync();
        IReadOnlyList<ForwardRecord>? seenAtAdd = null;
        rig.Igd.OnAdd = () => seenAtAdd = rig.Store.Load();

        await rig.Mapper.MapAsync(443, 8443, "TCP", CancellationToken.None);

        var pending = Assert.Single(seenAtAdd!);
        Assert.Equal((443, ForwardStates.Pending, RouterRig.Lan), (pending.ExternalPort, pending.State, pending.InternalClient));
    }

    [Fact]
    public async Task No_router_means_no_router_and_nothing_recorded()
    {
        await using var rig = await RouterRig.StartAsync();
        rig.Igd.Mode = SsdpMode.Silent;

        var result = await rig.Mapper.MapAsync(443, 8443, "TCP", CancellationToken.None);

        Assert.Equal(MapOutcome.NoRouter, result.Outcome);
        Assert.Equal(RouterStatus.NoReply, result.Router);
        Assert.Empty(rig.Store.Load());
        Assert.Empty(rig.Igd.Calls);
    }

    [Fact]
    public async Task Without_an_address_on_the_routers_subnet_nothing_is_asked()
    {
        await using var rig = await RouterRig.StartAsync();
        rig.LanAddress = null;

        var result = await rig.Mapper.MapAsync(443, 8443, "TCP", CancellationToken.None);

        Assert.Equal(MapOutcome.NoLanAddress, result.Outcome);
        Assert.Equal(0, rig.Igd.Adds);
        Assert.Empty(rig.Store.Load());
    }

    [Fact]
    public async Task A_v2_only_router_is_mapped_through_its_v2_service()
    {
        await using var rig = await RouterRig.StartAsync();
        rig.Igd.ServiceType = FakeIgd.V2;
        rig.Igd.Mode = SsdpMode.V2Only;

        var result = await rig.Mapper.MapAsync(443, 8443, "TCP", CancellationToken.None);

        Assert.Equal(MapOutcome.Mapped, result.Outcome);
        Assert.Single(rig.Igd.Mappings);
    }
}
