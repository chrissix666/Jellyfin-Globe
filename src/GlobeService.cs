using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
#if NET10_0_OR_GREATER
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
#else
using Jellyfin.Data.Entities;
using Jellyfin.Data.Enums;
#endif
using Jellyfin.Plugin.Globe.Configuration;
using Jellyfin.Plugin.Globe.Data;
using Jellyfin.Plugin.Globe.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Globe;

/// <summary>Where a globe stands: waiting, building (with percent and phase), done, changed but not built, or failed.</summary>
public sealed class GlobeStatus
{
    public string State { get; set; } = "none";

    public double Percent { get; set; }

    public string Phase { get; set; } = string.Empty;

    public int Images { get; set; }

    public DateTime? BuiltAt { get; set; }

    public string Error { get; set; } = string.Empty;

    /// <summary>The settings changed since the last build, which waits for an update.</summary>
    public bool Changed { get; set; }
}

/// <summary>
/// Keeps the globes in step with the settings: builds new and changed globes one after the other, removes the data of
/// deleted ones, and knows the status of each. The builds run inside the scheduled task "Build Globe", so they show up
/// with progress in Jellyfin's task list; Globe never sets that task's schedule, the admin does.
/// </summary>
public sealed class GlobeService : IHostedService
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILocalizationManager _localization;
    private readonly ITaskManager _taskManager;
    private readonly ILogger<GlobeService> _logger;
    private readonly string _serverId;
    private readonly ConcurrentQueue<string> _queue = new();
    private readonly HashSet<string> _reshuffle = new();
    private readonly object _queueLock = new();
    private readonly ConcurrentDictionary<string, GlobeStatus> _status = new();
    private readonly ConcurrentDictionary<string, BuiltGlobe?> _built = new();
    private readonly SemaphoreSlim _work = new(1, 1);
    private readonly object _storeLock = new();
    private Dictionary<string, string> _known = new();
    private string _dataFolder = string.Empty;
    private ImageStore? _store;
    private CancellationTokenSource? _current;
    private string? _currentId;
    private bool _running;
    private bool _saving;
    private int _handStarts;

    private sealed record BuiltGlobe(string Fingerprint, string[] Libraries, string[] UnratedKinds, string Settings, int StoreGeneration, string MaxRating, string GamesLibrary);

    public GlobeService(ILibraryManager libraryManager, ILocalizationManager localization, ITaskManager taskManager, IServerApplicationHost host, ILogger<GlobeService> logger)
    {
        _libraryManager = libraryManager;
        _localization = localization;
        _taskManager = taskManager;
        _logger = logger;
        _serverId = host.SystemId;
        Instance = this;
    }

    public static GlobeService? Instance { get; private set; }

    public string DataFolder => _dataFolder;

    /// <summary>Why the configured data folder is not used, empty when it is.</summary>
    public string FolderProblem { get; private set; } = string.Empty;

    public string ServerId => _serverId;

    /// <summary>Whether a data folder is in use; without one nothing is built or shown and the admin page says why.</summary>
    public bool Ready => _dataFolder.Length > 0;

    /// <summary>A globe id is 32 lowercase hex digits; any other id (from an edited backup code, say) is never used as a
    /// folder name, so nothing outside Globe's own folders can be reached through it.</summary>
    public static bool ValidId(string? id) => id is { Length: 32 } && id.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <summary>The globes of the settings that can be built: those with a valid id.</summary>
    public static IEnumerable<GlobeDefinition> Globes() => Plugin.Instance!.Configuration.Globes.Where(g => ValidId(g.Id));

    public string GlobeFolder(string id) => ValidId(id) && Ready
        ? Path.Combine(_dataFolder, "globes", id)
        : throw new InvalidOperationException(Ready ? "Not a globe id." : FolderProblem);

    /// <summary>Whether a globe has built data the client can open.</summary>
    public bool IsBuilt(string id) => Ready && ValidId(id) && File.Exists(Path.Combine(GlobeFolder(id), "layout.json"));

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance!;
        UseFolder(plugin.ResolveDataFolder());
        _known = Globes().ToDictionary(g => g.Id, Settings);
        foreach (var globe in Globes())
        {
            var status = ReadStatus(globe.Id);
            status.Changed = status.State == "done" && Built(globe.Id)?.Settings != Settings(globe);
            _status[globe.Id] = status;
        }

        plugin.ConfigurationChanged += OnConfigurationChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance is not null)
        {
            Plugin.Instance.ConfigurationChanged -= OnConfigurationChanged;
        }

        _queue.Clear();
        _current?.Cancel();
        return Task.CompletedTask;
    }

    public GlobeStatus Status(string id) => _status.GetOrAdd(id, _ => new GlobeStatus());

    /// <summary>An update by hand: the globe is built if something changed, a random one with "Always" is shuffled anew.</summary>
    public void Update(string id)
    {
        var globe = Globes().FirstOrDefault(g => g.Id == id);
        if (globe is not null)
        {
            Enqueue(id, false, IsRandom(globe) && globe.Reshuffle == "Always");
        }
    }

    public void UpdateAll()
    {
        foreach (var globe in Globes())
        {
            Update(globe.Id);
        }
    }

    /// <summary>Puts a globe in the queue (a globe already waiting is not queued twice) and starts the task, unless a run
    /// is under way: that run takes the globe too.</summary>
    private void Enqueue(string id, bool changed, bool reshuffle)
    {
        bool start;
        lock (_queueLock)
        {
            // Changed settings end a build of this globe that is under way, it would finish with the old ones.
            if (changed && _currentId == id)
            {
                _current?.Cancel();
            }

            if (!_queue.Contains(id) && (changed || _currentId != id))
            {
                _queue.Enqueue(id);
                Status(id).State = "waiting";
            }

            if (reshuffle)
            {
                _reshuffle.Add(id);
            }

            start = !_running;
            _running = true;
            if (start)
            {
                _handStarts++;
            }
        }

        if (start)
        {
            _taskManager.QueueScheduledTask<BuildGlobeTask>();
        }
    }

    /// <summary>
    /// Whether a user may see a globe: every library of its settings and of its built data must be visible to them; a
    /// user with tag rules sees no globe; a user with an age limit only globes with a limit not above it; a user who blocks
    /// unrated titles of a kind only globes without unrated titles of that kind.
    /// </summary>
    public bool MayView(GlobeDefinition globe, User user)
    {
        if (globe.Libraries.Length == 0
            || user.GetPreference(PreferenceKind.BlockedTags).Length > 0
            || user.GetPreference(PreferenceKind.AllowedTags).Length > 0)
        {
            return false;
        }

        // The limit of the settings and the one the data was built with both count: until a lowered limit is built, the
        // old tiles are still there.
        var built = Built(globe.Id);
        var userLimit = UserLimit(user);
        if (userLimit is not null
            && !(WithinLimit(globe.MaxRating, userLimit.Value) && (built is null || WithinLimit(built.MaxRating, userLimit.Value))))
        {
            return false;
        }

        var blocked = user.GetPreference(PreferenceKind.BlockUnratedItems);
        if (built is not null && built.UnratedKinds.Any(kind => blocked.Contains(kind, StringComparer.OrdinalIgnoreCase)))
        {
            return false;
        }

        return globe.Libraries.All(id => Guid.TryParse(id, out var guid) && _libraryManager.GetItemById(guid) is Folder folder && folder.IsVisible(user))
            && (built?.Libraries ?? Array.Empty<string>()).All(id =>
                !Guid.TryParse(id, out var guid) || _libraryManager.GetItemById(guid) is not Folder folder || folder.IsVisible(user));
    }

    private bool WithinLimit(string rating, (int Score, int Sub) userLimit)
    {
        var limit = string.IsNullOrWhiteSpace(rating) ? null : Ratings.Score(rating, _localization, null);
        return limit is not null && Ratings.AtMost(limit.Value, userLimit);
    }

    private static (int Score, int Sub)? UserLimit(User user) =>
