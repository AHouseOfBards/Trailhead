using System.Text;
using Campground.Shared.Security;
using Certes;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailhead.Acme;

/// <summary>
/// The ACME account key (ES256), sealed with SecretBox. The key is the account: losing it only costs a new
/// account, so a file that won't open is replaced, not treated as a failure.
/// </summary>
internal sealed class AccountStore
{
    private const string AssociatedData = "trailhead:acme-account";

    private readonly string _path;
    private readonly SecretBox _box;
    private readonly ILogger _logger;
    private readonly object _gate = new();

    internal AccountStore(string path, SecretBox box, ILogger logger)
    {
        _path = path;
        _box = box;
        _logger = logger;
    }

    internal IKey LoadOrCreate()
    {
        lock (_gate)
        {
            var existing = TryLoad(out var present);
            if (existing is not null)
            {
                return existing;
            }

            if (present)
            {
                _logger.LogWarning("Trailhead's ACME account key could not be opened (sealed by another key, or damaged). Starting a new account.");
            }

            var key = KeyFactory.NewKey(KeyAlgorithm.ES256);
            try
            {
                PrivateWrite.Replace(_path, Encoding.UTF8.GetBytes(_box.Seal(key.ToPem(), AssociatedData)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Trailhead could not save its ACME account key ({Error}); a new account will be made next time.", e.GetType().Name);
            }

            return key;
        }
    }

    private IKey? TryLoad(out bool present)
    {
        present = false;
        string text;
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            present = true;
            text = File.ReadAllText(_path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var pem = _box.TryOpen(text.Trim(), AssociatedData);
        if (pem is null)
        {
            return null;
        }

        try
        {
            return KeyFactory.FromPem(pem);
        }
        catch (Exception)
        {
            // Sealed correctly but not a key: nothing to keep.
            return null;
        }
    }
}
