using System.Text.Json;
using Campground.Shared.Storage;
using Certes;

namespace Jellyfin.Plugin.Trailhead.Acme;

internal enum FailureKind
{
    Transient,
    Permanent,
}

/// <summary>The CA marked an authorization invalid (the challenge failed). These are the failures Let's Encrypt rate-limits.</summary>
internal sealed class AuthorizationInvalidException : Exception
{
    internal AuthorizationInvalidException(string name, string? problemType, string? detail)
        : base($"Validation of {name} failed: {problemType} {detail}")
    {
        Name = name;
        ProblemType = problemType;
        Detail = detail;
    }

    internal string Name { get; }

    internal string? ProblemType { get; }

    internal string? Detail { get; }
}

/// <summary>
/// Failed validations per name, kept on disk. Let's Encrypt allows 5 per hostname per hour; we stop at 3, so a
/// retry loop or a restart loop can't burn the allowance and lock the name out for an hour.
/// </summary>
internal sealed class FailureBudget
{
    private static readonly string[] TransientTypes = { "rateLimited", "serverInternal", "badNonce", "connection", "dns", "timeout" };

    private readonly AtomicJsonFile _file;
    private readonly TimeProvider _time;
    private readonly int _limit;
    private readonly TimeSpan _window;
    private readonly object _gate = new();

    internal FailureBudget(string path, TimeProvider time, int limit = 3, TimeSpan? window = null)
    {
        _file = new AtomicJsonFile(path);
        _time = time;
        _limit = limit;
        _window = window ?? TimeSpan.FromHours(1);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    }

    /// <summary>
    /// Network errors, timeouts, 5xx and rateLimited are worth another try later; the CA saying no to the request
    /// itself (unauthorized, badCSR, invalidProfile, rejectedIdentifier, any other 4xx) is not, until something changes.
    /// An exception we don't recognise is treated as transient: we'd rather retry than give up for good.
    /// </summary>
    internal static FailureKind Classify(Exception error)
    {
        switch (error)
        {
            case AuthorizationInvalidException invalid:
                return invalid.ProblemType is not null && IsTransientType(invalid.ProblemType) ? FailureKind.Transient : FailureKind.Permanent;
            case AcmeRequestException { Error: { } acme }:
                if (acme.Type is not null && IsTransientType(acme.Type))
                {
                    return FailureKind.Transient;
                }

                return (int)acme.Status >= 500 || (int)acme.Status == 0 ? FailureKind.Transient : FailureKind.Permanent;
            default:
                return FailureKind.Transient;
        }
    }

    /// <summary>When the name may be tried again, or null if it may be now.</summary>
    internal DateTimeOffset? BlockedUntil(string name)
    {
        lock (_gate)
        {
            var recent = Recent(Load(), name);
            if (recent.Count < _limit)
            {
                return null;
            }

            // Failure number (count - limit) is the one whose ageing out brings us back under the limit.
            return recent[recent.Count - _limit] + _window;
        }
    }

    internal void RecordFailure(string name)
    {
        lock (_gate)
        {
            var data = Load();
            var key = Key(name);
            var recent = Recent(data, name);
            recent.Add(_time.GetUtcNow());
            data.Failures[key] = recent;
            foreach (var other in data.Failures.Keys.ToList())
            {
                if (other != key && Recent(data, other).Count == 0)
                {
                    data.Failures.Remove(other);
                }
            }

            try
            {
                _file.Save(data);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Fail open: a budget we can't write only means a restart forgets it.
            }
        }
    }

    private static string Key(string name) => name.Trim().TrimEnd('.').ToLowerInvariant();

    private static bool IsTransientType(string type) => TransientTypes.Any(t => type.EndsWith(":" + t, StringComparison.Ordinal) || type == t);

    private List<DateTimeOffset> Recent(BudgetData data, string name)
    {
        var cutoff = _time.GetUtcNow() - _window;
        return data.Failures.TryGetValue(Key(name), out var all) ? all.Where(t => t > cutoff).OrderBy(t => t).ToList() : new List<DateTimeOffset>();
    }

    private BudgetData Load()
    {
        try
        {
            return _file.Load<BudgetData>() ?? new BudgetData();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            // An unreadable budget counts as empty. Refusing to issue because our own bookkeeping broke would be worse.
            return new BudgetData();
        }
    }

    private sealed class BudgetData
    {
        public Dictionary<string, List<DateTimeOffset>> Failures { get; set; } = new();
    }
}
