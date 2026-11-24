using System.Net;
using Jellyfin.Plugin.Trailhead.Router;
using Jellyfin.Plugin.Trailhead.TestSupport;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Router;

/// <summary>The lease task's work and Remove's, against a fake IGD. Cases from RASA's portkeep tests, plus the list.</summary>
public sealed class RenewAndReleaseTests
{
    [Fact]
    public async Task With_nothing_recorded_a_renewal_does_nothing_not_even_a_search()
    {
        await using var rig = await RouterRig.StartAsync();

        var reports = await rig.Mapper.RenewAsync(CancellationToken.None);

        Assert.Empty(reports);
        Assert.Empty(rig.Igd.Searches);
        Assert.Empty(rig.Igd.Calls);
    }

    [Fact]
    public async Task A_permanent_mapping_pointing_here_is_left_alone()
    {
        await using var rig = await RouterRig.StartAsync();
        await rig.Mapper.MapAsync(443, 8443, "TCP", CancellationToken.None);
        rig.Time.Advance(TimeSpan.FromDays(30));

        var report = Assert.Single(await rig.Mapper.RenewAsync(CancellationToken.None));

        Assert.Equal(ForwardOutcome.Ok, report.Outcome);
        Assert.False(report.Fault);
        Assert.Equal(1, rig.Igd.Adds);
    }

    [Fact]
    public async Task A_lease_is_renewed_once_it_is_half_gone_and_not_before()
    {
        await using var rig = await RouterRig.StartAsync();
        rig.Igd.RefusePermanentWith = UpnpCodes.OnlyPermanentLeases;
        await rig.Mapper.MapAsync(443, 8443, "TCP", CancellationToken.None);
        var addsAfterMap = rig.Igd.Adds;

        rig.Time.Advance(TimeSpan.FromDays(1));
        var early = Assert.Single(await rig.Mapper.RenewAsync(CancellationToken.None));
        rig.Time.Advance(TimeSpan.FromDays(3));
        var late = Assert.Single(await rig.Mapper.RenewAsync(CancellationToken.None));

        Assert.Equal(ForwardOutcome.Ok, early.Outcome);
        Assert.Equal(ForwardOutcome.Renewed, late.Outcome);
        Assert.Equal(addsAfterMap + 2, rig.Igd.Adds); // the renewal asks permanent first, then the week
        var m = Assert.Single(rig.Igd.Mappings);
        Assert.Equal(RouterMapper.WeekLease, m.Lease);
        Assert.Equal(rig.Time.GetUtcNow(), m.Added);
    }

    [Fact]
    public async Task A_router_that_never_counts_down_is_still_renewed_by_our_own_clock()
    {
        await using var rig = await RouterRig.StartAsync();
        rig.Igd.RefusePermanentWith = UpnpCodes.OnlyPermanentLeases;
        rig.Igd.LeaseDoesNotCountDown = true;
        await rig.Mapper.MapAsync(443, 8443, "TCP", CancellationToken.None);

        rig.Time.Advance(TimeSpan.FromDays(4));
        var report = Assert.Single(await rig.Mapper.RenewAsync(CancellationToken.None));

        Assert.Equal(ForwardOutcome.Renewed, report.Outcome);
    }

    [Fact]
    public async Task A_router_restart_that_lost_the_mapping_and_moved_its_control_url_gets_it_back()
    {
        await using var rig = await RouterRig.StartAsync();
        await rig.Mapper.MapAsync(443, 8443, "TCP", CancellationToken.None);
        rig.Igd.Restart();

        var report = Assert.Single(await rig.Mapper.RenewAsync(CancellationToken.None));

        Assert.Equal(ForwardOutcome.Readded, report.Outcome);
        var m = Assert.Single(rig.Igd.Mappings);
        Assert.Equal((443, 8443, RouterRig.Lan), (m.External, m.Internal, m.Client));
    }

    [Fact]
    public async Task A_lapsed_lease_is_added_again()
    {
        await using var rig = await RouterRig.StartAsync();
        rig.Igd.MaxLease = 7200;
        await rig.Mapper.MapAsync(443, 8443, "TCP", CancellationToken.None);

        rig.Time.Advance(TimeSpan.FromHours(2));
        var report = Assert.Single(await rig.Mapper.RenewAsync(CancellationToken.None));

        Assert.Equal(ForwardOutcome.Readded, report.Outcome);
        Assert.Single(rig.Igd.Mappings);
    }

