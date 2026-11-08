using System;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.Globe.Data;

/// <summary>
/// The data folder can be any folder the admin names, so Globe only ever deletes what it made there: the folders
/// "globes" and "store" and its marker file, never the folder itself and nothing else. A folder is taken only when it is
/// empty or already Globe's, and it carries the id of the server that uses it, so two servers never share one.
/// </summary>
public static class DataFolder
{
    public const string Marker = ".globe-data";

    private static readonly string[] Own = { "globes", "store" };

    /// <summary>Why <paramref name="path"/> cannot be the data folder, or null when it can.</summary>
    public static string? Problem(string path, string serverId, string? other)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "The data folder is not a valid path.";
        }

        if (!string.IsNullOrWhiteSpace(other) && Nested(full, Path.GetFullPath(other)))
        {
            return "The new data folder must not lie inside the old one or hold it.";
        }

        if (!Directory.Exists(full))
        {
            return null;
        }

        // Only the marker makes a folder Globe's: folders that merely carry the same names could be someone else's.
        var marker = Path.Combine(full, Marker);
        if (File.Exists(marker))
        {
            return File.ReadAllText(marker).Trim() == serverId ? null : "This data folder belongs to another server.";
        }

        return Directory.EnumerateFileSystemEntries(full).Any() ? "The data folder must be empty or already hold Globe's data." : null;
    }

    public static void Mark(string path, string serverId)
    {
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, Marker), serverId);
    }

    /// <summary>Deletes Globe's own data in a folder this server marked; anything else in it stays.</summary>
    public static bool DeleteOwn(string path, string serverId)
    {
        var marker = Path.Combine(path, Marker);
        if (!File.Exists(marker) || File.ReadAllText(marker).Trim() != serverId)
        {
            return false;
        }

        foreach (var name in Own)
        {
            var folder = Path.Combine(path, name);
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }
        }

        File.Delete(marker);
        return true;
    }

    private static bool Nested(string a, string b)
    {
        a = a.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        b = b.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
    }
}
