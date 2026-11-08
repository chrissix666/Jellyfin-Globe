using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.Globe.Configuration;

namespace Jellyfin.Plugin.Globe.Data;

public enum BuildOutcome
{
    Built,
    Unchanged,
    Empty,
}

public sealed record BuildResult(BuildOutcome Outcome, int Images, int Unreadable);

/// <summary>
/// Builds one globe from its titles: the sequence of tiles, the images in the shared store, the column mosaic, the
/// atlas pages of the tiny and small stage and the data the client reads. The new globe replaces the old one at once.
/// </summary>
public static class GlobeBuilder
{
    public const int BundleSize = 16;
    private const int TinyLevels = 3;
    private const int SmallLevels = 5;

    // The client has sixteen texture units: one tiny page and three small pages per group fit beside its other textures.
    private const int TinyPages = 1;
    private const int SmallPages = 3;
    // Version of the data format; it is part of the fingerprint, so a new format rebuilds every globe.
    private const int Version = 2;

    private const int SwapAttempts = 10;

    private sealed record Tile(StoreImage Image, int Item, string ItemId, int Cluster);

    public static BuildResult Build(
        GlobeDefinition globe,
        IReadOnlyList<Title> titles,
        ImageStore store,
        string folder,
        Random random,
        string settings,
        bool reshuffle,
        string gamesLibrary,
        Action<double, string> progress,
        CancellationToken cancellationToken)
    {
        var items = new List<object>();
        var tiles = new List<Tile>();
        for (var cluster = 0; cluster < titles.Count; cluster++)
        {
            Add(titles[cluster], cluster);
            foreach (var child in titles[cluster].Children)
            {
                Add(child, cluster);
            }
        }

        void Add(Title title, int cluster)
        {
            items.Add(new { id = title.Id, name = title.Name, type = title.Type });
            tiles.AddRange(title.Images.Select(image => new Tile(image, items.Count - 1, title.Id, cluster)));
        }

        var isRandom = string.Equals(globe.Sort, "Random", StringComparison.OrdinalIgnoreCase);
        if (isRandom && !globe.Cluster)
        {
            Selection.Shuffle(tiles, random);
        }

        if (tiles.Count == 0)
        {
            RemoveGlobe(folder);
            return new BuildResult(BuildOutcome.Empty, 0, 0);
        }

        // What the server checks before it shows the globe; it can change while the tiles stay the same (a title gets
        // a rating, a setting that does not touch the tiles), so an unchanged globe gets it anew too.
        var rules = new Dictionary<string, object>
        {
            ["settings"] = settings,
            ["maxRating"] = globe.MaxRating,
            ["gamesLibrary"] = gamesLibrary,
            ["unratedKinds"] = titles.Concat(titles.SelectMany(t => t.Children)).Select(t => t.UnratedKind).OfType<string>().Distinct().ToArray(),
            ["libraries"] = globe.Libraries.Concat(titles.SelectMany(t => t.Children).SelectMany(c => c.Libraries)).Distinct().ToArray(),
        };

        var fingerprint = Fingerprint(globe, tiles, isRandom);
        // A random globe due for a new shuffle is built again although its titles are the same.
        if (!reshuffle && ReadFingerprint(folder) == fingerprint)
        {
            UpdateLayout(folder, rules);
            return new BuildResult(BuildOutcome.Unchanged, tiles.Count, 0);
        }

        progress(0, "images");
        var unreadable = store.Add(tiles.Select(t => t.Image).ToList(), new Progress<double>(p => progress(p * 0.75, "images")), cancellationToken);
        tiles = tiles.Where(t => !unreadable.Contains(t.Image.Key)).ToList();
        if (tiles.Count == 0)
        {
            RemoveGlobe(folder);
            return new BuildResult(BuildOutcome.Empty, 0, unreadable.Count);
        }

        var records = tiles.Select(t => store.TryGetRecord(t.Image, out var record) ? record : -1).ToArray();

        progress(75, "layout");
        var layout = Mosaic.Build(tiles.Select(t => new MosaicTile(t.Image.Kind.Aspect, t.Cluster)).ToList(),
            globe.Start is "NorthPole" or "SouthPole" ? globe.Start : "Front");

        var building = folder + ".building";
        if (Directory.Exists(building))
        {
            Directory.Delete(building, true);
        }

        Directory.CreateDirectory(building);
        var tiny = new Dictionary<int, AtlasRect>();
        var small = new Dictionary<int, AtlasRect>();
        var atlases = new Dictionary<string, object>();
        var stageCount = 0;
        foreach (var (name, rects, isSmall, levels, maxPages) in new[] { ("tiny", tiny, false, TinyLevels, TinyPages), ("small", small, true, SmallLevels, SmallPages) })
        {
            var stage = new Dictionary<string, object> { ["levels"] = levels, ["pageWidth"] = Atlas.PageWidth };
            foreach (var transparent in new[] { false, true })
            {
                cancellationToken.ThrowIfCancellationRequested();
                var group = Enumerable.Range(0, tiles.Count).Where(i => tiles[i].Image.Kind.Transparent == transparent)
                    .Select(i => (i, tiles[i].Image.Kind)).ToList();
                var groupRects = new Dictionary<int, AtlasRect>();
                var pages = Atlas.Pack(group, isSmall, levels, maxPages, groupRects);
                var placed = groupRects.Select(r => (r.Key, r.Value)).ToList();
                for (var page = 0; page < pages.Count; page++)
                {
                    Atlas.Write(
                        Path.Combine(building, $"{name}-{(transparent ? "alpha" : "opaque")}-{page}.bin"),
                        page,
                        pages[page].Height,
                        levels,
                        transparent,
                        placed,
                        i => isSmall ? store.ReadSmall(tiles[i].Image.Kind, records[i]) : store.ReadTiny(tiles[i].Image.Kind, records[i]));
                }

                foreach (var (tile, rect) in groupRects)
                {
                    rects[tile] = rect;
                }

                stage[transparent ? "alpha" : "opaque"] = pages.Select(p => p.Height).ToArray();
                progress(80 + 15 * ++stageCount / 4.0, "atlas");
            }

            atlases[name] = stage;
        }

        WriteFloats(Path.Combine(building, "rings.bin"), layout.Rings.SelectMany(r => new[] { r.Offset, r.FirstColumn, r.ColumnCount, r.Scale }));
        WriteFloats(Path.Combine(building, "columns.bin"), layout.Columns.SelectMany(c => new double[] { c.Start, c.Width, c.FirstTile, c.TileCount }));
        WriteFloats(Path.Combine(building, "tiles.bin"), Enumerable.Range(0, tiles.Count).SelectMany(i =>
        {
            var slot = layout.Slots[i];
            AtlasRect tinyRect = tiny[i], smallRect = small[i];
            var kind = tiles[i].Image.Kind;
            return new double[]
            {
                slot.Top, slot.Height, kind.Index, tiles[i].Item,
                tinyRect.X, tinyRect.Y, tinyRect.Width, tinyRect.Height,
                smallRect.X, smallRect.Y, smallRect.Width, smallRect.Height,
                tinyRect.Page, smallRect.Page, kind.Transparent ? 1 : 0, store.Info(tiles[i].Image.Key)?.Animated == true ? 1 : 0,
            };
        }));
        using (var file = File.Create(Path.Combine(building, "records.bin")))
        using (var writer = new BinaryWriter(file))
        {
            for (var i = 0; i < tiles.Count; i++)
            {
                writer.Write(tiles[i].Image.Kind.Index);
                writer.Write(records[i]);
            }
        }

        File.WriteAllText(Path.Combine(building, "items.json"), JsonSerializer.Serialize(items));

        // Where an animated original goes over its tile: the part of the original the tile shows and where it sits.
        var animated = new Dictionary<string, object>();
        for (var i = 0; i < tiles.Count; i++)
        {
            if (store.Info(tiles[i].Image.Key) is { Animated: true } info)
            {
                animated[i.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new { source = info.Source, target = info.Target };
            }
        }

        File.WriteAllText(Path.Combine(building, "animated.json"), JsonSerializer.Serialize(animated));

        // The files behind the animated tiles; only the server reads this, like images.json.
        File.WriteAllText(Path.Combine(building, "animated-files.json"), JsonSerializer.Serialize(
            animated.Keys.ToDictionary(tile => tile, tile => tiles[int.Parse(tile, System.Globalization.CultureInfo.InvariantCulture)].Image.File)));
        File.WriteAllText(Path.Combine(building, "images.json"), JsonSerializer.Serialize(tiles.Select(t => t.Image.Key).Distinct()));
        File.WriteAllText(Path.Combine(building, "layout.json"), JsonSerializer.Serialize(new Dictionary<string, object>(rules)
        {
            ["version"] = Version,
            ["fingerprint"] = fingerprint,
            ["builtAt"] = DateTime.UtcNow,
            ["images"] = tiles.Count,
            ["items"] = items.Count,
            ["ringCount"] = layout.Rings.Length,
            ["startRing"] = layout.StartRing,
            ["bundleSize"] = BundleSize,
            ["storeGeneration"] = store.Generation,
            ["kinds"] = ArtworkKind.All.Select(k => new
            {
                index = k.Index, name = k.Name, transparent = k.Transparent,
                width = k.SharpWidth, height = k.SharpHeight, bytes = k.SharpBytes,
            }),
            ["atlas"] = atlases,
        }));

        cancellationToken.ThrowIfCancellationRequested();
        Replace(building, folder, cancellationToken);
        progress(100, "done");
        return new BuildResult(BuildOutcome.Built, tiles.Count, unreadable.Count);
    }

    /// <summary>The images a built globe uses (for freeing store records nobody needs).</summary>
    public static IEnumerable<string> UsedImages(string folder)
    {
        var file = Path.Combine(folder, "images.json");
        return File.Exists(file) ? JsonSerializer.Deserialize<string[]>(File.ReadAllText(file)) ?? Array.Empty<string>() : Array.Empty<string>();
    }

    /// <summary>Everything that decides how a globe looks and where its tiles lead, except its name and colour. With
    /// Random the order is new on every build, so only the set counts.</summary>
    private static string Fingerprint(GlobeDefinition globe, List<Tile> tiles, bool isRandom)
    {
        var text = new StringBuilder();
        text.Append(Version).Append('|')
            .Append(string.Join(',', globe.Libraries.OrderBy(l => l, StringComparer.Ordinal))).Append('|')
            .Append(string.Join(',', globe.Folders.OrderBy(f => f, StringComparer.Ordinal))).Append('|')
            .Append(string.Join(',', globe.ArtworkTypes.OrderBy(t => t, StringComparer.OrdinalIgnoreCase))).Append('|')
            .Append(globe.IncludeChildren).Append('|').Append(globe.Backdrops).Append('|')
            .Append(globe.Sort).Append('|').Append(globe.Descending).Append('|').Append(globe.Start).Append('|')
            .Append(globe.Cluster).Append('|').Append(globe.FolderMode).Append('|').Append(globe.Media).Append('|')
            .Append(globe.CollectionTitlesAlone).Append('|').Append(globe.MaxRating).Append('|').Append(globe.LeaveOutUnrated).Append('|')
            .Append(globe.Versions).Append('|').Append(globe.KeepVersionsTogether).Append('|').Append(globe.Discs).Append('\n');
        var keys = tiles.Select(t => t.ItemId + ":" + t.Image.Key);
        foreach (var key in isRandom ? keys.OrderBy(k => k, StringComparer.Ordinal) : keys)
        {
            text.Append(key).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>A globe with nothing left to show goes: its old data may come from libraries it no longer uses.</summary>
    private static void RemoveGlobe(string folder)
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, true);
        }
    }