    [Fact]
    public async Task A_mapping_that_now_points_at_another_device_is_never_renewed_or_deleted()
    {
        await using var rig = await RouterRig.StartAsync();
        rig.Igd.RefusePermanentWith = UpnpCodes.OnlyPermanentLeases;
        await rig.Mapper.MapAsync(443, 8443, "TCP", CancellationToken.None);
        rig.Igd.Forget(443);
        rig.Igd.Seed(443, 443, "192.168.1.77", "NAS");
        var adds = rig.Igd.Adds;

        rig.Time.Advance(TimeSpan.FromDays(5));
        var report = Assert.Single(await rig.Mapper.RenewAsync(CancellationToken.None));

        Assert.Equal(ForwardOutcome.PointsElsewhere, report.Outcome);
        Assert.True(report.Fault);
        Assert.Equal(adds, rig.Igd.Adds);
        Assert.Equal(0, rig.Igd.Deletes);
        Assert.Contains(rig.Igd.Mappings, m => m is { External: 443, Client: "192.168.1.77", Description: "NAS" });
    }

    [Fact]
    public async Task When_DHCP_moves_this_machine_the_forward_follows_it()
    {
        await using var rig = await RouterRig.StartAsync();
        await rig.Mapper.MapAsync(443, 8443, "TCP", CancellationToken.None);
        rig.LanAddress = IPAddress.Parse("192.168.1.61");

        var report = Assert.Single(await rig.Mapper.RenewAsync(CancellationToken.None));

        Assert.Equal(ForwardOutcome.Moved, report.Outcome);
        var m = Assert.Single(rig.Igd.Mappings);
        Assert.Equal("192.168.1.61", m.Client);
        Assert.Equal("192.168.1.61", Assert.Single(rig.Store.Load()).InternalClient);
    }

    [Fact]
    public async Task A_missing_router_is_a_blip_for_six_hours_and_a_fault_after()
    {
        await using var rig = await RouterRig.StartAsync();
        await rig.Mapper.MapAsync(443, 8443, "TCP", CancellationToken.None);
        rig.Igd.Mode = SsdpMode.Silent;

        rig.Time.Advance(TimeSpan.FromHours(5));
        var blip = Assert.Single(await rig.Mapper.RenewAsync(CancellationToken.None));
        rig.Time.Advance(TimeSpan.FromHours(2));
        var fault = Assert.Single(await rig.Mapper.RenewAsync(CancellationToken.None));

        Assert.Equal(ForwardOutcome.RouterMissing, blip.Outcome);
        Assert.False(blip.Fault);
        Assert.Equal(ForwardOutcome.RouterMissing, fault.Outcome);
        Assert.True(fault.Fault);
        Assert.Single(rig.Store.Load());
    }

    [Fact]
    public async Task A_pending_record_whose_add_never_landed_is_dropped_not_added()
    {
        await using var rig = await RouterRig.StartAsync();
        rig.Store.Put(new ForwardRecord("crashed", RouterMapper.TrailheadOwner, "TCP", 443, 8443, RouterRig.Lan, ForwardStates.Pending, 0, rig.Time.GetUtcNow(), null, null));

        var report = Assert.Single(await rig.Mapper.RenewAsync(CancellationToken.None));

        Assert.Equal(ForwardOutcome.Dropped, report.Outcome);
        Assert.Equal(0, rig.Igd.Adds);
        Assert.Empty(rig.Store.Load());
    }

    [Fact]
    public async Task A_pending_record_whose_add_did_land_is_adopted()
    {
        await using var rig = await RouterRig.StartAsync();
        rig.Igd.Seed(443, 8443, RouterRig.Lan, IgdClient.Description);
        rig.Store.Put(new ForwardRecord("crashed", RouterMapper.TrailheadOwner, "TCP", 443, 8443, RouterRig.Lan, ForwardStates.Pending, 0, rig.Time.GetUtcNow(), null, null));

        var report = Assert.Single(await rig.Mapper.RenewAsync(CancellationToken.None));

        Assert.Equal(ForwardOutcome.Ok, report.Outcome);
        Assert.Equal(ForwardStates.Active, Assert.Single(rig.Store.Load()).State);
    }

