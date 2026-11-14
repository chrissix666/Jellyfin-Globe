using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Globe.Data;

/// <summary>An image as the store knows it: its type, its file, the file's date and how it is prepared (black bars,
/// fit); the same file prepared another way is another record.</summary>
public sealed record StoreImage(ArtworkKind Kind, string File, DateTime Modified, string Treatment = "")
{
    public string Key => Kind.Index + "|" + File + "|" + Modified.Ticks + "|" + Treatment;
}

/// <summary>What a prepared picture remembers besides its pixels: whether the file is animated, which part of the
/// original it shows (x, y, width, height as shares of the original) and where that part sits in the tile.</summary>
public sealed record ImageInfo(bool Animated, float[] Source, float[] Target);

/// <summary>
/// The images of every globe, each prepared once: one file per artwork type with records of a fixed size
/// (sharp stage, small and tiny stage), and an index from image to record. Globes only point into it.
/// Records of images no globe uses any more are freed and reused.
/// </summary>
public sealed class ImageStore
{
    private readonly string _folder;
    private readonly object _lock = new();

    // Readers of sharp tiles hold this while they read a globe's records and the store; compacting swaps its files
    // and renumbers the globes alone, so nobody reads a new number from an old file or the other way round.
    private readonly ReaderWriterLockSlim _swap = new(LockRecursionPolicy.SupportsRecursion);
    private Dictionary<string, int> _records = new();
    private Dictionary<int, List<int>> _free = new();
    private Dictionary<int, int> _counts = new();
    private Dictionary<string, ImageInfo> _info = new();

    public ImageStore(string folder)
    {
        _folder = folder;
        Directory.CreateDirectory(folder);
        Load();
    }

    private sealed class IndexFile
    {
        public Dictionary<string, int> Records { get; set; } = new();

        public Dictionary<int, List<int>> Free { get; set; } = new();

        public Dictionary<int, int> Counts { get; set; } = new();

        public Dictionary<string, ImageInfo> Info { get; set; } = new();

        public int Generation { get; set; }
    }

    /// <summary>Rises when records move (compacting), so a client never reads a moved record under an old number.</summary>
    public int Generation { get; private set; }

    private string IndexPath => Path.Combine(_folder, "index.json");

    private string KindPath(ArtworkKind kind) => Path.Combine(_folder, kind.Name.ToLowerInvariant() + ".bin");

    public bool TryGetRecord(StoreImage image, out int record)
    {
        lock (_lock)
        {
            return _records.TryGetValue(image.Key, out record);
        }
    }

    /// <summary>The image file a record was made from, or null; the caller holds <see cref="Reading"/>.</summary>
    public string? FileOf(ArtworkKind kind, int record)
    {
        string? key;
        lock (_lock)
        {
            key = _records.FirstOrDefault(r => r.Value == record && KindOf(r.Key) == kind.Index).Key;
        }

        if (key is null)
        {
            return null;
        }

        // Key = kind|file|modified|treatment: cut from both ends, so a '|' inside the path does no harm.
        var end = key.LastIndexOf('|', key.LastIndexOf('|') - 1);
        var start = key.IndexOf('|', StringComparison.Ordinal) + 1;
        return end > start ? key[start..end] : null;
    }

    public ImageInfo? Info(string key)
    {
        lock (_lock)
        {
            return _info.GetValueOrDefault(key);
        }
    }

    /// <summary>Bytes of all record files and of the freed records in them, for the admin page.</summary>
    public (long Total, long Free) Sizes()
    {
        lock (_lock)
        {
            long total = 0, free = 0;
            foreach (var kind in ArtworkKind.All)
            {
                total += (long)_counts.GetValueOrDefault(kind.Index) * kind.RecordBytes;
                free += (long)FreeList(kind.Index).Count * kind.RecordBytes;
            }

            return (total, free);
        }
    }

    /// <summary>Bytes of the records behind <paramref name="keys"/>.</summary>
    public long Bytes(IEnumerable<string> keys)
    {
        lock (_lock)
        {
            return keys.Where(_records.ContainsKey).Sum(key => (long)ArtworkKind.All[KindOf(key)].RecordBytes);
        }
    }

