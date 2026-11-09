using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Globe.Data;

/// <summary>
/// The grid for a globe whose tiles all have one shape: the sphere is cut into rings of equal height, and every ring into
/// as many places of that shape as fit, so every tile is the same size. The places are fixed before any picture is
/// placed. A sequence fills the rings in order; groups (the pictures of a title, or the titles of a folder) instead grow
/// as patches: every place of a group touches one it already has, and no free area is ever cut off.
/// </summary>
public static class GridMosaic
{
    /// <summary>How much an occupied neighbour pulls a place in, against its distance in ring heights.</summary>
    private const double NeighbourWeight = 1.5;

    /// <summary>A little chance in the choice of places, so patches do not grow in straight lines.</summary>
    private const double Chance = 0.4;

    /// <summary>
    /// Lays out <paramref name="count"/> tiles of one <paramref name="aspect"/>. With <paramref name="groups"/> (the group of
    /// each tile) the groups grow as patches, each starting beside the patch of its nearest placed parent
    /// (<paramref name="parentOf"/>, none: beside the first patch); without, the tiles fill the rings in order. Returns the
    /// layout and, for each tile position in it, the index of the tile in the given order.
    /// </summary>
    public static (MosaicLayout Layout, int[] Order) Build(
        int count, double aspect, string startAt, IReadOnlyList<string>? groups, Func<string, string?> parentOf)
    {
        var places = Places(count, aspect);
        var ringCount = places.Length;
        var visit = Mosaic.RingOrder(ringCount, startAt);
        // A ring near a pole can come out without a place; the sequence then starts in the next one.
        var firstRing = visit.First(k => places[k] > 0);
        var firstPlace = new int[ringCount];
        for (int k = 0, sum = 0; k < ringCount; k++)
        {
            firstPlace[k] = sum;
            sum += places[k];
        }

        // tileAt[place] = tile index, places counted ring by ring from the south.
        int[] tileAt;
        int startPlace;
        if (groups is null)
        {
            tileAt = new int[count];
            var next = 0;
            foreach (var k in visit)
            {
                for (var j = 0; j < places[k]; j++)
                {
                    tileAt[firstPlace[k] + j] = next++;
                }
            }

            startPlace = firstPlace[firstRing];
        }
        else
        {
            var grower = new Grower(places, firstPlace);
            startPlace = grower.Nearest(startAt is "NorthPole" or "SouthPole" ? firstRing : -1);
            tileAt = grower.Arrange(groups, parentOf, startPlace);
        }

        // Rings in visiting order, each place a column with one tile, the tiles in the order of their places.
        var rings = new MosaicRing[ringCount];
        var columns = new List<MosaicColumn>(count);
        var order = new int[count];
        var position = 0;
        foreach (var k in visit)
        {
            var n = places[k];
            // In a filled sequence the first place sits centred in front; patches keep every ring on the same origin.
            var offset = groups is null && k == firstRing ? -0.5 / n : 0;
            rings[k] = new MosaicRing(offset, columns.Count, n, 1);
            for (var j = 0; j < n; j++)
            {
                order[position] = tileAt[firstPlace[k] + j];
                columns.Add(new MosaicColumn((double)j / n, 1.0 / n, position, 1));
                position++;
            }
        }

        for (var k = 0; k < ringCount; k++)
        {
            if (places[k] == 0)
            {
                rings[k] = new MosaicRing(0, columns.Count, 0, 1);
            }
        }

        var slots = Enumerable.Repeat(new MosaicSlot(0, 1), count).ToArray();
        var layout = new MosaicLayout { Rings = rings, Columns = columns.ToArray(), Slots = slots, StartRing = RingOf(firstPlace, places, startPlace) };
        return (layout, order);
    }

    private static int RingOf(int[] firstPlace, int[] places, int place)
    {
        for (var k = 0; k < places.Length; k++)
        {
            if (place >= firstPlace[k] && place < firstPlace[k] + places[k])
            {
                return k;
            }
        }

        return 0;
    }

    /// <summary>
    /// The places per ring (the polar caps hold none): the ring count whose places of this shape come closest to the tile
    /// count, then each ring's share of the tiles by its area, the rest to the largest remainders.
    /// </summary>
    public static int[] Places(int count, double aspect)
    {
        static double[] Ideal(int ringCount, double aspect)
        {
            var h = Math.PI / ringCount;
            var ideal = new double[ringCount];
            for (var k = 1; k < ringCount - 1; k++)
            {
                var low = -Math.PI / 2 + k * h;
                ideal[k] = 2 * Math.PI * (Math.Sin(low + h) - Math.Sin(low)) / (aspect * h * h);
            }

            return ideal;
        }

        var ringCount = Enumerable.Range(3, 4000).MinBy(r => Math.Abs(Ideal(r, aspect).Sum() - count));
        var share = Ideal(ringCount, aspect);
        var total = share.Sum();
        var quota = share.Select(v => v * count / total).ToArray();
        var places = quota.Select(q => (int)Math.Floor(q)).ToArray();
        foreach (var k in Enumerable.Range(0, ringCount).OrderByDescending(k => quota[k] - places[k]).Take(count - places.Sum()))
        {
            places[k]++;
        }

        return places;
    }