    [Fact]
    public async Task Release_deletes_only_our_forwards_and_empties_the_list()
    {
        await using var rig = await RouterRig.StartAsync();
        rig.Igd.Seed(9000, 9000, "192.168.1.77", "Game");
        rig.Igd.Seed(8096, 8096, RouterRig.Lan, "Admin's own forward");
        await rig.Mapper.MapAsync(443, 8443, "TCP", CancellationToken.None);

        var reports = await rig.Mapper.ReleaseAsync(null, CancellationToken.None);

        Assert.Equal(ForwardOutcome.Released, Assert.Single(reports).Outcome);
        Assert.Empty(rig.Store.Load());
        Assert.Equal(new[] { "443" }, rig.Igd.Calls.Where(c => c.Action == "DeletePortMapping").Select(c => c.Args["NewExternalPort"]));
        Assert.Equal(new[] { 8096, 9000 }, rig.Igd.Mappings.Select(m => m.External).Order());
    }

    [Fact]
    public async Task Release_leaves_a_port_that_now_goes_to_another_device()
    {
        await using var rig = await RouterRig.StartAsync();
        await rig.Mapper.MapAsync(443, 8443, "TCP", CancellationToken.None);
        rig.Igd.Forget(443);
        rig.Igd.Seed(443, 443, "192.168.1.77", "NAS");

        var report = Assert.Single(await rig.Mapper.ReleaseAsync(null, CancellationToken.None));

        Assert.Equal(ForwardOutcome.LeftAlone, report.Outcome);
        Assert.Equal(0, rig.Igd.Deletes);
        Assert.Contains(rig.Igd.Mappings, m => m.Client == "192.168.1.77");
        Assert.Empty(rig.Store.Load());
    }

    [Fact]
    public async Task Another_devices_mapping_to_the_same_inside_port_with_our_label_is_still_not_ours()
    {
        // Another Trailhead on the LAN (or anything copying the label) made it. Only the address decides.
        await using var rig = await RouterRig.StartAsync();
        await rig.Mapper.MapAsync(443, 8443, "TCP", CancellationToken.None);
        rig.Igd.Forget(443);
        rig.Igd.Seed(443, 8443, "192.168.1.77", IgdClient.Description);

        var renewed = Assert.Single(await rig.Mapper.RenewAsync(CancellationToken.None));
        var released = Assert.Single(await rig.Mapper.ReleaseAsync(null, CancellationToken.None));

        Assert.Equal(ForwardOutcome.PointsElsewhere, renewed.Outcome);
        Assert.Equal(ForwardOutcome.LeftAlone, released.Outcome);
        Assert.Equal(0, rig.Igd.Deletes);
        Assert.Contains(rig.Igd.Mappings, m => m is { External: 443, Internal: 8443, Client: "192.168.1.77" });
    }

    [Fact]
    public async Task Release_with_no_router_keeps_the_record_for_the_next_try()
    {
        await using var rig = await RouterRig.StartAsync();
        await rig.Mapper.MapAsync(443, 8443, "TCP", CancellationToken.None);
        rig.Igd.Mode = SsdpMode.Silent;

        var report = Assert.Single(await rig.Mapper.ReleaseAsync(null, CancellationToken.None));

        Assert.Equal(ForwardOutcome.RouterMissing, report.Outcome);
        Assert.Single(rig.Store.Load());
        Assert.Single(rig.Igd.Mappings);
    }

    [Fact]
    public async Task Release_of_one_id_leaves_the_others()
    {
        await using var rig = await RouterRig.StartAsync();
        var a = await rig.Mapper.MapAsync(443, 8443, "TCP", CancellationToken.None);
        var b = await rig.Mapper.MapAsync(9443, 9443, "TCP", CancellationToken.None);

        await rig.Mapper.ReleaseAsync(a.Forward!.Id, CancellationToken.None);

        Assert.Equal(b.Forward!.Id, Assert.Single(rig.Store.Load()).Id);
        Assert.Equal(9443, Assert.Single(rig.Igd.Mappings).External);
    }

    [Fact]
    public async Task A_release_and_a_renewal_at_once_take_turns_on_the_router()
    {
        await using var rig = await RouterRig.StartAsync();
        rig.Igd.RefusePermanentWith = UpnpCodes.OnlyPermanentLeases;
        await rig.Mapper.MapAsync(443, 8443, "TCP", CancellationToken.None);
        rig.Time.Advance(TimeSpan.FromDays(5));

        var renew = rig.Mapper.RenewAsync(CancellationToken.None);
        var release = rig.Mapper.ReleaseAsync(null, CancellationToken.None);
        await Task.WhenAll(renew, release);

        // Whatever order they ran in, Remove's answer stands: nothing left on the router or the list.
        Assert.Empty(rig.Igd.Mappings);
        Assert.Empty(rig.Store.Load());
    }
}