    /// <summary>Prepares every image that is not in the store yet. Returns the images that could not be read.</summary>
    public HashSet<string> Add(IReadOnlyCollection<StoreImage> images, IProgress<double> progress, CancellationToken cancellationToken)
    {
        List<StoreImage> missing;
        lock (_lock)
        {
            missing = images.Where(i => !_records.ContainsKey(i.Key)).DistinctBy(i => i.Key).ToList();
        }

        var failed = new HashSet<string>();
        var done = 0;
        Parallel.ForEach(
            missing,
            new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4) },
            image =>
            {
                var prepared = ImagePreparation.MakeRecord(image.Kind, image.File, image.Treatment);
                lock (_lock)
                {
                    if (prepared is null)
                    {
                        failed.Add(image.Key);
                    }
                    else
                    {
                        Write(image, prepared.Record);
                        _info[image.Key] = prepared.Info;
                    }
                }

                progress.Report(100.0 * Interlocked.Increment(ref done) / missing.Count);
            });

        lock (_lock)
        {
            Save();
        }

        return failed;
    }

    /// <summary>Reads part of a record: the sharp stage, or the small or tiny RGBA pixels.</summary>
    public byte[] Read(ArtworkKind kind, int record, int offset, int length)
    {
        var bytes = new byte[length];
        using var file = new FileStream(KindPath(kind), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        file.Position = (long)record * kind.RecordBytes + offset;
        file.ReadExactly(bytes);
        return bytes;
    }

    public byte[] ReadSharp(ArtworkKind kind, int record) => Read(kind, record, 0, kind.SharpBytes);

    /// <summary>Held while a globe's record numbers and the store are read together.</summary>
    public IDisposable Reading()
    {
        _swap.EnterReadLock();
        return new ExitOnDispose(_swap.ExitReadLock);
    }

    private sealed class ExitOnDispose : IDisposable
    {
        private Action? _action;

        public ExitOnDispose(Action action) => _action = action;

        public void Dispose()
        {
            _action?.Invoke();
            _action = null;
        }
    }

    public byte[] ReadSmall(ArtworkKind kind, int record) => Read(kind, record, kind.SharpBytes, kind.SmallBytes);

    public byte[] ReadTiny(ArtworkKind kind, int record) => Read(kind, record, kind.SharpBytes + kind.SmallBytes, kind.TinyBytes);

    /// <summary>Frees the records of every image that is not in <paramref name="used"/>.</summary>
    public int Release(ISet<string> used)
    {
        lock (_lock)
        {
            var unused = _records.Keys.Where(k => !used.Contains(k)).ToList();
            foreach (var key in unused)
            {
                FreeList(KindOf(key)).Add(_records[key]);
                _records.Remove(key);
                _info.Remove(key);
            }

            var empty = _records.Count == 0;
            if (empty)
            {
                _free.Clear();
                _counts.Clear();
            }

            Save();
            if (empty)
            {
                RemoveRecordFiles();
            }

            return unused.Count;
        }
    }

    /// <summary>With no image left the record files go too: free records only help while the store is in use.</summary>
    private void RemoveRecordFiles()
    {
        try
        {
            foreach (var file in Directory.GetFiles(_folder, "*.bin"))
            {
                File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A client is still reading a file; the next globe simply writes over it.
        }
    }

    /// <summary>
    /// Copies the records still in use, without gaps, into new files, then swaps them in at once and lets the globes
    /// follow (<paramref name="follow"/> gets the old and new number of every moved record per kind and the new
    /// generation). Nobody reads tiles during the swap. It needs the room of the kept records once more for a moment;
    /// stopped before the swap, the old files stay as they were.
    /// </summary>
    public void Compact(Action<Dictionary<int, Dictionary<int, int>>, int> follow, CancellationToken cancellationToken)
    {
        // Builds and deletions wait for compacting, so the records stay as they are while the copies are written.
        List<(ArtworkKind Kind, List<KeyValuePair<string, int>> Kept)> work;
        lock (_lock)
        {
            work = ArtworkKind.All.Where(kind => FreeList(kind.Index).Count > 0)
                .Select(kind => (kind, _records.Where(r => KindOf(r.Key) == kind.Index).OrderBy(r => r.Value).ToList()))
                .ToList();
        }

        var maps = new List<(ArtworkKind Kind, List<KeyValuePair<string, int>> Kept, Dictionary<int, int> Map)>();
        try
        {
            foreach (var (kind, kept) in work)
            {
                var map = new Dictionary<int, int>();
                using (var input = new FileStream(KindPath(kind), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var output = new FileStream(KindPath(kind) + ".new", FileMode.Create, FileAccess.Write))
                {
                    var buffer = new byte[kind.RecordBytes];
                    foreach (var record in kept.Select(r => r.Value).Distinct())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        input.Position = (long)record * kind.RecordBytes;
                        input.ReadExactly(buffer);
                        output.Write(buffer);
                        map[record] = map.Count;
                    }
                }

                maps.Add((kind, kept, map));
            }
        }
        catch
        {
            foreach (var (kind, _) in work)
            {
                File.Delete(KindPath(kind) + ".new");
            }

            throw;
        }

        _swap.EnterWriteLock();
        try
        {
            lock (_lock)
            {
                // A file that cannot be swapped keeps its old records; the kinds swapped before it are followed all the same.
                var moves = new Dictionary<int, Dictionary<int, int>>();
                Exception? failure = null;
                foreach (var (kind, kept, map) in maps)
                {
                    try
                    {
                        File.Move(KindPath(kind) + ".new", KindPath(kind), true);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        failure = ex;
                        break;
                    }

                    foreach (var (key, record) in kept)
                    {
                        _records[key] = map[record];
                    }

                    _counts[kind.Index] = map.Count;
                    FreeList(kind.Index).Clear();
                    moves[kind.Index] = map;
                }

                Generation++;
                Save();
                follow(moves, Generation);
                foreach (var (kind, _, _) in maps.Where(m => !moves.ContainsKey(m.Kind.Index)))
                {
                    File.Delete(KindPath(kind) + ".new");
                }

                if (failure is not null)
                {
                    throw new IOException("Compacting stopped at a file that could not be replaced.", failure);
                }
            }
        }
        finally
        {
            _swap.ExitWriteLock();
        }
    }

    private static int KindOf(string key) =>
        int.Parse(key[..key.IndexOf('|', StringComparison.Ordinal)], System.Globalization.CultureInfo.InvariantCulture);

    private List<int> FreeList(int kind)
    {
        if (!_free.TryGetValue(kind, out var list))
        {
            _free[kind] = list = new List<int>();
        }

        return list;
    }

    private void Write(StoreImage image, byte[] record)
    {
        var free = FreeList(image.Kind.Index);
        int slot;
        if (free.Count > 0)
        {
            slot = free[^1];
            free.RemoveAt(free.Count - 1);
        }
        else
        {
            _counts.TryGetValue(image.Kind.Index, out slot);
            _counts[image.Kind.Index] = slot + 1;
        }

        using (var file = new FileStream(KindPath(image.Kind), FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite))
        {
            file.Position = (long)slot * image.Kind.RecordBytes;
            file.Write(record);
        }

        _records[image.Key] = slot;
    }

    private void Load()
    {
        if (!File.Exists(IndexPath))
        {
            return;
        }

        var index = JsonSerializer.Deserialize<IndexFile>(File.ReadAllText(IndexPath)) ?? new IndexFile();
        _records = index.Records;
        _free = index.Free;
        _counts = index.Counts;
        _info = index.Info;
        Generation = index.Generation;
    }

    private void Save()
    {
        var temporary = IndexPath + ".new";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new IndexFile { Records = _records, Free = _free, Counts = _counts, Info = _info, Generation = Generation }));
        File.Move(temporary, IndexPath, true);
    }
}
