using Jellyfin.Plugin.Trailhead.Acme;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailhead.Dns;

internal enum ClaimKind
{
    /// <summary>Already on this account: reuse it.</summary>
    Ours,

    /// <summary>Check only: nothing says it is taken. Creating it is the real test.</summary>
    Free,

    Claimed,

    /// <summary>Someone else has it. <see cref="NameClaim.Alternatives"/> has the same label under the other parents.</summary>
    Taken,

    AccountFull,

    /// <summary>Dynu would not create the name through the API. It can be added in Dynu's own site and then found.</summary>
    DynuRefused,

    BadKey,

    /// <summary>Transient: no answer, or an answer that says try later.</summary>
    Unavailable,

    /// <summary>The label or parent is not usable. Nothing was sent to Dynu.</summary>
    Invalid,
}

internal sealed record NameClaim(ClaimKind Kind, string Message, string? Name = null, DynuDomain? Domain = null, IReadOnlyList<string>? Alternatives = null, int Status = 0, string? DynuType = null);

/// <summary>
/// Checks and claims a hostname under one of the offered parents. It gives a result, never an exception (except for a
/// cancel), so a Dynu problem can't take setup down with it.
/// </summary>
internal sealed class NameClaimer
{
    private readonly DynuClient _dynu;
    private readonly ParentDomains _parents;
    private readonly ILogger _logger;

    internal NameClaimer(DynuClient dynu, ParentDomains parents, ILogger logger)
    {
        _dynu = dynu;
        _parents = parents;
        _logger = logger;
    }

    /// <summary>
    /// Looks without creating. Ours when the account has it; Taken when Dynu roots the name at itself on an account
    /// that isn't ours; otherwise Free, which is only advice (a name can be held with no address published).
    /// </summary>
    internal async Task<NameClaim> CheckAsync(string? label, string? parent, CancellationToken ct)
    {
        var built = HostLabel.Build(label, parent, _parents);
        if (!built.Ok)
        {
            return Invalid(built.Problem!);
        }

        var name = built.Name!;
        var parentName = ParentDomains.Normalize(parent);
        try
        {
            var ours = (await _dynu.ListDomainsAsync(ct).ConfigureAwait(false)).FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (ours is not null)
            {
                return Done(ClaimKind.Ours, "That name is already on your Dynu account. Trailhead will use it.", name, parentName, domain: ours);
            }

            try
            {
                var root = await _dynu.GetRootAsync(name, ct).ConfigureAwait(false);
                if (root is not null && root.DomainName.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return Done(ClaimKind.Taken, TakenMessage, name, parentName, alternatives: Alternatives(HostLabel.Check(label).Label, parentName));
                }
            }
            catch (DynuException e) when (e.Kind == FailureKind.Permanent && !e.AuthRejected)
            {
                // Dynu not knowing the name (501) is the common case for a free one. Not a reason to stop.
            }

            return Done(ClaimKind.Free, "That name looks free.", name, parentName);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Failed(e, parentName, name);
        }
    }

    /// <summary>Creates the name, or finds it if the account already has it, so running it twice makes one name.</summary>
    internal async Task<NameClaim> ClaimAsync(string? label, string? parent, CancellationToken ct)
    {
        var built = HostLabel.Build(label, parent, _parents);
        if (!built.Ok)
        {
            return Invalid(built.Problem!);
        }

        var name = built.Name!;
        var parentName = ParentDomains.Normalize(parent);
        try
        {
            var result = await _dynu.CreateDomainAsync(name, ct).ConfigureAwait(false);
            return result.Created
                ? Done(ClaimKind.Claimed, "Your name is set up.", name, parentName, domain: result.Domain)
                : Done(ClaimKind.Ours, "That name is already on your Dynu account. Trailhead will use it.", name, parentName, domain: result.Domain);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Failed(e, parentName, name, HostLabel.Check(label).Label);
        }
    }

    private const string TakenMessage = "Someone else has that name. Try it with another ending.";

    private IReadOnlyList<string> Alternatives(string label, string parent)
        => _parents.Others(parent).Select(p => label + "." + p.Name).ToList();

    private static NameClaim Invalid(string problem) => new(ClaimKind.Invalid, problem);

    private NameClaim Done(ClaimKind kind, string message, string name, string parent, DynuDomain? domain = null, IReadOnlyList<string>? alternatives = null)
    {
        // The kind and the parent only: the name is the user's own public address, and nothing here needs it in a log.
        _logger.LogInformation("Trailhead name check under {Parent}: {Kind}.", parent, kind);
        return new NameClaim(kind, message, name, domain, alternatives);
    }

    // How a Dynu answer maps to a kind. 501 and 505 are Dynu's own codes (they describe the request, so there is no
    // retry). 505 is "that exists already" (someone else's name). 501 is "Argument Exception", which the live account
    // gave for names nobody owns (research/dynu-live-check.md), so it must never be read as Taken: that would tell a
    // user a free name is gone, and offer them the same thing under other parents that fails the same way.
    private NameClaim Failed(Exception error, string parent, string? name = null, string? label = null)
    {
        var status = 0;
        string? type = null;
        ClaimKind kind;
        string message;
        if (error is DynuException d)
        {
            status = d.Status;
            type = d.ErrorType;
            if (d.AuthRejected)
            {
                kind = ClaimKind.BadKey;
                message = "Dynu didn't accept your key. Check it and try again.";
            }
            else if (d.Kind == FailureKind.Transient)
            {
                kind = ClaimKind.Unavailable;
                message = UnavailableMessage;
            }
            else if (d.FromEnvelope && d.Status == 503)
            {
                kind = ClaimKind.AccountFull;
                message = "Your Dynu account is full. Delete a name there, then try again.";
            }
            else if (d.Status == 505)
            {
                kind = ClaimKind.Taken;
                message = TakenMessage;
            }
            else if (d.Status == 501)
            {
                kind = ClaimKind.DynuRefused;
                message = "Dynu won't make that name from here. Add it on Dynu's own site and Trailhead will find it.";
            }
            else
            {
                kind = ClaimKind.Unavailable;
                message = UnavailableMessage;
            }
        }
        else
        {
            // Not from Dynu's side of the wire; the type is all the log gets, since a message could carry anything.
            kind = ClaimKind.Unavailable;
            message = UnavailableMessage;
            _logger.LogWarning("Trailhead name check failed with {ErrorType}.", error.GetType().Name);
        }

        _logger.LogInformation("Trailhead name check under {Parent}: {Kind} (Dynu status {Status}).", parent, kind, status);
        var alternatives = kind == ClaimKind.Taken && label is not null ? Alternatives(label, parent) : null;
        return new NameClaim(kind, message, name, null, alternatives, status, type);
    }

    private const string UnavailableMessage = "Couldn't reach Dynu just now. Try again in a minute.";
}
