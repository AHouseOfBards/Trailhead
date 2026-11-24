using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Jellyfin.Plugin.Trailhead.Router;

/// <summary>
/// The four SOAP actions Trailhead uses on a router's WANIPConnection / WANPPPConnection service. Ported from RASA's
/// <c>internal/portmap</c>, with its live-learned facts: a fault arrives as HTTP 500 with the detail in the body, so
/// the body is read before the status; and a read whose response element isn't there is "absent", never a
/// zero-valued mapping (which RASA once read as "exists, permanent, disabled").
/// </summary>
internal sealed class IgdClient
{
    internal const string Description = "Trailhead (Jellyfin)";

    // A router answers in milliseconds or not at all; a hung one must not hold the router lock for long.
    internal static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(8);

    private const int MaxBody = 256 * 1024;

    private readonly HttpClient _http;
    private readonly IgdService _service;

    internal IgdClient(HttpClient http, IgdService service)
    {
        _http = http;
        _service = service;
    }

    internal IgdService Service => _service;

    /// <summary>The router's own idea of its WAN address. Null when it has none yet (0.0.0.0) or gives garbage.</summary>
    internal async Task<IPAddress?> GetExternalAddressAsync(CancellationToken ct)
    {
        var body = await CallAsync("GetExternalIPAddress", Array.Empty<(string, string)>(), ct).ConfigureAwait(false);
        var text = Field(body, "NewExternalIPAddress");

        // A router without a WAN lease reports 0.0.0.0, which would read as a mismatch in the CGNAT comparison.
        return IPAddress.TryParse(text, out var address) && !address.Equals(IPAddress.Any) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? address
            : null;
    }

    internal Task AddAsync(int external, string protocol, int internalPort, string client, int leaseSeconds, CancellationToken ct)
        => CallAsync(
            "AddPortMapping",
            new[]
            {
                ("NewRemoteHost", string.Empty),
                ("NewExternalPort", external.ToString(CultureInfo.InvariantCulture)),
                ("NewProtocol", protocol),
                ("NewInternalPort", internalPort.ToString(CultureInfo.InvariantCulture)),
                ("NewInternalClient", client),
                ("NewEnabled", "1"),
                ("NewPortMappingDescription", Description),
                ("NewLeaseDuration", leaseSeconds.ToString(CultureInfo.InvariantCulture)),
            },
            ct);

    /// <summary>The mapping on that port, or null when the router says there is none (714) or answers without one.</summary>
    internal async Task<PortMapping?> GetAsync(int external, string protocol, CancellationToken ct)
    {
        XDocument body;
        try
        {
            body = await CallAsync("GetSpecificPortMappingEntry", Key(external, protocol), ct).ConfigureAwait(false);
        }
        catch (UpnpException e) when (e.Code == UpnpCodes.NoSuchEntry)
        {
            return null;
        }

        var client = Field(body, "NewInternalClient");
        if (string.IsNullOrWhiteSpace(client))
        {
            return null;
        }

        var enabled = Field(body, "NewEnabled");
        return new PortMapping(
            external,
            protocol,
            Number(Field(body, "NewInternalPort")),
            client.Trim(),
            Field(body, "NewPortMappingDescription")?.Trim() ?? string.Empty,
            Number(Field(body, "NewLeaseDuration")),
            enabled is "1" || string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Deletes a mapping. One that isn't there (714) counts as deleted, so cleanup can be repeated.</summary>
    internal async Task DeleteAsync(int external, string protocol, CancellationToken ct)
    {
        try
        {
            await CallAsync("DeletePortMapping", Key(external, protocol), ct).ConfigureAwait(false);
        }
        catch (UpnpException e) when (e.Code == UpnpCodes.NoSuchEntry)
        {
        }
    }

    private static (string, string)[] Key(int external, string protocol) => new[]
    {
        ("NewRemoteHost", string.Empty),
        ("NewExternalPort", external.ToString(CultureInfo.InvariantCulture)),
        ("NewProtocol", protocol),
    };

    private async Task<XDocument> CallAsync(string action, IReadOnlyList<(string Name, string Value)> args, CancellationToken ct)
    {
        XNamespace s = "http://schemas.xmlsoap.org/soap/envelope/";
        XNamespace u = _service.ServiceType;
        var envelope = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(
                s + "Envelope",
                new XAttribute(XNamespace.Xmlns + "s", s),
                new XAttribute(s + "encodingStyle", "http://schemas.xmlsoap.org/soap/encoding/"),
                new XElement(
                    s + "Body",
                    new XElement(u + action, new XAttribute(XNamespace.Xmlns + "u", u), args.Select(a => new XElement(a.Name, a.Value))))));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CallTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, _service.ControlUrl)
        {
            Content = new StringContent(envelope.Declaration + envelope.ToString(SaveOptions.DisableFormatting), Encoding.UTF8),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("text/xml") { CharSet = "utf-8" };
        request.Headers.TryAddWithoutValidation("SOAPAction", "\"" + _service.ServiceType + "#" + action + "\"");

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        var raw = await ReadCappedAsync(response.Content, timeout.Token).ConfigureAwait(false);

        // The fault is in the body of an HTTP 500, so the body is read before the status is judged.
        var doc = Parse(raw);
        if (doc is not null && Field(doc, "errorCode") is { } codeText && int.TryParse(codeText, NumberStyles.None, CultureInfo.InvariantCulture, out var code))
        {
            throw new UpnpException(action, code, Field(doc, "errorDescription"));
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException("The router answered " + action + " with HTTP " + (int)response.StatusCode + ".", null, response.StatusCode);
        }

        return doc ?? throw new FormatException("The router's answer to " + action + " wasn't XML.");
    }

    internal static async Task<byte[]> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBody)
            {
                throw new InvalidDataException("The router's answer is too large.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>Parses router XML with DTDs off and no resolver: a device on the LAN is not trusted to be a router.</summary>
    internal static XDocument? Parse(byte[] raw)
    {
        if (raw.Length == 0)
        {
            return null;
        }

        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxBody * 2L };
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(raw), settings);
            return XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    // By local name anywhere in the document: routers disagree on whether the response's children carry a namespace.
    private static string? Field(XDocument doc, string name)
        => doc.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value;

    private static int Number(string? text)
        => int.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
}