    /// <summary>Lets groups grow as patches on the grid.</summary>
    private sealed class Grower
    {
        private const int Cap = -1;
        private readonly int[] _places;
        private readonly int[] _firstPlace;
        private readonly (int Place, bool Edge)[][] _around;
        private readonly int[][] _edgeNeighbours;
        private readonly bool[] _atCap;
        private readonly double[][] _centre;
        private readonly double _ringHeight;
        private readonly bool[] _occupied;
        private readonly HashSet<int> _border = new();
        private readonly Random _random = new(1);
        private int _free;

        public Grower(int[] places, int[] firstPlace)
        {
            _places = places;
            _firstPlace = firstPlace;
            var count = places.Sum();
            _ringHeight = Math.PI / places.Length;
            _around = new (int, bool)[count][];
            _centre = new double[count][];
            for (var k = 0; k < places.Length; k++)
            {
                var n = places[k];
                var latitude = -Math.PI / 2 + (k + 0.5) * _ringHeight;
                for (var j = 0; j < n; j++)
                {
                    var around = new List<(int, bool)> { (firstPlace[k] + ((j - 1 + n) % n), true) };
                    around.AddRange(Side(j, n, k + 1, false));
                    around.Add((firstPlace[k] + ((j + 1) % n), true));
                    around.AddRange(Side(j, n, k - 1, true));
                    _around[firstPlace[k] + j] = around.ToArray();
                    var longitude = 2 * Math.PI * (j + 0.5) / n;
                    _centre[firstPlace[k] + j] = new[]
                    {
                        Math.Cos(latitude) * Math.Sin(longitude), Math.Sin(latitude), Math.Cos(latitude) * Math.Cos(longitude),
                    };
                }
            }

            _edgeNeighbours = _around.Select(a => a.Where(x => x.Edge && x.Place != Cap).Select(x => x.Place).ToArray()).ToArray();
            _atCap = _around.Select(a => a.Any(x => x.Place == Cap)).ToArray();
            _occupied = new bool[count];
            _free = count;
        }

        /// <summary>The places of ring <paramref name="other"/> beside place j of a ring with n places, walking round it.</summary>
        private IEnumerable<(int Place, bool Edge)> Side(int j, int n, int other, bool reverse)
        {
            if (other < 0 || other >= _places.Length || _places[other] == 0)
            {
                return new[] { (Cap, true) };
            }

            var n2 = _places[other];
            long low = (long)j * n2, high = (long)(j + 1) * n2;
            long first = low / n, last = ((high + n - 1) / n) - 1;
            var sequence = new List<(int, bool)>();
            if (low % n == 0)
            {
                sequence.Add(((int)(first - 1), false));
            }

            for (var m = first; m <= last; m++)
            {
                sequence.Add(((int)m, true));
            }

            if (high % n == 0)
            {
                sequence.Add(((int)(last + 1), false));
            }

            var places = sequence.Select(s => (_firstPlace[other] + (((s.Item1 % n2) + n2) % n2), s.Item2)).ToList();
            if (reverse)
            {
                places.Reverse();
            }

            return places;
        }

        /// <summary>The place nearest the front of the equator, or nearest the front within ring <paramref name="ring"/>.</summary>
        public int Nearest(int ring)
        {
            var candidates = ring < 0
                ? Enumerable.Range(0, _occupied.Length)
                : Enumerable.Range(_firstPlace[ring], _places[ring]);
            return candidates.MaxBy(p => _centre[p][2]);
        }

