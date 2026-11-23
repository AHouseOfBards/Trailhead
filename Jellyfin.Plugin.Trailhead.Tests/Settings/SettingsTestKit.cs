using System.Collections.Concurrent;
using System.Net;
using System.Xml;
using System.Xml.Serialization;
using Jellyfin.Plugin.Trailhead.Settings;
using Jellyfin.Plugin.Trailhead.Tests.Acme;
using Jellyfin.Plugin.Trailhead.Tests.Renewal;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace Jellyfin.Plugin.Trailhead.Tests.Settings;

/// <summary>
/// Jellyfin's own XML serializer, line for line (Emby.Server.Implementations/Serialization/MyXmlSerializer.cs, v12.4).
/// The real class isn't in a package a plugin can reference, so the file a test writes is the file Jellyfin writes.
/// </summary>
internal sealed class JellyfinXml : IXmlSerializer
{
    private readonly ConcurrentDictionary<string, XmlSerializer> _serializers = new();

    private XmlSerializer GetSerializer(Type type)
        => _serializers.GetOrAdd(type.FullName!, static (_, t) => new XmlSerializer(t), type);

    public object? DeserializeFromStream(Type type, Stream stream)
    {
        using var reader = XmlReader.Create(stream);
        return GetSerializer(type).Deserialize(reader);
    }

    public void SerializeToStream(object obj, Stream stream)
    {
        using var writer = new StreamWriter(stream, null, 1024, true);
        using var textWriter = new XmlTextWriter(writer);
        textWriter.Formatting = Formatting.Indented;
        GetSerializer(obj.GetType()).Serialize(textWriter, obj);
    }

    public void SerializeToFile(object obj, string file)
    {
        using var stream = new FileStream(file, FileMode.Create, FileAccess.Write);
        SerializeToStream(obj, stream);
    }

    public object? DeserializeFromFile(Type type, string file)
    {
        using var stream = File.OpenRead(file);
        return DeserializeFromStream(type, stream);
    }

    public object? DeserializeFromBytes(Type type, byte[] buffer)
    {
        using var stream = new MemoryStream(buffer, 0, buffer.Length, false, true);
        return DeserializeFromStream(type, stream);
    }
}

/// <summary>
/// The named-configuration half of Jellyfin's <c>BaseConfigurationManager</c> for the "network" key, as it is at v12.4
/// (Emby.Server.Implementations/AppBase/BaseConfigurationManager.cs:271-356): a cache filled from
/// <c>&lt;config&gt;/network.xml</c> on first ask, and a save that swaps the cache and writes the file. A test can
/// make the save throw, or make the disk hold something other than what was saved.
/// </summary>
internal sealed class FakeJellyfinConfig : IConfigurationManager
{
    private readonly ConcurrentDictionary<string, object> _configurations = new();
    private readonly JellyfinXml _xml = new();

    internal FakeJellyfinConfig(string root)
    {
        CommonApplicationPaths = RenewalTestKit.Paths(root);
        Directory.CreateDirectory(CommonApplicationPaths.ConfigurationDirectoryPath);
    }

    public event EventHandler<ConfigurationUpdateEventArgs>? NamedConfigurationUpdating;

    public event EventHandler<EventArgs>? ConfigurationUpdated;

    public event EventHandler<ConfigurationUpdateEventArgs>? NamedConfigurationUpdated;

    public IApplicationPaths CommonApplicationPaths { get; }

    public BaseApplicationConfiguration CommonConfiguration => throw new NotSupportedException();

    internal string NetworkFile => Path.Combine(CommonApplicationPaths.ConfigurationDirectoryPath, "network.xml");

    internal int Saves { get; private set; }

    /// <summary>Thrown by the next save, before anything is written.</summary>
    internal Exception? FailNextSave { get; set; }

    /// <summary>Thrown by the next save after the file is written: a crash halfway through, as far as the caller can tell.</summary>
    internal Exception? FailNextSaveAfterWrite { get; set; }

    /// <summary>Changes what lands on disk (a disk or another writer that disagrees). Memory still gets the saved object.</summary>
    internal Action<NetworkConfiguration>? OnDisk { get; set; }

    internal void WriteFile(NetworkConfiguration config) => _xml.SerializeToFile(config, NetworkFile);

    internal NetworkConfiguration ReadFile() => (NetworkConfiguration)_xml.DeserializeFromFile(typeof(NetworkConfiguration), NetworkFile)!;

    /// <summary>Drops the cache, as a Jellyfin restart would.</summary>
    internal void Restart() => _configurations.Clear();

