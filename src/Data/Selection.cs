using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Jellyfin.Data.Enums;
#if NET10_0_OR_GREATER
using Jellyfin.Database.Implementations.Enums;
#endif
using Jellyfin.Plugin.Globe.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;

namespace Jellyfin.Plugin.Globe.Data;

/// <summary>A title on the globe: a movie, series, season, collection, home video or photo, its images and its children.</summary>
public sealed class Title
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Type { get; init; }

    public List<StoreImage> Images { get; } = new();

    public List<Title> Children { get; } = new();

    /// <summary>Libraries a collection's movie comes from, which can lie outside the libraries of the globe.</summary>
    public List<string> Libraries { get; } = new();

    /// <summary>For a title without a rating Jellyfin knows: the kind Jellyfin blocks it as (Movie, Series, Other ...).</summary>
    public string? UnratedKind { get; init; }

    /// <summary>What a globe of movies and games is sorted by: one value per sort field, null where the title has none.</summary>
    public Dictionary<string, IComparable?> SortKeys { get; init; } = new();
}

/// <summary>Picks the titles of a globe through Jellyfin's own query, in Jellyfin's own sort order.</summary>
public static class Selection
{
    public static List<Title> Load(GlobeDefinition globe, ILibraryManager libraryManager, ILocalizationManager localization, Random random)
    {
        var kinds = ArtworkKind.Parse(globe.ArtworkTypes);
        var rules = new Rules(globe, localization);
        var isRandom = string.Equals(globe.Sort, "Random", StringComparison.OrdinalIgnoreCase);

        // The games of Playnite Bridge are not in Jellyfin's database; they come from the Bridge, the rest from Jellyfin.
        var gamesId = GamesLink.LibraryId();
        var gamesLibrary = gamesId is Guid found && globe.Libraries.Any(id => Guid.TryParse(id, out var guid) && guid == found) ? found : Guid.Empty;
        var games = gamesLibrary == Guid.Empty || kinds.Count == 0 ? new List<Title>() : Games(gamesLibrary, globe, kinds, rules);
        var others = Load(globe, kinds, rules, isRandom, libraryManager, gamesLibrary);
        if (games.Count == 0 && others.Count == 0)
        {
            return others;
        }

        if (isRandom)
        {
            var all = others.Concat(games).ToList();
            ShuffleKeepingVersions(all, globe, random);
            return all;
        }

        // Folder (home videos): the folder tree as the Explorer shows it, a folder's own files before its subfolders.
        if (globe.Sort == "Folder")
        {
            others = others.OrderBy(t => t.SortKeys.GetValueOrDefault("Path") as string ?? string.Empty, FolderOrder.Instance).ToList();
        }

        // Only a globe of both is sorted here; Jellyfin's own order stays for the rest.
        return others.Count == 0 ? SortGames(games, globe) : games.Count == 0 ? others : Merge(others, SortGames(games, globe), globe);
    }