        public int[] Arrange(IReadOnlyList<string> groups, Func<string, string?> parentOf, int start)
        {
            // Groups in the order their first tile comes; each group's tiles in their order.
            var members = new Dictionary<string, List<int>>();
            var order = new List<string>();
            for (var i = 0; i < groups.Count; i++)
            {
                if (!members.TryGetValue(groups[i], out var list))
                {
                    members[groups[i]] = list = new List<int>();
                    order.Add(groups[i]);
                }

                list.Add(i);
            }

            var tileAt = new int[_occupied.Length];
            var startOf = new Dictionary<string, int>();
            foreach (var group in order)
            {
                int? anchor = null;
                for (var parent = parentOf(group); parent is not null; parent = parentOf(parent))
                {
                    if (startOf.TryGetValue(parent, out var placed))
                    {
                        anchor = placed;
                        break;
                    }
                }

                var first = startOf.Count == 0 ? start : BestBorder(anchor ?? startOf.Values.First());
                Place(first);
                startOf[group] = first;
                tileAt[first] = members[group][0];
                var own = new HashSet<int>();
                foreach (var (place, _) in _around[first])
                {
                    if (place != Cap && !_occupied[place])
                    {
                        own.Add(place);
                    }
                }

                foreach (var tile in members[group].Skip(1))
                {
                    var best = -1;
                    var bestCost = double.MaxValue;
                    foreach (var place in own)
                    {
                        if (Allowed(place))
                        {
                            var cost = Cost(place, first);
                            if (cost < bestCost)
                            {
                                (best, bestCost) = (place, cost);
                            }
                        }
                    }

                    if (best < 0)
                    {
                        best = BestBorder(first);
                    }

                    Place(best);
                    tileAt[best] = tile;
                    own.Remove(best);
                    foreach (var (place, _) in _around[best])
                    {
                        if (place != Cap && !_occupied[place])
                        {
                            own.Add(place);
                        }
                    }
                }
            }

            return tileAt;
        }

        private void Place(int place)
        {
            _occupied[place] = true;
            _free--;
            _border.Remove(place);
            foreach (var (other, _) in _around[place])
            {
                if (other != Cap && !_occupied[other])
                {
                    _border.Add(other);
                }
            }
        }

        /// <summary>Among the twelve nearest free border places that may be taken, the cheapest; any free place as a last resort.</summary>
        private int BestBorder(int anchor)
        {
            var found = new List<int>(12);
            foreach (var place in _border.OrderBy(p => Distance(p, anchor)))
            {
                if (Allowed(place))
                {
                    found.Add(place);
                    if (found.Count == 12)
                    {
                        break;
                    }
                }
            }

            if (found.Count == 0)
            {
                return _border.Count > 0 ? _border.MinBy(p => Distance(p, anchor)) : Array.FindIndex(_occupied, o => !o);
            }

            return found.MinBy(p => Cost(p, anchor));
        }

        private double Distance(int a, int b)
        {
            var dot = (_centre[a][0] * _centre[b][0]) + (_centre[a][1] * _centre[b][1]) + (_centre[a][2] * _centre[b][2]);
            return Math.Acos(Math.Clamp(dot, -1, 1)) / _ringHeight;
        }

        private double Cost(int place, int anchor) =>
            Distance(place, anchor) - (NeighbourWeight * _edgeNeighbours[place].Count(x => _occupied[x])) + (_random.NextDouble() * Chance);

        private bool IsOccupied(int place) => place == Cap || _occupied[place];

        /// <summary>A free place that touches an occupied one, and whose free neighbours stay one piece without it.</summary>
        private bool Allowed(int place)
        {
            if (_occupied[place] || !_edgeNeighbours[place].Any(x => _occupied[x]))
            {
                return false;
            }

            if (_free == 1)
            {
                return true;
            }

            var runs = Runs(place);
            return runs == 1 || (_atCap[place] && runs > 1 && StillConnected(place));
        }

        /// <summary>How many separate stretches of free neighbours lie round the place.</summary>
        private int Runs(int place)
        {
            var around = _around[place];
            var free = around.Select(x => !IsOccupied(x.Place)).ToArray();
            if (!free.Any(f => f))
            {
                return 0;
            }

            if (free.All(f => f))
            {
                return 1;
            }

            var start = Array.IndexOf(free, false);
            int count = 0;
            bool inRun = false, edge = false;
            for (var i = 1; i <= around.Length; i++)
            {
                var index = (start + i) % around.Length;
                if (free[index])
                {
                    if (!inRun)
                    {
                        (inRun, edge) = (true, false);
                    }

                    edge |= around[index].Edge;
                }
                else if (inRun)
                {
                    count += edge ? 1 : 0;
                    inRun = false;
                }
            }

            return count + (inRun && edge ? 1 : 0);
        }

        /// <summary>Whether the free neighbours of a place next to a polar cap stay connected without it.</summary>
        private bool StillConnected(int place)
        {
            var targets = _edgeNeighbours[place].Where(x => !_occupied[x]).ToHashSet();
            if (targets.Count <= 1)
            {
                return true;
            }

            var first = targets.First();
            targets.Remove(first);
            var seen = new HashSet<int> { first, place };
            var queue = new Queue<int>();
            queue.Enqueue(first);
            while (queue.Count > 0 && targets.Count > 0)
            {
                foreach (var x in _edgeNeighbours[queue.Dequeue()])
                {
                    if (!_occupied[x] && seen.Add(x))
                    {
                        targets.Remove(x);
                        queue.Enqueue(x);
                    }
                }
            }

            return targets.Count == 0;
        }
    }
}
