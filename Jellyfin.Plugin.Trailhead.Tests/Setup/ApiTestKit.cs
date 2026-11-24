using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Campground.Shared.Security;
using Campground.Shared.Storage;
using Jellyfin.Plugin.Trailhead.Configuration;
using Jellyfin.Plugin.Trailhead.Dns;
using Jellyfin.Plugin.Trailhead.TestSupport;
using Jellyfin.Plugin.Trailhead.Tests.Acme;
using Jellyfin.Plugin.Trailhead.Tests.Renewal;
using MediaBrowser.Common.Api;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Setup;

/// <summary>Writes into one shared <see cref="ListLogger"/>, so a test can search every log line the host and the plugin made.</summary>
internal sealed class ListLoggerProvider : ILoggerProvider
{
    private readonly ILogger _logger;

    internal ListLoggerProvider(ILogger logger) => _logger = logger;

    public ILogger CreateLogger(string categoryName) => _logger;

    public void Dispose()
    {
    }
}

/// <summary>
/// Gives the same claims Jellyfin's CustomAuthenticationHandler gives (Jellyfin.Api/Auth/CustomAuthenticationHandler.cs,
/// v12.0): the Administrator role for an admin or an API key, User otherwise, and no result without a token. The test
/// header stands in for the token.
/// </summary>
internal sealed class HeaderAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    internal const string Scheme = "CustomAuthentication";
    internal const string RoleHeader = "X-Test-Role";

    public HeaderAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var role = Request.Headers[RoleHeader].FirstOrDefault();
        if (string.IsNullOrEmpty(role))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "tester"),
            new Claim(ClaimTypes.Role, role == "admin" ? "Administrator" : "User"),
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme)));
    }
}