    private static void UpdateLayout(string folder, Dictionary<string, object> values)
    {
        var file = Path.Combine(folder, "layout.json");
        var layout = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(file))!;
        foreach (var (name, value) in values)
        {
            layout[name] = JsonSerializer.SerializeToElement(value);
        }

        File.WriteAllText(file + ".new", JsonSerializer.Serialize(layout));
        File.Move(file + ".new", file, true);
    }

    private static string? ReadFingerprint(string folder)
    {
        try
        {
            var file = Path.Combine(folder, "layout.json");
            if (!File.Exists(file))
            {
                return null;
            }

            using var layout = JsonDocument.Parse(File.ReadAllText(file));
            return layout.RootElement.TryGetProperty("fingerprint", out var value) ? value.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Windows refuses to move a folder while a client reads a file in it, which usually ends within seconds.</summary>
    private static void MoveWhenFree(string from, string to, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(from, to);
                return;
            }
            catch (Exception ex) when (attempt < SwapAttempts && ex is IOException or UnauthorizedAccessException)
            {
                cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(1));
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

    private static void WriteFloats(string file, IEnumerable<double> values)
    {
        using var stream = File.Create(file);
        using var writer = new BinaryWriter(stream);
        foreach (var value in values)
        {
            writer.Write((float)value);
        }
    }

    /// <summary>Swaps the new globe in, so the client never reads a half-written one.</summary>
    private static void Replace(string building, string folder, CancellationToken cancellationToken)
    {
        var old = folder + ".old";
        if (Directory.Exists(old))
        {
            Directory.Delete(old, true);
        }

        if (Directory.Exists(folder))
        {
            MoveWhenFree(folder, old, cancellationToken);
        }

        Directory.Move(building, folder);
        if (Directory.Exists(old))
        {
            Directory.Delete(old, true);
        }
    }
}