#if NET10_0_OR_GREATER
        user.MaxParentalRatingScore is int score ? (score, user.MaxParentalRatingSubScore ?? 0) : null;
#else
        user.MaxParentalAgeRating is int level ? (level, 0) : null;
#endif

    /// <summary>What a client sends with every bundle request: the build and the store generation it read the tiles from.</summary>
    public string? Fingerprint(string id) => Built(id) is { } built ? built.Fingerprint + ":" + built.StoreGeneration : null;

    private BuiltGlobe? Built(string id) => Ready && ValidId(id) ? _built.GetOrAdd(id, ReadBuilt) : null;

    /// <summary>The games library a globe uses: the Bridge's while it is there, else the one of the last build, so a globe
    /// keeps knowing it held games while the Bridge is away.</summary>
    private string GamesLibraryOf(GlobeDefinition globe)
    {
        var games = GamesLink.LibraryId()?.ToString("N") ?? Built(globe.Id)?.GamesLibrary ?? string.Empty;
        return games.Length > 0 && globe.Libraries.Any(id => Guid.TryParse(id, out var library) && library.ToString("N") == games) ? games : string.Empty;
    }

    /// <summary>The name of a library of the globe none of whose folders exists, or null when all can be reached.</summary>
    private string? UnreachableLibrary(GlobeDefinition globe, string gamesLibrary)
    {
        // The folders as Jellyfin's library list holds them; the library item itself may not know them yet.
        var chosen = globe.Libraries.Select(id => Guid.TryParse(id, out var guid) ? guid : Guid.Empty).ToHashSet();
        return _libraryManager.GetVirtualFolders()
            .Where(library => Guid.TryParse(library.ItemId, out var guid) && chosen.Contains(guid) && guid.ToString("N") != gamesLibrary)
            .FirstOrDefault(library => library.Locations is { Length: > 0 } locations && !locations.Any(Directory.Exists))?.Name;
    }

    /// <summary>A globe with games whose Playnite Bridge is not installed: it stays as built and says so.</summary>
    public bool BridgeMissing(string id) =>
        !GamesLink.Present && Globes().FirstOrDefault(g => g.Id == id) is { } globe && GamesLibraryOf(globe).Length > 0;

    private static bool IsRandom(GlobeDefinition globe) => string.Equals(globe.Sort, "Random", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Runs the queue; called by the task. A run that finds the queue empty is the automatic update (its schedule or a
    /// start from the task list): it checks the globes with "update automatically" ticked and shuffles their random ones
    /// as their setting says. Globes queued by hand are built first.
    /// </summary>
    public void RunQueue(IProgress<double> progress, CancellationToken cancellationToken)
    {
        try
        {
            _work.Wait(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            EndRun();
            throw;
        }

        try
        {
            bool automatic;
            lock (_queueLock)
            {
                _running = true;
                // A run that no update by hand started is the automatic update; it checks the ticked globes after the
                // ones that wait.
                automatic = _queue.IsEmpty || _handStarts == 0;
                _handStarts = Math.Max(0, _handStarts - 1);
                if (automatic)
                {
                    foreach (var globe in Globes().Where(g => g.AutoUpdate && !_queue.Contains(g.Id)))
                    {
                        _queue.Enqueue(globe.Id);
                        Status(globe.Id).State = "waiting";
                        if (IsRandom(globe) && globe.Reshuffle != "Never")
                        {
                            _reshuffle.Add(globe.Id);
                        }
                    }
                }
            }

            var total = Math.Max(1, _queue.Count);
            var done = 0;
            while (NextInQueue(out var id))
            {
                var globe = Globes().FirstOrDefault(g => g.Id == id);
                if (globe is null)
                {
                    continue;
                }

                bool reshuffle;
                lock (_queueLock)
                {
                    reshuffle = _reshuffle.Remove(id);
                }

                Build(globe, reshuffle, done, total, progress, cancellationToken);
                done++;
            }

            if (!Ready)
            {
                return;
            }

            ReleaseUnusedImages();
            progress.Report(100);

            // With "compact on the automatic update" the store is compacted once a quarter of it is free.
            var (stored, free) = Store().Sizes();
            if (automatic && Plugin.Instance!.Configuration.CompactOnUpdate && stored > 0 && free * 4 > stored)
            {
                _taskManager.QueueScheduledTask<CompactGlobeStorageTask>();
            }
        }
        finally
        {
            _work.Release();
        }
    }

    private void Build(GlobeDefinition globe, bool reshuffle, int done, int total, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var id = globe.Id;
        var gamesLibrary = GamesLibraryOf(globe);
        // Right after a server start the Bridge needs a few seconds before it has games; without them in two minutes the
        // globe keeps its last build and the next run tries again.
        for (var wait = 0; gamesLibrary.Length > 0 && GamesLink.Present && !GamesLink.Ready() && wait < 24; wait++)
        {
            cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        if (gamesLibrary.Length > 0 && GamesLink.Present && !GamesLink.Ready())
        {
            _logger.LogInformation("Globe: \"{Name}\" kept as it is, Playnite Bridge has no games yet", globe.Name);
            Status(id).State = IsBuilt(id) ? "done" : "none";
            return;
        }

        // A library whose folders cannot be reached (a disk asleep or unplugged) would leave the globe empty; the globe
        // keeps its last build instead and the next run tries again.
        if (UnreachableLibrary(globe, gamesLibrary) is { } unreachable)
        {
            _logger.LogWarning("Globe: \"{Name}\" kept as it is, the folders of library \"{Library}\" cannot be reached", globe.Name, unreachable);
            var kept = Status(id);
            kept.State = "error";
            kept.Error = $"the folders of library \"{unreachable}\" cannot be reached, the globe keeps its last build";
            return;
        }

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _current = cancel;
        _currentId = id;
        var status = Status(id);
        status.State = "building";
        status.Error = string.Empty;
        try
        {
            var random = new Random();
            var titles = Selection.Load(globe, _libraryManager, _localization, random);
            var result = GlobeBuilder.Build(globe, titles, Store(), GlobeFolder(id), random, Settings(globe), reshuffle, gamesLibrary, (percent, phase) =>
            {
                status.Percent = percent;
                status.Phase = phase;
                progress.Report((done + percent / 100) * 100 / total);
            }, cancel.Token);

            _built.TryRemove(id, out _);
            var built = ReadStatus(id);
            status.State = result.Outcome == BuildOutcome.Empty ? "empty" : "done";
            status.Images = built.Images;
            status.BuiltAt = built.BuiltAt;
            status.Changed = false;
            _logger.LogInformation("Globe: \"{Name}\" {Outcome}, {Images} images, {Unreadable} unreadable", globe.Name, result.Outcome, result.Images, result.Unreadable);
        }
        catch (OperationCanceledException)
        {
            status.State = IsBuilt(id) ? "done" : "none";
            RemoveLeftovers(id);
            if (cancellationToken.IsCancellationRequested)
            {
                EndRun();
                throw;
            }
        }
        catch (Exception ex)
        {
            status.State = "error";
            status.Error = ex.Message;
            RemoveLeftovers(id);
            _logger.LogError(ex, "Globe: building \"{Name}\" failed", globe.Name);
        }
        finally
        {
            _current = null;
            _currentId = null;
            status.Percent = 0;
            status.Phase = string.Empty;
        }
    }

    /// <summary>
    /// Compacts the image store (task "Compact Globe Storage"). It waits for builds and builds wait for it;
    /// the globes keep their layout and shuffle and only follow the moved records, and clients still on the old numbers
    /// are refused until they open the globe again.
    /// </summary>
    public void Compact(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _work.Wait(cancellationToken);
        try
        {
            if (!Ready)
            {
                return;
            }

            var store = Store();
            var (total, free) = store.Sizes();
            var room = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_dataFolder))!).AvailableFreeSpace;
            if (free == 0 || room < total - free)
            {
                _logger.LogInformation("Globe: nothing to compact or not enough free space ({Room} bytes free, {Needed} needed)", room, total - free);
                return;
            }

            progress.Report(5);
            store.Compact(
                (moves, generation) =>
                {
                    foreach (var globe in Globes())
                    {
                        FollowMoves(GlobeFolder(globe.Id), moves, generation);
                    }

                    _built.Clear();
                },
                cancellationToken);
            progress.Report(100);
            _logger.LogInformation("Globe: image store compacted, {Freed} bytes freed", free);
        }
        finally
        {
            _work.Release();
        }
    }

    private static void FollowMoves(string folder, Dictionary<int, Dictionary<int, int>> moves, int generation)
    {
        var recordsFile = Path.Combine(folder, "records.bin");
        var layoutFile = Path.Combine(folder, "layout.json");
        if (!File.Exists(recordsFile) || !File.Exists(layoutFile))
        {
            return;
        }

        var table = File.ReadAllBytes(recordsFile);
        for (var at = 0; at + 8 <= table.Length; at += 8)
        {
            var kind = BitConverter.ToInt32(table, at);
            if (moves.TryGetValue(kind, out var map) && map.TryGetValue(BitConverter.ToInt32(table, at + 4), out var moved))
            {
                BitConverter.GetBytes(moved).CopyTo(table, at + 4);
            }
        }

        WriteWhole(recordsFile, table);
        var layout = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(layoutFile))!;
        layout["storeGeneration"] = JsonSerializer.SerializeToElement(generation);
        WriteWhole(layoutFile, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(layout)));
    }

    /// <summary>Writes a file beside its place first, so a stop half way leaves the old one whole.</summary>
    private static void WriteWhole(string file, byte[] bytes)
    {
        File.WriteAllBytes(file + ".new", bytes);
        File.Move(file + ".new", file, true);
    }

    /// <summary>A run cancelled from the task list takes what still waits with it.</summary>
    private void EndRun()
    {
        lock (_queueLock)
        {
            while (_queue.TryDequeue(out var waiting))
            {
                Status(waiting).State = IsBuilt(waiting) ? "done" : "none";
            }

            _reshuffle.Clear();
            _running = false;
        }
    }

    private void RemoveLeftovers(string id)
    {
        if (!Ready)
        {
            return;
        }

        try
        {
            if (Directory.Exists(GlobeFolder(id) + ".building"))
            {
                Directory.Delete(GlobeFolder(id) + ".building", true);
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Globe: could not remove the rest of an unfinished build");
        }
    }

    private bool NextInQueue(out string id)
    {
        lock (_queueLock)
        {
            _running = _queue.TryDequeue(out id!);
            return _running;
        }
    }

    public ImageStore Store()
    {
        lock (_storeLock)
        {
            return Ready ? _store ??= new ImageStore(Path.Combine(_dataFolder, "store")) : throw new InvalidOperationException(FolderProblem);
        }
    }

    /// <summary>Bytes of each globe's images and of those no other globe uses, for the admin page; every globe's list
    /// is read once.</summary>
    public Dictionary<string, (long All, long Own)> Sizes()
    {
        var sizes = new Dictionary<string, (long All, long Own)>();
        if (!Ready)
        {
            return sizes;
        }

        var used = Globes().ToDictionary(g => g.Id, g => GlobeBuilder.UsedImages(GlobeFolder(g.Id)).ToHashSet());
        var users = new Dictionary<string, int>();
        foreach (var key in used.Values.SelectMany(keys => keys))
        {
            users[key] = users.GetValueOrDefault(key) + 1;
        }

        var store = Store();
        foreach (var (id, keys) in used)
        {
            var folder = new DirectoryInfo(GlobeFolder(id));
            var own = folder.Exists ? folder.EnumerateFiles().Sum(f => f.Length) : 0;
            sizes[id] = (store.Bytes(keys) + own, store.Bytes(keys.Where(k => users[k] == 1)) + own);
        }

        return sizes;
    }

    /// <summary>Deletes Globe's data in the folder used before; only what Globe made there.</summary>
    public bool DeleteOldFolder()
    {
        var config = Plugin.Instance!.Configuration;
        if (string.IsNullOrWhiteSpace(config.OldDataFolder) || !DeleteOwnData(config.OldDataFolder))
        {
            return false;
        }

        config.OldDataFolder = string.Empty;
        Save(config);
        return true;
    }

    private bool DeleteOwnData(string path)
    {
        try
        {
            return Data.DataFolder.DeleteOwn(path, _serverId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Globe: could not delete the old data folder");
            return false;
        }
    }

    /// <summary>Settings that change what a globe is built from; name, colour, the tick and the shuffle setting do not.</summary>
    private static string Settings(GlobeDefinition g) => string.Join('|',
        string.Join(',', g.Libraries), string.Join(',', g.Folders), g.FolderMode, g.Media, string.Join(',', g.ArtworkTypes),
        g.IncludeChildren, g.CollectionTitlesAlone, g.Backdrops, g.Sort, g.Descending, g.Start, g.Cluster,
        g.MaxRating, g.LeaveOutUnrated, g.BlackBars, g.PictureFit, g.CropLimit, g.CoverFit, g.Versions, g.KeepVersionsTogether, g.Discs);

    /// <summary>Takes a data folder when it is empty or already Globe's; otherwise the folder in use stays.</summary>
    private bool UseFolder(string folder)
    {
        var problem = Data.DataFolder.Problem(folder, _serverId, null);
        if (problem is not null)
        {
            FolderProblem = problem;
            _logger.LogWarning("Globe: data folder {Folder} refused: {Problem}", folder, problem);
            return false;
        }

        try
        {
            Data.DataFolder.Mark(folder, _serverId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A drive that is not there (an unplugged disk, say): nothing is built until the folder can be used.
            FolderProblem = "The data folder cannot be used: " + ex.Message;
            _logger.LogWarning(ex, "Globe: data folder {Folder} cannot be used", folder);
            return false;
        }

        lock (_storeLock)
        {
            _dataFolder = folder;
            _store = null;
        }

        FolderProblem = string.Empty;
        _built.Clear();
        return true;
    }

    private void OnConfigurationChanged(object? sender, BasePluginConfiguration configuration)
    {
        if (_saving)
        {
            return;
        }

        var config = (PluginConfiguration)configuration;
        var folder = Plugin.Instance!.ResolveDataFolder();
        var folderChanged = !Ready || !SamePath(folder, _dataFolder);
        if (folderChanged)
        {
            var old = _dataFolder;
            var problem = Ready ? Data.DataFolder.Problem(folder, _serverId, old) : null;
            // One old folder at a time: a second change waits until the first old folder is deleted, or goes back to it.
            if (problem is null && Ready && !string.IsNullOrWhiteSpace(config.OldDataFolder) && !SamePath(folder, config.OldDataFolder))
            {
                problem = "Delete the old data folder first, or go back to it.";
            }

            if (problem is not null || !UseFolder(folder))
            {
                FolderProblem = problem ?? FolderProblem;
                folderChanged = false;
            }
            else if (old.Length > 0)
            {
                // The old folder stays until the admin deletes it; the globes are built in the new one.
                config.OldDataFolder = old;
                Save(config);
            }
        }

        var now = config.Globes.Where(g => ValidId(g.Id)).ToDictionary(g => g.Id, Settings);
        foreach (var removed in _known.Keys.Except(now.Keys).ToList())
        {
            Remove(removed);
        }

        foreach (var (id, settings) in now)
        {
            if (folderChanged || !_known.TryGetValue(id, out var before) || before != settings)
            {
                Enqueue(id, true, false);
            }
        }

        _known = now;
    }

    private static bool SamePath(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }

        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private void Save(PluginConfiguration config)
    {
        _saving = true;
        try
        {
            Plugin.Instance!.UpdateConfiguration(config);
        }
        finally
        {
            _saving = false;
        }
    }

    private void Remove(string id)
    {
        if (_currentId == id)
        {
            _current?.Cancel();
        }

        _status.TryRemove(id, out _);
        _built.TryRemove(id, out _);
        if (!Ready)
        {
            return;
        }

        // Deleting waits for a build that runs, so its swap cannot bring the folder back afterwards.
        var folder = GlobeFolder(id);
        Task.Run(() =>
        {
            _work.Wait();
            try
            {
                foreach (var path in new[] { folder, folder + ".building", folder + ".old" })
                {
                    if (Directory.Exists(path))
                    {
                        Directory.Delete(path, true);
                    }
                }

                // During a run the images stay: a build has added some that no globe lists yet, and the run frees them
                // at its end.
                bool idle;
                lock (_queueLock)
                {
                    idle = !_running;
                }

                if (idle)
                {
                    ReleaseUnusedImages();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Globe: could not remove the data of a deleted globe");
            }
            finally
            {
                _work.Release();
            }
        });
    }

    private void ReleaseUnusedImages()
    {
        var used = new HashSet<string>(Globes().SelectMany(g => GlobeBuilder.UsedImages(GlobeFolder(g.Id))));
        var freed = Store().Release(used);
        if (freed > 0)
        {
            _logger.LogInformation("Globe: {Count} images no globe uses any more were freed", freed);
        }
    }

    /// <summary>Whether Jellyfin's task list has a schedule for "Build Globe"; only read, Globe never sets one.</summary>
    public bool HasSchedule() =>
        _taskManager.ScheduledTasks.FirstOrDefault(t => t.ScheduledTask is BuildGlobeTask)?.Triggers.Count > 0;

    private BuiltGlobe? ReadBuilt(string id)
    {
        var file = Path.Combine(GlobeFolder(id), "layout.json");
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }

            using var layout = JsonDocument.Parse(File.ReadAllText(file));
            var root = layout.RootElement;
            string[] Strings(string name) => root.TryGetProperty(name, out var list)
                ? list.EnumerateArray().Select(l => l.GetString() ?? string.Empty).ToArray()
                : Array.Empty<string>();
            return new BuiltGlobe(
                root.GetProperty("fingerprint").GetString() ?? string.Empty,
                Strings("libraries"),
                Strings("unratedKinds"),
                root.TryGetProperty("settings", out var settings) ? settings.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("storeGeneration", out var generation) ? generation.GetInt32() : 0,
                root.TryGetProperty("maxRating", out var maxRating) ? maxRating.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("gamesLibrary", out var games) ? games.GetString() ?? string.Empty : string.Empty);
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private GlobeStatus ReadStatus(string id)
    {
        var status = new GlobeStatus();
        if (!IsBuilt(id))
        {
            return status;
        }

        var file = Path.Combine(GlobeFolder(id), "layout.json");
        if (!File.Exists(file))
        {
            return status;
        }

        try
        {
            using var layout = JsonDocument.Parse(File.ReadAllText(file));
            status.State = "done";
            status.Images = layout.RootElement.GetProperty("images").GetInt32();
            status.BuiltAt = layout.RootElement.GetProperty("builtAt").GetDateTime();
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException)
        {
            status.State = "error";
        }

        return status;
    }
}
