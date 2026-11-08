using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace Jellyfin.Plugin.Globe.Data;

/// <summary>One game of the Playnite Bridge library, as GlobeLink.Games returns it: one string array per game, in this order.</summary>
public sealed record GameRow(
    Guid Id,
    string Name,
    string SortName,
    DateTime? Added,
    DateTime? Released,
    double? CommunityRating,
    string OfficialRating,
    int? RatingScore,
    int? RatingSubScore,
    Guid GroupId,
    bool IsMain,
    bool IsTwin);

/// <summary>
/// The games of Playnite Bridge. Its games are not in Jellyfin's database; the plugin offers the read-only class
/// Jellyfin.Plugin.PlayniteBridge.GlobeLink, found here by reflection over every load context, so Globe needs no
/// reference to it. Without the Bridge every answer is empty and Globe offers no games.
/// </summary>
public static class GamesLink
{
    private const string TypeName = "Jellyfin.Plugin.PlayniteBridge.GlobeLink";

    // The pictures a collection has of its own come from the class the Bridge made for ArtworkPlus.
    private const string ArtworkTypeName = "Jellyfin.Plugin.PlayniteBridge.ArtworkPlusLink";

    private static readonly object Gate = new();
    private static readonly Dictionary<string, MethodInfo> Methods = new();
    private static bool _resolved;
    private static (string Stamp, Guid Library, List<GameRow> Rows)? _cache;

    /// <summary>Whether Playnite Bridge with GlobeLink is installed.</summary>
    public static bool Present
    {
        get
        {
            Resolve();
            return Methods.Count > 0;
        }
    }

    /// <summary>Whether the Bridge answers yet: right after a server start it can still be empty. Its own Ready() says
    /// so, else a known games library does.</summary>
    public static bool Ready() => Present && (Methods.ContainsKey("Ready") ? Call("Ready") is true : LibraryId() is not null);

    /// <summary>The games library, or null without the Bridge or without such a library.</summary>
    public static Guid? LibraryId() =>
        Call("GamesLibraryId") is string id && Guid.TryParse(id, out var guid) && guid != Guid.Empty ? guid : null;

    /// <summary>Every game of the library; the list is kept until the Bridge's stamp changes, as reading it looks at
    /// every game folder.</summary>
    public static IReadOnlyList<GameRow> Games(Guid libraryId)
    {
        var stamp = Call("DataStamp") as string ?? string.Empty;
        lock (Gate)
        {
            if (_cache is { } cache && cache.Stamp == stamp && stamp.Length > 0 && cache.Library == libraryId)
            {
                return cache.Rows;
            }
        }

        var rows = (Call("Games", libraryId) as string[][] ?? Array.Empty<string[]>())
            .Select(Row).OfType<GameRow>().ToList();
        lock (Gate)
        {
            _cache = (stamp, libraryId, rows);
        }

        return rows;
    }

    /// <summary>[path, tag] of a game's picture: Primary (cover), Logo, Banner, Thumb, Art (clearart) or Disc.</summary>
    public static string? Image(Guid id, string type) => Call("Image", id, type) is string[] { Length: >= 1 } image ? image[0] : null;

    public static IReadOnlyList<string> Backdrops(Guid id) => Paths(Call("Backdrops", id));

    /// <summary>The collections of the games library: id, name, sort name and the game whose cover stands for it.</summary>
    public static IReadOnlyList<(Guid Id, string Name, string SortName, Guid? CoverGame)> Collections(Guid libraryId) =>
        (Call("Collections", libraryId) as string[][] ?? Array.Empty<string[]>())
            .Where(f => f is { Length: >= 4 } && Guid.TryParse(f[0], out _))
            .Select(f => (Guid.Parse(f[0]), f[1] ?? string.Empty, string.IsNullOrEmpty(f[2]) ? f[1] ?? string.Empty : f[2],
                Guid.TryParse(f[3], out var cover) ? cover : (Guid?)null))
            .ToList();

    /// <summary>The games of a collection with every version: id and whether it is the main version of its game.</summary>
    public static IReadOnlyList<(Guid Id, bool IsMain)> CollectionGames(Guid collectionId) =>
        (Call("CollectionGames", collectionId) as string[][] ?? Array.Empty<string[]>())
            .Where(f => f is { Length: >= 6 } && Guid.TryParse(f[0], out _))
            .Select(f => (Guid.Parse(f[0]), f[5] == "true"))
            .ToList();

    /// <summary>A collection's own pictures: "Poster" (at most one) or "Backdrop".</summary>
    public static IReadOnlyList<string> CollectionImages(Guid collectionId, string type) => Paths(Call("ArtworkPlusLink.CollectionImages", collectionId, type));

    public static IReadOnlyList<string> Discs(Guid id) => Paths(Call("Discs", id));

    private static IReadOnlyList<string> Paths(object? answer) =>
        (answer as string[][] ?? Array.Empty<string[]>()).Where(p => p is { Length: >= 1 } && !string.IsNullOrEmpty(p[0])).Select(p => p[0]).ToList();

    private static GameRow? Row(string[] f)
    {
        if (f is not { Length: >= 12 } || !Guid.TryParse(f[0], out var id))
        {
            return null;
        }

        return new GameRow(
            id,
            f[1] ?? string.Empty,
            string.IsNullOrEmpty(f[2]) ? f[1] ?? string.Empty : f[2],
            Date(f[3]),
            Date(f[4]),
            double.TryParse(f[5], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rating) ? rating : null,
            f[6] ?? string.Empty,
            int.TryParse(f[7], out var score) ? score : null,
            int.TryParse(f[8], out var sub) ? sub : null,
            Guid.TryParse(f[9], out var group) ? group : id,
            f[10] == "true",
            f[11] == "true");
    }

    private static DateTime? Date(string? text) =>
        DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date) ? date : null;

    private static object? Call(string name, params object[] args)
    {
        Resolve();
        if (!Methods.TryGetValue(name, out var method))
        {
            return null;
        }

        try
        {
            return method.Invoke(null, args);
        }
        catch (Exception)
        {
            // The Bridge failing must never break a globe: it counts as "no games".
            return null;
        }
    }

    private static void Resolve()
    {
        lock (Gate)
        {
            if (_resolved)
            {
                return;
            }

            _resolved = true;
            foreach (var assembly in AssemblyLoadContext.All.SelectMany(context => context.Assemblies))
            {
                Type? type, artwork;
                try
                {
                    type = assembly.GetType(TypeName, false);
                    artwork = assembly.GetType(ArtworkTypeName, false);
                }
                catch (Exception)
                {
                    // Another plugin's assembly that cannot be read must not stop the search.
                    continue;
                }

                if (type is null)
                {
                    continue;
                }

                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    Methods[method.Name] = method;
                }

                if (artwork?.GetMethod("CollectionImages", BindingFlags.Public | BindingFlags.Static) is { } images)
                {
                    Methods["ArtworkPlusLink.CollectionImages"] = images;
                }

                return;
            }
        }
    }
}
