using Campground.Shared.Security;

namespace Jellyfin.Plugin.Trailhead.Dns;

/// <summary>
/// The Dynu API key at rest. It controls the server's DNS and so its certificate, so config holds it sealed (a
/// <c>cg1:</c> string) and the plain text exists only in memory, for as long as one client lives.
/// </summary>
internal sealed class DynuKey
{
    /// <summary>What to show when the saved value can't be used. Not a failure: the owner types the key again.</summary>
    internal const string ReenterMessage = "Re-enter your Dynu key.";

    // Binds the sealed value to this setting, so a sealed value copied from another place won't open here.
    private const string AssociatedData = "trailhead:dynu";

    private readonly SecretBox _box;

    internal DynuKey(SecretBox box) => _box = box;

    internal string Seal(string plainKey)
    {
        if (string.IsNullOrWhiteSpace(plainKey))
        {
            throw new ArgumentException("The Dynu key is empty.", nameof(plainKey));
        }

        return _box.Seal(plainKey.Trim(), AssociatedData);
    }

    /// <summary>
    /// Opens the saved value. False (and no key) when it is empty, isn't sealed, or won't open: a config restored on
    /// another machine, or a rotated key file. A plain-text value is refused too; it would mean something wrote the key unsealed.
    /// </summary>
    internal bool TryOpen(string? saved, out string? key)
    {
        key = string.IsNullOrWhiteSpace(saved) ? null : _box.TryOpen(saved, AssociatedData);
        if (string.IsNullOrWhiteSpace(key))
        {
            key = null;
            return false;
        }

        return true;
    }
}
