using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.Globe.Data;

/// <summary>Where a tile lies in an atlas: page and pixel rectangle.</summary>
public readonly record struct AtlasRect(int Page, int X, int Y, int Width, int Height);

/// <summary>
/// The atlas pages of one stage (tiny or small) for one group (opaque or transparent): every tile of the globe in a few
/// large textures, so the whole globe shows at once. Tiles are packed on shelves, one kind after the other; a page is
/// encoded in strips (BC1 or BC3 with mip levels), so a large page never sits in memory as raw pixels.
/// </summary>
public static class Atlas
{
    public const int PageWidth = 16384;
    public const int MaxPageHeight = 16384;

    public sealed record Page(int Height);

    /// <summary>Packs the tiles; <paramref name="tiles"/> are (tile index, kind). Returns the pages and a rect per tile.</summary>
    public static List<Page> Pack(IEnumerable<(int Tile, ArtworkKind Kind)> tiles, bool small, int levels, int maxPages, Dictionary<int, AtlasRect> rects)
    {
        var step = 4 << (levels - 1);
        var pages = new List<Page>();
        int page = 0, x = 0, y = 0, rowHeight = 0;
        ArtworkKind? lastKind = null;
        foreach (var (tile, kind) in tiles.OrderBy(t => t.Kind.Index))
        {
            int w = small ? kind.SmallWidth : kind.TinyWidth, h = small ? kind.SmallHeight : kind.TinyHeight;
            if (lastKind != kind || x + w > PageWidth)
            {
                x = 0;
                y += rowHeight;
                rowHeight = 0;
                lastKind = kind;
            }

            if (y + h > MaxPageHeight)
            {
                pages.Add(new Page(Round(y, step)));
                page++;
                if (page >= maxPages)
                {
                    throw new InvalidOperationException("too many images for one globe");
                }

                x = 0;
                y = 0;
                rowHeight = 0;
            }

            rects[tile] = new AtlasRect(page, x, y, w, h);
            x += w;
            rowHeight = Math.Max(rowHeight, h);
        }

        if (rects.Count > 0)
        {
            pages.Add(new Page(Round(y + rowHeight, step)));
        }

        return pages;
    }

    private static int Round(int value, int step) => Math.Max(step, (value + step - 1) / step * step);

    /// <summary>Writes one page: level by level, each level the page halved again. Pixels come from <paramref name="pixelsOf"/>
    /// (RGBA of the tile at its atlas size).</summary>
    public static void Write(
        string file,
        int page,
        int height,
        int levels,
        bool transparent,
        IReadOnlyList<(int Tile, AtlasRect Rect)> placed,
        Func<int, byte[]> pixelsOf)
    {
        var step = 4 << (levels - 1);
        var channels = transparent ? 4 : 3;
        var levelFiles = Enumerable.Range(0, levels).Select(l => file + ".level" + l).ToArray();
        var outputs = levelFiles.Select(f => new FileStream(f, FileMode.Create, FileAccess.Write)).ToArray();
        try
        {
            var onPage = placed.Where(p => p.Rect.Page == page).OrderBy(p => p.Rect.Y).ToList();
            var cache = new Dictionary<int, byte[]>();
            var next = 0;
            var active = new List<(int Tile, AtlasRect Rect)>();
            for (var top = 0; top < height; top += step)
            {
                while (next < onPage.Count && onPage[next].Rect.Y < top + step)
                {
                    active.Add(onPage[next++]);
                }

                active.RemoveAll(p =>
                {
                    var gone = p.Rect.Y + p.Rect.Height <= top;
                    if (gone)
                    {
                        cache.Remove(p.Tile);
                    }

                    return gone;
                });

                var strip = new byte[PageWidth * step * channels];
                foreach (var (tile, rect) in active)
                {
                    if (!cache.TryGetValue(tile, out var rgba))
                    {
                        cache[tile] = rgba = pixelsOf(tile);
                    }

                    var from = Math.Max(rect.Y, top);
                    var to = Math.Min(rect.Y + rect.Height, top + step);
                    for (var row = from; row < to; row++)
                    {
                        var source = (row - rect.Y) * rect.Width * 4;
                        var target = ((row - top) * PageWidth + rect.X) * channels;
                        for (var px = 0; px < rect.Width; px++, source += 4, target += channels)
                        {
                            strip[target] = rgba[source];
                            strip[target + 1] = rgba[source + 1];
                            strip[target + 2] = rgba[source + 2];
                            if (transparent)
                            {
                                strip[target + 3] = rgba[source + 3];
                            }
                        }
                    }
                }

                int width = PageWidth, rows = step;
                for (var level = 0; level < levels; level++)
                {
                    outputs[level].Write(transparent ? BlockCompression.EncodeBc3(strip, width, rows) : BlockCompression.EncodeBc1(strip, width, rows));
                    if (level < levels - 1)
                    {
                        strip = BlockCompression.Half(strip, width, rows, channels);
                        width /= 2;
                        rows /= 2;
                    }
                }
            }
        }
        finally
        {
            foreach (var output in outputs)
            {
                output.Dispose();
            }
        }

        using (var result = File.Create(file))
        {
            foreach (var levelFile in levelFiles)
            {
                using (var part = File.OpenRead(levelFile))
                {
                    part.CopyTo(result);
                }

                File.Delete(levelFile);
            }
        }
    }
}
