using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Globe.Data;

/// <summary>A tile as the layout sees it: its aspect and the title (cluster) it belongs to.</summary>
public readonly record struct MosaicTile(double Aspect, int Cluster);

/// <summary>A ring of the sphere: where its first column starts (fraction of the circle) and which columns it holds.</summary>
public readonly record struct MosaicRing(double Offset, int FirstColumn, int ColumnCount, double Scale);

/// <summary>A column: its start and width as fractions of its ring, and the tiles stacked in it.</summary>
public readonly record struct MosaicColumn(double Start, double Width, int FirstTile, int TileCount);

/// <summary>Where a tile sits inside its column, from the top: start and height as fractions of the ring height.</summary>
public readonly record struct MosaicSlot(double Top, double Height);

public sealed class MosaicLayout
{
    public required MosaicRing[] Rings { get; init; }

    public required MosaicColumn[] Columns { get; init; }

    public required MosaicSlot[] Slots { get; init; }

    /// <summary>The ring the sequence starts in.</summary>
    public required int StartRing { get; init; }
}

/// <summary>
/// The column mosaic: the sphere is cut into rings of equal height (the polar caps are the first and last ring), every
/// ring into columns, and a column holds one or more tiles stacked so that they fill the ring height exactly. All tiles
/// of a column share its width, ring height / Σ(1 / aspect), so any mix of shapes fits without a gap.
/// Tiles enter a column in sequence until it is narrow enough; a ring closes by scaling its columns a few percent.
/// </summary>
public static class Mosaic
{
    /// <summary>A column is closed once it is no wider than this many ring heights.</summary>
    public const double ColumnWidth = 1.2;

    /// <summary>At the end of a title a wider column may stay alone instead of taking a tile of the next title.</summary>
    public const double ClusterEndWidth = 2.0;

    public static MosaicLayout Build(IReadOnlyList<MosaicTile> tiles, string startAt)
    {
        // The ring count only comes in whole steps, so with few tiles the rings would have to stretch a lot. Trying a
        // few column widths around the usual one finds a total width that matches a ring count almost exactly.
        List<MosaicColumn> columns = null!;
        MosaicSlot[] slots = null!;
        List<double> widths = null!;
        var ringCount = 0;
        var bestError = double.MaxValue;
        for (var target = 1.0; target <= 1.5001; target += 0.05)
        {
            var tryColumns = BuildColumns(tiles, target, out var trySlots, out var tryWidths);
            var tryTotal = tryWidths.Sum();
            var tryRings = RingCount(tryTotal);
            var error = Math.Abs(Capacities(tryRings).Sum() / tryTotal - 1) + Math.Abs(target - ColumnWidth) * 0.02;
            if (error < bestError)
            {
                (bestError, columns, slots, widths, ringCount) = (error, tryColumns, trySlots, tryWidths, tryRings);
            }
        }

        var total = widths.Sum();
        var capacity = Capacities(ringCount);
        var order = RingOrder(ringCount, startAt);

        // Columns go to rings in visiting order: each ring gets the columns whose middle falls into its share of the
        // total width, so every ring is filled to about its own circumference.
        var capacityTotal = order.Sum(k => capacity[k]);
        var rings = new MosaicRing[ringCount];
        var boundaries = new double[order.Length];
        var running = 0.0;
        for (var i = 0; i < order.Length; i++)
        {
            running += capacity[order[i]];
            boundaries[i] = total * running / capacityTotal;
        }

        var placed = new MosaicColumn[columns.Count];
        var column = 0;
        var position = 0.0;
        for (var i = 0; i < order.Length; i++)
        {
            var first = column;
            var width = 0.0;
            while (column < columns.Count && position + widths[column] / 2 < boundaries[i] + 1e-9)
            {
                width += widths[column];
                position += widths[column];
                column++;
            }

            var start = 0.0;
            for (var c = first; c < column; c++)
            {
                placed[c] = columns[c] with { Start = start / width, Width = widths[c] / width };
                start += widths[c];
            }

            // The first column of the sequence sits centred in front when the globe starts on the equator. Every other
            // ring starts a little further round (steps of the golden ratio), so column edges never line up into a seam
            // from pole to pole.
            var offset = i == 0
                ? (startAt == "Front" && column > first ? -placed[first].Width / 2 : 0)
                : i * 0.6180339887498949 % 1;
            var k = order[i];
            rings[k] = new MosaicRing(offset, first, column - first, width > 0 ? capacity[k] / width : 1);
        }

        return new MosaicLayout { Rings = rings, Columns = placed, Slots = slots, StartRing = order[0] };
    }

    private static List<MosaicColumn> BuildColumns(IReadOnlyList<MosaicTile> tiles, double columnWidth, out MosaicSlot[] slots, out List<double> widths)
    {
        var columns = new List<MosaicColumn>();
        widths = new List<double>();
        slots = new MosaicSlot[tiles.Count];
        var i = 0;
        while (i < tiles.Count)
        {
            var first = i;
            var inverse = 1 / tiles[i].Aspect;
            i++;
            while (i < tiles.Count && 1 / inverse > columnWidth)
            {
                if (tiles[i].Cluster != tiles[i - 1].Cluster && 1 / inverse <= ClusterEndWidth)
                {
                    break;
                }

                inverse += 1 / tiles[i].Aspect;
                i++;
            }

            var top = 0.0;
            for (var t = first; t < i; t++)
            {
                var height = 1 / tiles[t].Aspect / inverse;
                slots[t] = new MosaicSlot(top, height);
                top += height;
            }

            columns.Add(new MosaicColumn(0, 0, first, i - first));
            widths.Add(1 / inverse);
        }

        return columns;
    }

    /// <summary>Circumference of each ring in ring heights (2 R cos latitude); the polar caps hold nothing.</summary>
    public static double[] Capacities(int ringCount)
    {
        var capacity = new double[ringCount];
        for (var k = 1; k < ringCount - 1; k++)
        {
            var latitude = -Math.PI / 2 + (k + 0.5) * Math.PI / ringCount;
            capacity[k] = 2 * ringCount * Math.Cos(latitude);
        }

        return capacity;
    }

    /// <summary>The number of rings (caps included) whose total circumference comes closest to the columns' width.</summary>
    public static int RingCount(double totalWidth) =>
        Enumerable.Range(3, 4000).MinBy(r => Math.Abs(Capacities(r).Sum() - totalWidth));

    /// <summary>In front on the equator: the middle ring, then alternately below and above. At the north pole: top down; at
    /// the south pole: bottom up.</summary>
    public static int[] RingOrder(int ringCount, string start)
    {
        if (start == "NorthPole")
        {
            return Enumerable.Range(1, ringCount - 2).Reverse().ToArray();
        }

        if (start == "SouthPole")
        {
            return Enumerable.Range(1, ringCount - 2).ToArray();
        }

        var middle = (ringCount - 1) / 2;
        var order = new List<int> { middle };
        for (var step = 1; order.Count < ringCount - 2; step++)
        {
            if (middle - step >= 1)
            {
                order.Add(middle - step);
            }

            if (middle + step <= ringCount - 2)
            {
                order.Add(middle + step);
            }
        }

        return order.ToArray();
    }
}
