using Campground.Shared.Storage;

namespace Jellyfin.Plugin.Trailhead.Acme;

internal static class PrivateWrite
{
    /// <summary>
    /// Writes the file private from its first byte, under a temporary name, then moves it over the target. A plain
    /// overwrite would leave a half-written key or certificate if the process died in the middle.
    /// </summary>
    internal static void Replace(string path, ReadOnlySpan<byte> data)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        PrivateFiles.WriteNew(temp, data);
        try
        {
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    internal static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