    private static List<Title> Load(GlobeDefinition globe, IReadOnlyList<ArtworkKind> kinds, Rules rules, bool isRandom, ILibraryManager libraryManager, Guid gamesLibrary)
    {
        var libraries = globe.Libraries.Select(id => Guid.TryParse(id, out var guid) ? guid : Guid.Empty)
            .Where(id => id != Guid.Empty && id != gamesLibrary && libraryManager.GetItemById(id) is not null).ToArray();
        var homeVideos = libraries.Any(id => libraryManager.GetItemById(id) is CollectionFolder { CollectionType: CollectionType.homevideos });
        var leftOut = Array.Empty<string>();
        if (string.Equals(globe.FolderMode, "Except", StringComparison.OrdinalIgnoreCase))
        {
            leftOut = ChosenFolders(libraries, globe.Folders, libraryManager).Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)).ToArray();
        }
        else
        {
            libraries = NarrowToFolders(libraries, globe.Folders, libraryManager);
        }

        if ((kinds.Count == 0 && !homeVideos) || libraries.Length == 0)
        {
            return new List<Title>();
        }

        var types = new List<BaseItemKind> { BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.BoxSet };
        if (homeVideos && globe.Media != "Photos")
        {
            types.Add(BaseItemKind.Video);
        }

        if (homeVideos && globe.Media != "Videos")
        {
            types.Add(BaseItemKind.Photo);
        }

        var query = new InternalItemsQuery
        {
            IncludeItemTypes = types.ToArray(),
            AncestorIds = libraries,
            Recursive = true,
            IsVirtualItem = false,
        };
        if (!isRandom)
        {
            query.OrderBy = Order(globe.Sort, globe.Descending);
        }

        // Extras of movies are videos too; only a video of its own is a title.
        var items = libraryManager.GetItemList(query)
            .Where(item => item.ExtraType is null && !leftOut.Any(folder => IsBelow(item.Path, folder)));
        var titles = new List<Title>();
        var inCollections = new HashSet<Guid>();
        foreach (var item in items)
        {
            // A collection counts by its movies: it stays while one of them is within the rules, with or without children.
            var members = item is BoxSet ? Children(item, libraryManager).Where(rules.Allows).ToList() : null;
            if (members is not null ? members.Count == 0 : !rules.Allows(item))
            {
                continue;
            }

            var collection = members is not null && globe.IncludeChildren;
            var title = MakeTitle(item, kinds, globe, rules, members);
            if (globe.IncludeChildren)
            {
                foreach (var child in members ?? Children(item, libraryManager).Where(rules.Allows))
                {
                    var childTitle = MakeTitle(child, kinds, globe, rules, null);
                    if (item is BoxSet)
                    {
                        inCollections.Add(child.Id);
                        childTitle.Libraries.AddRange(libraryManager.GetCollectionFolders(child).Select(l => l.Id.ToString("N")));
                    }

                    if (childTitle.Images.Count > 0)
                    {
                        title.Children.Add(childTitle);
                    }
                }
            }

            // A collection stays while one of its movies stays.
            if (collection && title.Children.Count == 0)
            {
                continue;
            }

            titles.Add(title);
        }

        // A movie of an included collection appears in its collections, and on its own only when the admin asks for it.
        return titles.Where(t => (t.Images.Count > 0 || t.Children.Count > 0)
            && (globe.CollectionTitlesAlone || !(t.Type == "Movie" && inCollections.Contains(Guid.Parse(t.Id))))).ToList();
    }

    /// <summary>The games of the Bridge's library within the rules, the main version of each game or every version (a
    /// twin is a double entry of a game and never has a tile), and its collections like movie collections.</summary>
    private static List<Title> Games(Guid library, GlobeDefinition globe, IReadOnlyList<ArtworkKind> kinds, Rules rules)
    {
        var allVersions = string.Equals(globe.Versions, "All", StringComparison.OrdinalIgnoreCase);
        var rows = GamesLink.Games(library);
        var shown = new Dictionary<Guid, (GameRow Row, int Place)>();
        for (var place = 0; place < rows.Count; place++)
        {
            var row = rows[place];
            if (!row.IsTwin && (allVersions || row.IsMain) && rules.Allows(row))
            {
                shown[row.Id] = (row, place);
            }
        }

        var collections = new List<Title>();
        var inCollections = new HashSet<Guid>();
        foreach (var collection in GamesLink.Collections(library))
        {
            // A collection stays while one of its games is within the rules.
            var members = GamesLink.CollectionGames(collection.Id).Where(m => shown.ContainsKey(m.Id)).Select(m => shown[m.Id])
                .OrderBy(m => m.Row.Released ?? DateTime.MaxValue).ThenBy(m => m.Row.SortName, StringComparer.OrdinalIgnoreCase).ToList();
            if (members.Count == 0)
            {
                continue;
            }

            var title = MakeGameCollection(collection.Id, collection.Name, collection.SortName, collection.CoverGame, members.Select(m => m.Row), kinds, globe);
            if (globe.IncludeChildren)
            {
                foreach (var (row, place) in members)
                {
                    inCollections.Add(row.Id);
                    var child = MakeGameTitle(row, place, kinds, globe);
                    if (child.Images.Count > 0)
                    {
                        title.Children.Add(child);
                    }
                }
            }

            if (globe.IncludeChildren ? title.Children.Count > 0 : title.Images.Count > 0)
            {
                collections.Add(title);
            }
        }

        // A game of an included collection appears in its collections, and on its own only when the admin asks for it.
        return shown.Values.OrderBy(g => g.Place)
            .Where(g => globe.CollectionTitlesAlone || !inCollections.Contains(g.Row.Id))
            .Select(g => MakeGameTitle(g.Row, g.Place, kinds, globe))
            .Where(t => t.Images.Count > 0)
            .Concat(collections)
            .ToList();
    }

    /// <summary>A collection of games: its own poster and backdrops, else the cover of the game that stands for it.</summary>
    private static Title MakeGameCollection(Guid id, string name, string sortName, Guid? coverGame, IEnumerable<GameRow> members, IReadOnlyList<ArtworkKind> kinds, GlobeDefinition globe)
    {
        var title = new Title
        {
            Id = id.ToString("N"),
            Name = name,
            Type = "GameCollection",
            // Unrated as far as its games are.
            UnratedKind = members.Any(m => m.RatingScore is null) ? "Movie" : null,
            SortKeys = new Dictionary<string, IComparable?> { ["SortName"] = sortName, ["Group"] = id.ToString("N"), ["Place"] = 0 },
        };

        if (kinds.Contains(ArtworkKind.Poster))
        {
            var poster = GamesLink.CollectionImages(id, "Poster").FirstOrDefault()
                ?? (coverGame is Guid game ? GamesLink.Image(game, "Primary") : null);
            if (poster is not null && File.Exists(poster))
            {
                var shape = globe.CoverFit == "OwnShape" ? ArtworkKind.NearestShape(AspectOf(poster, ArtworkKind.Poster.Aspect)) : ArtworkKind.Poster;
                title.Images.Add(new StoreImage(shape, poster, File.GetLastWriteTimeUtc(poster), ImagePreparation.Treatment(globe.BlackBars, globe.CoverFit, globe.CropLimit)));
            }
        }

        var backdrop = ArtworkKind.All.First(k => k.Name == "Backdrop");
        if (kinds.Contains(backdrop))
        {
            foreach (var file in ChooseBackdrops(GamesLink.CollectionImages(id, "Backdrop"), globe.Backdrops).Where(File.Exists))
            {
                title.Images.Add(new StoreImage(backdrop, file, File.GetLastWriteTimeUtc(file), ImagePreparation.Treatment(globe.BlackBars, "Crop", globe.CropLimit)));
            }
        }

        return title;
    }

    private static Title MakeGameTitle(GameRow row, int place, IReadOnlyList<ArtworkKind> kinds, GlobeDefinition globe)
    {
        var title = new Title
        {
            Id = row.Id.ToString("N"),
            Name = row.Name,
            Type = "Game",
            // The Bridge counts games as movies when Jellyfin blocks unrated items.
            UnratedKind = row.RatingScore is null ? "Movie" : null,
            SortKeys = new Dictionary<string, IComparable?>
            {
                ["SortName"] = row.SortName,
                ["DateCreated"] = row.Added,
                ["PremiereDate"] = row.Released,
                ["CommunityRating"] = row.CommunityRating,
                ["OfficialRating"] = row.RatingScore is int score ? score * 1000 + (row.RatingSubScore ?? 0) : null,
                ["Group"] = row.GroupId.ToString("N"),
                ["Place"] = place,
            },
        };

        var opaque = ImagePreparation.Treatment(globe.BlackBars, "Crop", globe.CropLimit);
        foreach (var kind in ArtworkKind.TitleOrder.Where(kinds.Contains))
        {
            IEnumerable<string> files = kind.Name switch
            {
                "Backdrop" => Selection.ChooseBackdrops(GamesLink.Backdrops(row.Id), globe.Backdrops),
                "Disc" => Selection.ChooseBackdrops(DistinctPictures(GamesLink.Discs(row.Id)), globe.Discs),
                "Poster" => new[] { GamesLink.Image(row.Id, "Primary") }.OfType<string>(),
                "Clearart" => new[] { GamesLink.Image(row.Id, "Art") }.OfType<string>(),
                _ => new[] { GamesLink.Image(row.Id, kind.Name) }.OfType<string>(),
            };

            foreach (var file in files.Where(File.Exists))
            {
                var modified = File.GetLastWriteTimeUtc(file);
                if (kind.Name == "Poster")
                {
                    // The cover: cut to 2:3, or in its own shape, or on a blurred fill, as the globe's Cover Fit says.
                    var shape = globe.CoverFit == "OwnShape" ? ArtworkKind.NearestShape(AspectOf(file, kind.Aspect)) : kind;
                    title.Images.Add(new StoreImage(shape, file, modified, ImagePreparation.Treatment(globe.BlackBars, globe.CoverFit, globe.CropLimit)));
                }
                else
                {
                    title.Images.Add(new StoreImage(kind, file, modified, kind.Transparent ? string.Empty : opaque));
                }
            }
        }

        return title;
    }

    /// <summary>Two disc files can hold the same picture (disc and cdart); each picture counts once.</summary>
    private static List<string> DistinctPictures(IReadOnlyList<string> files)
    {
        if (files.Count < 2)
        {
            return files.ToList();
        }

        var seen = new HashSet<string>();
        var kept = new List<string>();
        foreach (var file in files.Where(File.Exists))
        {
            using var stream = File.OpenRead(file);
            if (seen.Add(Convert.ToHexString(SHA256.HashData(stream))))
            {
                kept.Add(file);
            }
        }

        return kept;
    }

    private static double AspectOf(string file, double fallback)
    {
        var size = ImagePreparation.Size(file);
        return size is { Width: > 0, Height: > 0 } ? (double)size.Value.Width / size.Value.Height : fallback;
    }

    /// <summary>The games in the globe's sort; with "keep versions together" a game's versions stand at its main
    /// version's place, in the library's order.</summary>
    private static List<Title> SortGames(List<Title> games, GlobeDefinition globe)
    {
        var keep = globe.KeepVersionsTogether;
        var mains = games.GroupBy(t => (string)t.SortKeys["Group"]!).ToDictionary(g => g.Key, g => g.FirstOrDefault(t => t.Id == g.Key) ?? g.First());
        var order = Comparer(globe);
        return games.OrderBy(t => keep ? mains[(string)t.SortKeys["Group"]!] : t, order)
            .ThenBy(t => keep ? (int)t.SortKeys["Place"]! : 0)
            .ToList();
    }

    /// <summary>A globe of movies and games: both lists in one order by the same sort.</summary>
    private static List<Title> Merge(List<Title> others, List<Title> games, GlobeDefinition globe)
    {
        var order = Comparer(globe);
        var merged = new List<Title>(others.Count + games.Count);
        int a = 0, b = 0;
        while (a < others.Count || b < games.Count)
        {
            // A game's versions follow their main version without a break.
            var takeGame = a == others.Count || (b < games.Count && order.Compare(games[b], others[a]) < 0);
            merged.Add(takeGame ? games[b++] : others[a++]);
            while (takeGame && b < games.Count && globe.KeepVersionsTogether && games[b].SortKeys["Group"]!.Equals(games[b - 1].SortKeys["Group"]))
            {
                merged.Add(games[b++]);
            }
        }

        return merged;
    }

    /// <summary>The sort field, then the name, in one direction; a title without a value comes first going up.</summary>
    private static IComparer<Title> Comparer(GlobeDefinition globe)
    {
        var field = globe.Sort is "CommunityRating" or "DateCreated" or "OfficialRating" or "PremiereDate" ? globe.Sort : "SortName";
        var direction = globe.Descending ? -1 : 1;
        return Comparer<Title>.Create((x, y) =>
        {
            var result = Compare(x.SortKeys.GetValueOrDefault(field), y.SortKeys.GetValueOrDefault(field));
            if (result == 0 && field != "SortName")
            {
                result = Compare(x.SortKeys.GetValueOrDefault("SortName"), y.SortKeys.GetValueOrDefault("SortName"));
            }

            return result * direction;
        });
    }

    private static int Compare(IComparable? x, IComparable? y) =>
        x is null ? (y is null ? 0 : -1)
        : y is null ? 1
        : x is string a && y is string b ? string.Compare(a, b, StringComparison.OrdinalIgnoreCase)
        : x.CompareTo(y);

    /// <summary>Random: with Cluster and "keep versions together" a game's versions are shuffled as one.</summary>
    private static void ShuffleKeepingVersions(List<Title> titles, GlobeDefinition globe, Random random)
    {
        if (!globe.Cluster || !globe.KeepVersionsTogether)
        {
            Shuffle(titles, random);
            return;
        }

        var blocks = titles.GroupBy(t => t.SortKeys.GetValueOrDefault("Group") as string ?? t.Id).Select(g => g.ToList()).ToList();
        Shuffle(blocks, random);
        titles.Clear();
        titles.AddRange(blocks.SelectMany(block => block));
    }

    /// <summary>The rating rules of one globe: its highest rating and whether unrated titles stay out.</summary>
    private sealed class Rules
    {
        private readonly ILocalizationManager _localization;
        private readonly (int Score, int Sub)? _limit;
        private readonly bool _leaveOutUnrated;

        public Rules(GlobeDefinition globe, ILocalizationManager localization)
        {
            _localization = localization;
            _leaveOutUnrated = globe.LeaveOutUnrated;
            _limit = string.IsNullOrWhiteSpace(globe.MaxRating) ? null : Ratings.Score(globe.MaxRating, localization, null);
        }

        public bool Allows(BaseItem item) => Allows(Ratings.Of(item, _localization));

        /// <summary>A game by the strictest rating the Bridge gives it.</summary>
        public bool Allows(GameRow game) => Allows(game.RatingScore is int score ? (score, game.RatingSubScore ?? 0) : null);

        private bool Allows((int Score, int Sub)? score)
        {
            if (score is null)
            {
                return !_leaveOutUnrated;
            }

            return _limit is null || Ratings.AtMost(score.Value, _limit.Value);
        }

        public string? UnratedKind(BaseItem item) => Ratings.Of(item, _localization) is null ? item.GetBlockUnratedType().ToString() : null;

        public (int Score, int Sub)? Score(BaseItem item) => Ratings.Of(item, _localization);
    }

    /// <summary>Puts chosen folders in the place of their library. Folders can sit at any depth: Jellyfin keeps every
    /// ancestor of an item, so a query on a folder finds everything below it. A folder that is gone stays out, and its
    /// library does not fall back to the whole library.</summary>
    private static Guid[] NarrowToFolders(Guid[] libraries, string[] folders, ILibraryManager libraryManager)
    {
        var chosen = Choices(libraries, folders);
        var present = chosen.Select(choice => choice.Folder).Where(id => libraryManager.GetItemById(id) is not null);
        return libraries.Except(chosen.Select(choice => choice.Library)).Concat(present).ToArray();
    }

    /// <summary>The ticked folders that still exist; with "Except" a folder that is gone leaves nothing out.</summary>
    private static IEnumerable<BaseItem> ChosenFolders(Guid[] libraries, string[] folders, ILibraryManager libraryManager) =>
        Choices(libraries, folders).Select(choice => libraryManager.GetItemById(choice.Folder)).OfType<BaseItem>();

    private static List<(Guid Library, Guid Folder)> Choices(Guid[] libraries, string[] folders) =>
        folders.Select(entry => entry.Split(':'))
            .Where(parts => parts.Length == 2 && Guid.TryParse(parts[0], out _) && Guid.TryParse(parts[1], out _))
            .Select(parts => (Library: Guid.Parse(parts[0]), Folder: Guid.Parse(parts[1])))
            .Where(choice => libraries.Contains(choice.Library))
            .ToList();

    private static bool IsBelow(string? path, string folder) =>
        !string.IsNullOrEmpty(path)
        && path.StartsWith(folder.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>Jellyfin's sort as its web client asks for it: the field, then name and year, all in one direction.</summary>
    private static (ItemSortBy, SortOrder)[] Order(string sort, bool descending)
    {
        var direction = descending ? SortOrder.Descending : SortOrder.Ascending;
        if (!Enum.TryParse<ItemSortBy>(sort, true, out var field) || field is ItemSortBy.DatePlayed or ItemSortBy.PlayCount)
        {
            field = ItemSortBy.SortName;
        }

        return field == ItemSortBy.SortName
            ? new[] { (ItemSortBy.SortName, direction), (ItemSortBy.ProductionYear, direction) }
            : new[] { (field, direction), (ItemSortBy.SortName, direction), (ItemSortBy.ProductionYear, direction) };
    }

    private static IEnumerable<BaseItem> Children(BaseItem item, ILibraryManager libraryManager)
    {
        if (item is Series)
        {
            return libraryManager.GetItemList(new InternalItemsQuery
            {
                ParentId = item.Id,
                IncludeItemTypes = new[] { BaseItemKind.Season },
                IsVirtualItem = false,
                OrderBy = new[] { (ItemSortBy.IndexNumber, SortOrder.Ascending) }
            });
        }

        if (item is BoxSet collection)
        {
            return collection.GetLinkedChildren().OfType<Movie>()
                .OrderBy(m => m.PremiereDate ?? DateTime.MaxValue).ThenBy(m => m.SortName, StringComparer.OrdinalIgnoreCase);
        }

        return Array.Empty<BaseItem>();
    }

    private static Title MakeTitle(BaseItem item, IReadOnlyList<ArtworkKind> kinds, GlobeDefinition globe, Rules rules, List<BaseItem>? members)
    {
        var rating = rules.Score(item);
        var title = new Title
        {
            Id = item.Id.ToString("N"),
            Name = item.Name,
            Type = item.GetType().Name,
            // A collection is unrated as far as its movies are.
            UnratedKind = members is null ? rules.UnratedKind(item) : members.Select(rules.UnratedKind).FirstOrDefault(kind => kind is not null),
            SortKeys = new Dictionary<string, IComparable?>
            {
                ["SortName"] = item.SortName,
                ["DateCreated"] = item.DateCreated,
                ["PremiereDate"] = item.PremiereDate,
                ["CommunityRating"] = item.CommunityRating is float community ? (double)community : null,
                ["OfficialRating"] = rating is { } r ? r.Score * 1000 + r.Sub : null,
                ["Path"] = item.Path,
            },
        };

        var opaque = ImagePreparation.Treatment(globe.BlackBars, "Crop", globe.CropLimit);

        // A photo always comes with its own picture, a home video with Poster ticked.
        if (item is Photo || item.GetType() == typeof(Video))
        {
            var picture = item is Photo || kinds.Contains(ArtworkKind.Poster)
                ? item.GetImages(ImageType.Primary).FirstOrDefault(i => i.IsLocalFile && File.Exists(i.Path))
                : null;
            if (picture is not null)
            {
                var aspect = Aspect(picture);
                var kind = globe.PictureFit == "OwnShape" ? ArtworkKind.NearestShape(aspect) : ArtworkKind.NearestCard(aspect);
                title.Images.Add(new StoreImage(kind, picture.Path, picture.DateModified, ImagePreparation.Treatment(globe.BlackBars, globe.PictureFit, globe.CropLimit)));
            }

            kinds = kinds.Where(k => k.ImageType != ImageType.Primary).ToList();
        }

        foreach (var kind in ArtworkKind.TitleOrder.Where(kinds.Contains))
        {
            // A file can be listed twice, as the main backdrop and among the others.
            var images = item.GetImages(kind.ImageType).Where(i => i.IsLocalFile && File.Exists(i.Path)).DistinctBy(i => i.Path).ToList();
            var chosen = kind.ImageType == ImageType.Backdrop ? ChooseBackdrops(images, globe.Backdrops) : images.Take(1);
            title.Images.AddRange(chosen.Select(i => new StoreImage(kind, i.Path, i.DateModified, kind.Transparent ? string.Empty : opaque)));
        }

        return title;
    }

    /// <summary>The picture's aspect from the file's header, turned by its EXIF origin (Jellyfin keeps a photo's size as
    /// stored, not as shown); Jellyfin's size when the header cannot be read.</summary>
    private static double Aspect(ItemImageInfo image)
    {
        var size = ImagePreparation.Size(image.Path);
        if (size is { Width: > 0, Height: > 0 })
        {
            return (double)size.Value.Width / size.Value.Height;
        }

        return image.Width > 0 && image.Height > 0 ? (double)image.Width / image.Height : 16.0 / 9.0;
    }

    /// <summary>The images whose numbers are in <paramref name="choice"/>, such as "1", "2-" or "2-5,8".</summary>
    public static IEnumerable<T> ChooseBackdrops<T>(IReadOnlyList<T> images, string choice)
    {
        var numbers = new SortedSet<int>();
        foreach (var part in choice.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var ends = part.Split('-');
            if (ends.Length <= 2 && int.TryParse(ends[0], out var first))
            {
                var last = ends.Length == 1 ? first : int.TryParse(ends[1], out var end) ? end : images.Count;
                for (var number = Math.Max(first, 1); number <= Math.Min(last, images.Count); number++)
                {
                    numbers.Add(number);
                }
            }
        }

        return numbers.Select(number => images[number - 1]);
    }

    /// <summary>Whether <paramref name="number"/> is in <paramref name="choice"/>, written like the backdrop choice ("1-9", "2-", "1-5,8").</summary>
    public static bool InChoice(int number, string choice)
    {
        foreach (var part in choice.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var ends = part.Split('-');
            if (ends.Length <= 2 && int.TryParse(ends[0], out var first))
            {
                var last = ends.Length == 1 ? first : int.TryParse(ends[1], out var end) ? end : int.MaxValue;
                if (number >= first && number <= last)
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static void Shuffle<T>(IList<T> list, Random random)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}

/// <summary>
/// Paths in the order of a walk through the folder tree: folder by folder, a folder's own files before its subfolders,
/// names in natural order as the Explorer shows them (2 before 10, capitals and small letters alike).
/// </summary>
public sealed class FolderOrder : IComparer<string>
{
    public static readonly FolderOrder Instance = new();

    public int Compare(string? x, string? y)
    {
        var a = (x ?? string.Empty).Split('\\', '/');
        var b = (y ?? string.Empty).Split('\\', '/');
        for (var i = 0; ; i++)
        {
            bool aFile = i == a.Length - 1, bFile = i == b.Length - 1;
            if (aFile != bFile)
            {
                // One ends here with a file, the other goes on into a subfolder: the file comes first.
                return aFile ? -1 : 1;
            }

            var result = Natural(a[i], b[i]);
            if (result != 0 || aFile)
            {
                return result;
            }
        }
    }

    /// <summary>Digit runs compare by their value, the rest without regard to case; ties by the plain text.</summary>
    public static int Natural(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsAsciiDigit(a[i]))
                {
                    i++;
                }

                while (j < b.Length && char.IsAsciiDigit(b[j]))
                {
                    j++;
                }

                var da = a[si..i].TrimStart('0');
                var db = b[sj..j].TrimStart('0');
                var byValue = da.Length != db.Length ? da.Length.CompareTo(db.Length) : string.CompareOrdinal(da, db);
                if (byValue != 0)
                {
                    return byValue;
                }
            }
            else
            {
                var byLetter = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                if (byLetter != 0)
                {
                    return byLetter;
                }

                i++;
                j++;
            }
        }

        var byRest = (a.Length - i).CompareTo(b.Length - j);
        return byRest != 0 ? byRest : string.CompareOrdinal(a, b);
    }
}