    public object GetConfiguration(string key)
        => _configurations.GetOrAdd(key, k =>
        {
            if (k != NetworkConfigurationStore.StoreKey)
            {
                throw new NotSupportedException(k);
            }

            return File.Exists(NetworkFile) ? ReadFile() : new NetworkConfiguration();
        });

    public void SaveConfiguration(string key, object configuration)
    {
        if (key != NetworkConfigurationStore.StoreKey || configuration is not NetworkConfiguration network)
        {
            throw new ArgumentException("Expected configuration type is NetworkConfiguration");
        }

        if (FailNextSave is { } e)
        {
            FailNextSave = null;
            throw e;
        }

        Saves++;
        NamedConfigurationUpdating?.Invoke(this, new ConfigurationUpdateEventArgs(key, configuration));
        _configurations.AddOrUpdate(key, configuration, (_, _) => configuration);
        if (OnDisk is { } tamper)
        {
            using var stream = new MemoryStream();
            _xml.SerializeToStream(network, stream);
            var onDisk = (NetworkConfiguration)_xml.DeserializeFromBytes(typeof(NetworkConfiguration), stream.ToArray())!;
            tamper(onDisk);
            _xml.SerializeToFile(onDisk, NetworkFile);
        }
        else
        {
            _xml.SerializeToFile(configuration, NetworkFile);
        }

        if (FailNextSaveAfterWrite is { } late)
        {
            FailNextSaveAfterWrite = null;
            throw late;
        }

        NamedConfigurationUpdated?.Invoke(this, new ConfigurationUpdateEventArgs(key, configuration));
    }

    public void SaveConfiguration() => ConfigurationUpdated?.Invoke(this, EventArgs.Empty);

    public void ReplaceConfiguration(BaseApplicationConfiguration newConfiguration) => throw new NotSupportedException();

    public void RegisterConfiguration<T>()
        where T : IConfigurationFactory => throw new NotSupportedException();

    public ConfigurationStore[] GetConfigurationStores() => new ConfigurationStore[] { new NetworkConfigurationStore() };

    public Type GetConfigurationType(string key) => typeof(NetworkConfiguration);

    public void AddParts(IEnumerable<IConfigurationFactory> factories) => throw new NotSupportedException();
}

/// <summary>Everything a JellyfinNetwork test needs, in one temp folder.</summary>
internal sealed class NetworkRig : IDisposable
{
    private readonly TempFolder _folder = new();

    internal NetworkRig(NetworkConfiguration? start = null, ForwardedHeadersOptions? live = null)
    {
        Config = new FakeJellyfinConfig(_folder.File("jellyfin"));
        if (start is not null)
        {
            Config.WriteFile(start);
        }

        Live = live ?? LiveOptionsAsJellyfinBuildsThem(start ?? new NetworkConfiguration());
        Journal = new Journal(() => _folder.File("journal.json"), TimeProvider.System, Logger);
        Network = new JellyfinNetwork(Config, new JellyfinXml(), Journal, Options.Create(Live), Logger);
    }

    internal FakeJellyfinConfig Config { get; }

    internal Journal Journal { get; }

    internal JellyfinNetwork Network { get; }

    internal ForwardedHeadersOptions Live { get; }

    internal ListLogger Logger { get; } = new();

    internal string Folder => _folder.Path;

    internal static NetworkPlan Plan(string address = "127.0.0.1", int port = 443, string? subnet = null)
        => new(IPAddress.Parse(address), "trailhead-test.example.com", port, subnet);

    /// <summary>
    /// The forwarded-headers options Jellyfin builds at start from Known proxies, by the steps of
    /// <c>ConfigureForwardHeaders</c> and <c>AddIPAddress</c> (Jellyfin.Server/Extensions/ApiServiceCollectionExtensions.cs:168-187
    /// and 290-310, v12.4) for plain addresses. That code is internal to Jellyfin.Server.
    /// </summary>
    internal static ForwardedHeadersOptions LiveOptionsAsJellyfinBuildsThem(NetworkConfiguration config)
    {
        var options = new ForwardedHeadersOptions();
        if (config.KnownProxies.Length == 0)
        {
            options.ForwardedHeaders = ForwardedHeaders.None;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
            return options;
        }

        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
        foreach (var p in config.KnownProxies)
        {
            if (IPAddress.TryParse(p, out var a)
                && !((!config.EnableIPv4 && a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                     || (!config.EnableIPv6 && a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)))
            {
                options.KnownProxies.Add(a);
            }
        }

        return options;
    }

    public void Dispose() => _folder.Dispose();
}
