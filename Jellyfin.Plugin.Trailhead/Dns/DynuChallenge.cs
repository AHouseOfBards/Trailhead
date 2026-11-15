using System.Globalization;
using Jellyfin.Plugin.Trailhead.Acme;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailhead.Dns;

/// <summary>DNS-01 against Dynu: the TXT record goes in through the API, and the wait asks every authoritative nameserver.</summary>
internal sealed class DynuChallenge : IDnsChallenge
{
    private readonly DynuClient _client;
    private readonly AuthoritativeDnsWaiter _waiter;
    private readonly ILogger _logger;

    internal DynuChallenge(DynuClient client, AuthoritativeDnsWaiter waiter, ILogger logger)
    {
        _client = client;
        _waiter = waiter;
        _logger = logger;
    }

    public async Task<string> SetTxtAsync(string name, string value, CancellationToken ct)
    {
        var fqdn = name.Trim().TrimEnd('.');
        var domains = await _client.ListDomainsAsync(ct).ConfigureAwait(false);

        // The longest match: with both "example.org" and "home.example.org" on the account, the record belongs in
        // the more specific zone, since that is the one that has the nameserver's answer for the name.
        var domain = domains
            .Where(d => fqdn.Equals(d.Name, StringComparison.OrdinalIgnoreCase) || fqdn.EndsWith("." + d.Name, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d.Name.Length)
            .FirstOrDefault();
        if (domain is null)
        {
            throw new DynuException(FailureKind.Permanent, "Your Dynu account has no domain that " + fqdn + " belongs to.");
        }

        var node = fqdn.Length == domain.Name.Length ? string.Empty : fqdn[..(fqdn.Length - domain.Name.Length - 1)];
        var recordId = await _client.AddTxtAsync(domain.Id, node, value, ct).ConfigureAwait(false);
        _logger.LogInformation("Trailhead published a DNS challenge record on Dynu domain {DomainId} (record {RecordId}).", domain.Id, recordId);
        return domain.Id.ToString(CultureInfo.InvariantCulture) + ":" + recordId.ToString(CultureInfo.InvariantCulture);
    }

    public async Task RemoveAsync(string handle, CancellationToken ct)
    {
        var parts = handle.Split(':');
        if (parts.Length != 2
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var domainId)
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var recordId))
        {
            throw new DynuException(FailureKind.Permanent, "A Dynu challenge handle looks like <domain id>:<record id>.");
        }

        await _client.DeleteRecordAsync(domainId, recordId, ct).ConfigureAwait(false);
    }

    public Task WaitUntilVisibleAsync(string name, string value, CancellationToken ct) => _waiter.WaitAsync(name, value, ct);
}
