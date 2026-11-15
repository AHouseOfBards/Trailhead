using System.Reflection;
using System.Xml.Serialization;
using Campground.Shared.Security;
using Jellyfin.Plugin.Trailhead.Acme;
using Jellyfin.Plugin.Trailhead.Dns;
using Jellyfin.Plugin.Trailhead.Tests.Acme;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailhead.Tests.Renewal;

/// <summary>An <see cref="ILogger{T}"/> that writes into a shared <see cref="ListLogger"/>.</summary>
internal sealed class TypedLogger<T> : ILogger<T>
{
    private readonly ILogger _inner;

    internal TypedLogger(ILogger inner) => _inner = inner;

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => _inner.Log(logLevel, eventId, state, exception, formatter);
}

/// <summary>Settings a test can change between runs, as the admin would from the dashboard.</summary>
internal sealed class MutableSettings
{
    internal ServiceSettings Value { get; set; } = new(string.Empty, string.Empty);

    internal Func<ServiceSettings> Read => () => Value;
}

/// <summary>A CA that does what the test says: counts the calls, and answers when released.</summary>
internal sealed class GatedIssuer : ICertificateIssuer
{
    private int _calls;

    internal TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Exception Failure { get; set; } = new InvalidOperationException("the CA said no");

    internal int Calls => Volatile.Read(ref _calls);

    public TimeSpan? LastRetryAfter => null;

    public async Task<IssueResult> IssueAsync(IssueRequest request, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        Started.TrySetResult();
        await Gate.Task.WaitAsync(ct).ConfigureAwait(false);
        throw Failure;
    }

    public Task<RenewalInfo?> GetRenewalInfoAsync(string chainPem, CancellationToken ct) => Task.FromResult<RenewalInfo?>(null);
}

/// <summary>A manager that answers with what the test hands it.</summary>
internal sealed class CannedRenewer : ICertificateRenewer
{
    private readonly Func<CancellationToken, Task<ReconcileResult>> _answer;

    internal CannedRenewer(Func<CancellationToken, Task<ReconcileResult>> answer) => _answer = answer;

    internal CannedRenewer(ReconcileResult result)
        : this(_ => Task.FromResult(result))
    {
    }

    public Task<ReconcileResult> ReconcileAsync(bool force, CancellationToken ct) => _answer(ct);
}

internal sealed class RecordingProgress : IProgress<double>
{
    internal List<double> Values { get; } = new();

    public void Report(double value) => Values.Add(value);
}

internal static class RenewalTestKit
{
    internal static SecretBox Box(byte seed) => new(Enumerable.Repeat(seed, 32).ToArray());

    internal static string Sealed(byte seed, string plain) => new DynuKey(Box(seed)).Seal(plain);

    internal static CertificateServices Services(ILogger logger, MutableSettings settings, ServicesOverrides overrides)
        => new(logger, settings.Read, overrides);

    /// <summary>An <see cref="IApplicationPaths"/> that answers every path with a folder under one root. The real one has no small implementation.</summary>
    internal static IApplicationPaths Paths(string root)
    {
        var proxy = System.Reflection.DispatchProxy.Create<IApplicationPaths, PathsProxy>();
        ((PathsProxy)(object)proxy).Root = root;
        return proxy;
    }

    /// <summary>Jellyfin's <c>Plugin.Instance</c> is process-wide, so tests that make a plugin put it back.</summary>
    internal static void ForgetPlugin() => typeof(Plugin).GetProperty(nameof(Plugin.Instance))!.SetValue(null, null);

    internal class PathsProxy : DispatchProxy
    {
        internal string Root { get; set; } = string.Empty;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var name = targetMethod!.Name;
            if (targetMethod.ReturnType == typeof(string) && name.StartsWith("get_", StringComparison.Ordinal))
            {
                return Path.Combine(Root, name[4..]);
            }

            return targetMethod.ReturnType.IsValueType && targetMethod.ReturnType != typeof(void) ? Activator.CreateInstance(targetMethod.ReturnType) : null;
        }
    }

    /// <summary>Saves and loads with the framework's own XML serializer; the plugin's configuration is a plain class.</summary>
    internal sealed class XmlStub : IXmlSerializer
    {
        public object DeserializeFromStream(Type type, Stream stream) => new XmlSerializer(type).Deserialize(stream)!;

        public void SerializeToStream(object obj, Stream stream) => new XmlSerializer(obj.GetType()).Serialize(stream, obj);

        public void SerializeToFile(object obj, string file)
        {
            using var stream = File.Create(file);
            SerializeToStream(obj, stream);
        }

        public object DeserializeFromFile(Type type, string file)
        {
            using var stream = File.OpenRead(file);
            return DeserializeFromStream(type, stream);
        }

        public object DeserializeFromBytes(Type type, byte[] buffer) => DeserializeFromStream(type, new MemoryStream(buffer));
    }
}
