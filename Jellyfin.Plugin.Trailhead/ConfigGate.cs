using System.Reflection;
using Jellyfin.Plugin.Trailhead.Configuration;

namespace Jellyfin.Plugin.Trailhead;

/// <summary>The saved configuration could not be written, or what was read back is not what was written.</summary>
internal sealed class ConfigSaveException : Exception
{
    // Property names and exception types only: a setting's value can be the sealed key.
    internal ConfigSaveException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// The one way Trailhead changes its configuration. Jellyfin swaps in a new configuration object whenever an admin
/// saves the plugin's settings, so a lock on the object lets two writers in at once and a reference kept from earlier
/// writes to an orphan (Bonfire, advice 1). This has one static lock, always reads <c>Plugin.Instance.Configuration</c>
/// afresh inside it, and saves inside it.
/// </summary>
internal static class ConfigGate
{
    private static readonly object Gate = new();

    /// <summary>Runs work under the same lock the changes take, so Jellyfin's own save can't land in the middle of one.</summary>
    internal static void Locked(Action work)
    {
        lock (Gate)
        {
            work();
        }
    }

    /// <summary>A copy of the settings now, taken under the lock. Empty settings when there is no plugin.</summary>
    internal static PluginConfiguration Read()
    {
        lock (Gate)
        {
            var copy = new PluginConfiguration();
            if (Plugin.Instance?.Configuration is { } current)
            {
                CopyValues(current, copy, includeOwned: true);
            }

            return copy;
        }
    }

    /// <summary>
    /// Changes the configuration, saves it, and reads it back from the saved file. It returns that copy, so a caller
    /// has the saved values and not what it hoped it wrote. A write that fails, or comes back different, throws
    /// <see cref="ConfigSaveException"/> and puts the old values back in memory.
    /// </summary>
    internal static PluginConfiguration Update(Action<PluginConfiguration> change)
    {
        lock (Gate)
        {
            var plugin = Plugin.Instance ?? throw new ConfigSaveException("Trailhead has no plugin instance to save to.");
            var inUse = plugin.Configuration;
            var before = new PluginConfiguration();
            CopyValues(inUse, before, includeOwned: true);

            change(inUse);
            try
            {
                plugin.SaveConfiguration();
                var saved = plugin.ReadSavedConfiguration();
                var differs = Differences(inUse, saved);
                if (differs.Count > 0)
                {
                    throw new ConfigSaveException("The saved settings differ from what was written (" + string.Join(", ", differs) + ").");
                }

                return saved;
            }
            catch (Exception e)
            {
                // Without this, memory would say the new key is in use while the disk still has the old one.
                CopyValues(before, inUse, includeOwned: true);
                throw e as ConfigSaveException ?? new ConfigSaveException("The settings could not be saved or read back (" + e.GetType().Name + ").");
            }
        }
    }

    /// <summary>Copies every setting from one configuration to another. Without <paramref name="includeOwned"/>, the Trailhead-owned ones stay as they are.</summary>
    internal static void CopyValues(PluginConfiguration from, PluginConfiguration to, bool includeOwned)
    {
        foreach (var p in Settings())
        {
            if (!includeOwned && p.GetCustomAttribute<TrailheadOwnedAttribute>() is not null)
            {
                continue;
            }

            p.SetValue(to, p.GetValue(from));
        }
    }

    private static List<string> Differences(PluginConfiguration a, PluginConfiguration b)
        => Settings().Where(p => !Equals(p.GetValue(a), p.GetValue(b))).Select(p => p.Name).ToList();

    private static IEnumerable<PropertyInfo> Settings()
        => typeof(PluginConfiguration).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0);
}