/// <summary>The plugin's controllers in a real Kestrel host, with Jellyfin's auth shape and the exception middleware's logging.</summary>
internal sealed class ApiHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private ApiHost(WebApplication app, Uri baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
        Client = new HttpClient { BaseAddress = baseUrl, Timeout = TimeSpan.FromSeconds(60) };
    }

    internal Uri BaseUrl { get; }

    internal HttpClient Client { get; }

    internal IServiceProvider Services => _app.Services;

    internal static async Task<ApiHost> StartAsync(SetupService setup, ILogger log)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new ListLoggerProvider(log));
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(setup);
        builder.Services.AddSingleton(new Jellyfin.Plugin.Trailhead.Edge.EdgeIdentity());
        builder.Services.AddControllers()
            .AddApplicationPart(typeof(Plugin).Assembly)
            .AddControllersAsServices()
            .AddJsonOptions(o => o.JsonSerializerOptions.PropertyNamingPolicy = null);
        builder.Services.AddAuthentication(HeaderAuthHandler.Scheme).AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>(HeaderAuthHandler.Scheme, _ => { });

        // From Jellyfin.Server/Extensions/ApiServiceCollectionExtensions.cs (v12.0), lines 63-89: the elevation policy
        // is the custom scheme plus the Administrator role claim. Nothing else is copied.
        builder.Services.AddAuthorization(options =>
        {
            options.DefaultPolicy = new AuthorizationPolicyBuilder().AddAuthenticationSchemes(HeaderAuthHandler.Scheme).RequireAuthenticatedUser().Build();
            options.AddPolicy(
                Policies.RequiresElevation,
                policy => policy.AddAuthenticationSchemes(HeaderAuthHandler.Scheme).RequireClaim(ClaimTypes.Role, "Administrator"));
        });

        var app = builder.Build();

        // Jellyfin's ExceptionMiddleware logs an escaped exception with its message, and the URL (v12.0,
        // Jellyfin.Api/Middleware/ExceptionMiddleware.cs lines 77-79). This does the same, so a leak shows up in the log.
        app.Use(async (ctx, next) =>
        {
            try
            {
                await next().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                log.LogError(e, "Error processing request. URL {Method} {Url}.", ctx.Request.Method, ctx.Request.Path);
                ctx.Response.StatusCode = 500;
            }
        });
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync().ConfigureAwait(false);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new ApiHost(app, new Uri(address.TrimEnd('/') + "/"));
    }

    /// <summary>Every route the plugin's controllers answer, as method and lower-case path.</summary>
    internal IReadOnlyList<(string Method, string Path)> Routes()
    {
        var source = Services.GetRequiredService<EndpointDataSource>();
        var found = new List<(string, string)>();
        foreach (var endpoint in source.Endpoints.OfType<RouteEndpoint>())
        {
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? Array.Empty<string>();
            foreach (var m in methods)
            {
                found.Add((m, "/" + endpoint.RoutePattern.RawText!.Trim('/').ToLowerInvariant()));
            }
        }

        return found;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>An XML serializer that can be told to lie, so a test can prove a write is read back.</summary>
public sealed class FlakyXml : IXmlSerializer
{
    private readonly RenewalTestKit.XmlStub _inner = new();

    public enum Mode
    {
        Normal,

        /// <summary>Saves an empty configuration: the file says something other than what was written.</summary>
        WritesBlank,

        /// <summary>The save works but the read-back fails.</summary>
        ReadFails,

        /// <summary>The save itself fails.</summary>
        WriteFails,
    }

    public Mode Current { get; set; }

    public object DeserializeFromStream(Type type, Stream stream) => _inner.DeserializeFromStream(type, stream);

    public void SerializeToStream(object obj, Stream stream) => _inner.SerializeToStream(obj, stream);

    public void SerializeToFile(object obj, string file)
    {
        switch (Current)
        {
            case Mode.WritesBlank:
                _inner.SerializeToFile(new PluginConfiguration(), file);
                break;
            case Mode.WriteFails:
                throw new IOException("the disk is full");
            default:
                _inner.SerializeToFile(obj, file);
                break;
        }
    }

    public object DeserializeFromFile(Type type, string file)
        => Current == Mode.ReadFails ? throw new IOException("cannot read") : _inner.DeserializeFromFile(type, file);

    public object DeserializeFromBytes(Type type, byte[] buffer) => _inner.DeserializeFromBytes(type, buffer);
}

/// <summary>
/// A plugin, FakeDynu, a setup service and the controllers in a Kestrel host. Each test class derives from this and
/// changes what it needs. Plugin.Instance is process-wide, so every class that uses this is in the registry collection.
/// </summary>
public abstract class ApiTestBase : IAsyncLifetime
{
    /// <summary>The fake key. Tests look for it in every body, header, log line and saved file.</summary>
    internal const string Sentinel = FakeDynu.Key;

    private readonly List<IAsyncDisposable> _extra = new();

    internal TempFolder Folder { get; } = new();

    internal ListLogger Log { get; } = new();

    internal FakeDynu Dynu { get; private set; } = null!;

    internal FakeTimeProvider? Fake { get; private set; }

    internal FlakyXml Xml { get; } = new();

    internal Plugin ThePlugin { get; private set; } = null!;

    internal ApiHost Host { get; private set; } = null!;

    internal SetupService Setup { get; private set; } = null!;

    internal SecretBox Box { get; } = RenewalTestKit.Box(1);

    /// <summary>True to give the services a fake clock (cache and rate-limit windows). Dynu retries need real time, so most tests don't.</summary>
    internal virtual bool UseFakeTime => false;

    internal virtual TimeSpan? Budget => null;

    internal virtual DynuClientOptions DynuOptions => new() { MaxAttempts = 2, BackoffBase = TimeSpan.FromMilliseconds(1), RequestTimeout = TimeSpan.FromSeconds(10) };

    internal virtual CertificateServices? Certificates => null;

    internal virtual Campground.Shared.I18n.Strings? StringsOverride => null;

    public async Task InitializeAsync()
    {
        RenewalTestKit.ForgetPlugin();
        CampgroundPaths.OverrideForTests(Folder.File("campground"));
        ThePlugin = new Plugin(RenewalTestKit.Paths(Folder.File("jellyfin")), Xml);

        // The file is written on first access; tests read it.
        _ = ThePlugin.Configuration;
        Dynu = await FakeDynu.StartAsync().ConfigureAwait(false);
        Fake = UseFakeTime ? new FakeTimeProvider(new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero)) : null;
        var overrides = new ServicesOverrides
        {
            Box = Box,
            DynuBase = Dynu.BaseUrl,
            Time = Fake,
            DynuInteractive = DynuOptions,
            SetupBudget = Budget,
            Strings = StringsOverride,
        };
        Setup = MakeSetup(overrides, Certificates);
        Host = await ApiHost.StartAsync(Setup, Log).ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        await Host.DisposeAsync().ConfigureAwait(false);
        await Dynu.DisposeAsync().ConfigureAwait(false);
        foreach (var e in _extra)
        {
            await e.DisposeAsync().ConfigureAwait(false);
        }

        Setup.Dispose();
        RenewalTestKit.ForgetPlugin();
        Folder.Dispose();
    }

    internal virtual SetupService MakeSetup(ServicesOverrides overrides, CertificateServices? certificates)
        => new(Log, overrides, null, certificates);

    /// <summary>Saves the fake key sealed, as a finished key step would have.</summary>
    internal void SeedKey(string plain = Sentinel) => ConfigGate.Update(c => c.DynuKey = new DynuKey(Box).Seal(plain));

    internal static PluginConfiguration Config => ConfigGate.Read();

    internal string SavedXml => File.ReadAllText(ThePlugin.ConfigurationFilePath);

    internal Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? role = "admin", string? body = null, Action<HttpRequestMessage>? tweak = null)
    {
        var request = new HttpRequestMessage(method, path.TrimStart('/'));
        if (role is not null)
        {
            request.Headers.Add(HeaderAuthHandler.RoleHeader, role);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        tweak?.Invoke(request);
        return Host.Client.SendAsync(request);
    }

    internal Task<HttpResponseMessage> PostAsync(string path, object body, string? role = "admin")
        => SendAsync(HttpMethod.Post, path, role, body as string ?? JsonSerializer.Serialize(body));

    internal Task<HttpResponseMessage> GetAsync(string path, string? role = "admin") => SendAsync(HttpMethod.Get, path, role);

    internal static async Task<JsonDocument> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));

    internal static string Code(HttpResponseMessage response)
        => response.Headers.TryGetValues("X-Campground-Error", out var v) ? string.Join(",", v) : string.Empty;

    /// <summary>The whole response as text: status, every header, the body. For asserting that something is not in it.</summary>
    internal static async Task<string> DumpAsync(HttpResponseMessage response)
    {
        var text = new StringBuilder();
        text.Append((int)response.StatusCode).Append('\n');
        foreach (var h in response.Headers.Concat(response.Content.Headers))
        {
            text.Append(h.Key).Append(": ").Append(string.Join("; ", h.Value)).Append('\n');
        }

        text.Append(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        return text.ToString();
    }

    internal int DynuCount(string method, string path) => Dynu.Requests.Count(r => r.Method == method && r.Path == path);

    internal int DynuCalls => Dynu.Requests.Count;

    protected static async Task<bool> WaitUntilAsync(Func<bool> condition, int milliseconds = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < until)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        return condition();
    }

    protected static string DocsText()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "docs", "api.md");
        return File.ReadAllText(path);
    }
}
