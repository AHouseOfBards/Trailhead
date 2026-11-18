using Campground.Shared.I18n;
using Jellyfin.Plugin.Trailhead.Acme;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailhead.Dns;

internal enum ClaimKind
{
    /// <summary>Already on this account: reuse it.</summary>
    Ours,

    /// <summary>Check only: not on this account, and Dynu can't say more until the name is claimed.</summary>
    Unchecked,

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

/// <param name="Message">The English text. A caller that translates uses <see cref="NameClaimer.Describe"/> with the kind and problem.</param>
/// <param name="Problem">For <see cref="ClaimKind.Invalid"/>: which rule the label or parent broke.</param>
/// <param name="Reason">For <see cref="LabelProblem.ParentBlocked"/>: why the parent is refused.</param>
internal sealed record NameClaim(ClaimKind Kind, string Message, string? Name = null, DynuDomain? Domain = null, IReadOnlyList<string>? Alternatives = null, int Status = 0, string? DynuType = null, LabelProblem Problem = LabelProblem.None, string? Reason = null);

/// <summary>
/// Checks and claims a hostname under one of the offered parents. It gives a result, never an exception (except for a
/// cancel), so a Dynu problem can't take setup down with it.
/// </summary>
internal sealed class NameClaimer
{
    private readonly DynuClient _dynu;
    private readonly ParentDomains _parents;
    private readonly ILogger _logger;
    private readonly Func<CancellationToken, Task<IReadOnlyList<DynuDomain>>> _listForCheck;

    /// <param name="listForCheck">How a check reads the account's names (setup caches it). A claim always asks Dynu itself.</param>
    internal NameClaimer(DynuClient dynu, ParentDomains parents, ILogger logger, Func<CancellationToken, Task<IReadOnlyList<DynuDomain>>>? listForCheck = null)
    {
        _dynu = dynu;
        _parents = parents;
        _logger = logger;
        _listForCheck = listForCheck ?? dynu.ListDomainsAsync;
    }

    /// <summary>The string-table key for what a kind says, e.g. <c>name.kind.accountFull</c>.</summary>
    internal static string KeyFor(ClaimKind kind) => "name.kind." + char.ToLowerInvariant(kind.ToString()[0]) + kind.ToString()[1..];

    /// <summary>The words for an answer in one language. An invalid label or parent says which rule it broke.</summary>
    internal static string Describe(Strings strings, ClaimKind kind, LabelProblem problem, string? reason, string? language)
        => kind == ClaimKind.Invalid && problem != LabelProblem.None
            ? HostLabel.Describe(strings, problem, reason, language)
            : strings.Get(KeyFor(kind), language);

    /// <summary>The Invalid answer when the label or parent can't be used, or null when both can. Nothing goes to Dynu either way.</summary>
    internal static NameClaim? Validate(string? label, string? parent, ParentDomains parents)
    {
        var built = HostLabel.Build(label, parent, parents);
        return built.Ok ? null : Invalid(built);
    }

    /// <summary>
    /// Looks without creating: Ours when the account has it, otherwise Unchecked. Dynu has no way to ask whether a
    /// name is free; the claim is the test.
    /// </summary>
    internal async Task<NameClaim> CheckAsync(string? label, string? parent, CancellationToken ct)
    {
        var built = HostLabel.Build(label, parent, _parents);
        if (!built.Ok)
        {
            return Invalid(built);
        }

        var name = built.Name!;
        var parentName = ParentDomains.Normalize(parent);
        try
        {
            var ours = (await _listForCheck(ct).ConfigureAwait(false)).FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (ours is not null)
            {
                return Done(ClaimKind.Ours, name, parentName, domain: ours);
            }

            // No getroot: live, it answered 501 for every name not on this account, www.freeddns.org (held elsewhere)
            // included, so it can't tell taken from free. Only the create says, with 505 for taken.
            return Done(ClaimKind.Unchecked, name, parentName);
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
            return Invalid(built);
        }

        var name = built.Name!;
        var parentName = ParentDomains.Normalize(parent);
        try
        {
            var result = await _dynu.CreateDomainAsync(name, ct).ConfigureAwait(false);
            return result.Created
                ? Done(ClaimKind.Claimed, name, parentName, domain: result.Domain)
                : Done(ClaimKind.Ours, name, parentName, domain: result.Domain);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Failed(e, parentName, name, HostLabel.Check(label).Label);
        }
    }

    private IReadOnlyList<string> Alternatives(string label, string parent)
        => _parents.Others(parent).Select(p => label + "." + p.Name).ToList();

    private static NameClaim Invalid(BuiltName built)
        => new(ClaimKind.Invalid, built.Message ?? UiText.English(KeyFor(ClaimKind.Invalid)), Problem: built.Problem, Reason: built.Reason);

    private NameClaim Done(ClaimKind kind, string name, string parent, DynuDomain? domain = null)
    {
        // The kind and the parent only: the name is the user's own public address, and nothing here needs it in a log.
        _logger.LogInformation("Trailhead name check under {Parent}: {Kind}.", parent, kind);
        return new NameClaim(kind, UiText.English(KeyFor(kind)), name, domain);
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
        if (error is DynuException d)
        {
            status = d.Status;
            type = d.ErrorType;
            if (d.AuthRejected)
            {
                kind = ClaimKind.BadKey;
            }
            else if (d.Kind == FailureKind.Transient)
            {
                kind = ClaimKind.Unavailable;
            }
            else if (d.FromEnvelope && d.Status == 503)
            {
                kind = ClaimKind.AccountFull;
            }
            else if (d.Status == 505)
            {
                kind = ClaimKind.Taken;
            }
            else if (d.Status == 501)
            {
                kind = ClaimKind.DynuRefused;
            }
            else
            {
                kind = ClaimKind.Unavailable;
            }
        }
        else
        {
            // Not from Dynu's side of the wire; the type is all the log gets, since a message could carry anything.
            kind = ClaimKind.Unavailable;
            _logger.LogWarning("Trailhead name check failed with {ErrorType}.", error.GetType().Name);
        }

        _logger.LogInformation("Trailhead name check under {Parent}: {Kind} (Dynu status {Status}).", parent, kind, status);
        var alternatives = kind == ClaimKind.Taken && label is not null ? Alternatives(label, parent) : null;
        return new NameClaim(kind, UiText.English(KeyFor(kind)), name, null, alternatives, status, type);
    }
}
