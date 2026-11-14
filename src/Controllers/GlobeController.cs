using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
#if NET10_0_OR_GREATER
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
#else
using Jellyfin.Data.Entities;
#endif
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Globe.Configuration;
using Jellyfin.Plugin.Globe.Data;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.Globe.Controllers;

[ApiController]
[Route("Globe")]
public class GlobeController : ControllerBase
{
    private static readonly Regex DataFile = new("^[a-z0-9.-]+$", RegexOptions.Compiled);
    private static readonly Regex BackdropNumber = new("backdrop([0-9]+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly IUserManager _userManager;

    public GlobeController(IUserManager userManager)
    {
        _userManager = userManager;
    }

    private static GlobeService Service => GlobeService.Instance!;

    [HttpGet("script.js")]
    [AllowAnonymous]
    public ActionResult GetButtonScript() => Resource("globe-button.js", "text/javascript");

    [HttpGet("globe.js")]
    [AllowAnonymous]
    public ActionResult GetGlobeScript() => Resource("globe.js", "text/javascript");

    [HttpGet("vendor/three.module.js")]
    [AllowAnonymous]
    public ActionResult GetThree() => Resource("three.module.js", "text/javascript");

    [HttpGet("assets/jellyfin-logo.png")]
    [AllowAnonymous]
    public ActionResult GetLogo() => Resource("jellyfin-logo.png", "image/png");

    /// <summary>What the web client needs: the settings and the globes this user may see, each with its build state.</summary>
    [HttpGet("ClientConfiguration")]
    [Authorize]
    public ActionResult GetClientConfiguration()
    {
        var user = CurrentUser();
        if (user is null)
        {
            return Unauthorized();
        }

        var config = Plugin.Instance!.Configuration;
        var globes = GlobeService.Globes().Where(g => Service.MayView(g, user)).Select(g =>
        {
            var status = Service.Status(g.Id);
            return new
            {
                id = g.Id,
                name = g.Name,
                color = g.Color,
                startView = g.StartView,
                playJump = JumpActive(g),
                randomButton = g.RandomButton,
                state = status.State,
                percent = Math.Round(status.Percent),
                ready = Service.IsBuilt(g.Id),
            };
        });

        return Ok(new
        {
            actions = new
            {
                movie = config.MovieAction,
                noTrailer = config.NoTrailerAction,
                series = config.SeriesAction,
                season = config.SeasonAction,
                collection = config.CollectionAction,
                video = config.HomeVideoAction,
                photo = config.PhotoAction,
                game = config.GameAction,
            },
            playAnimated = config.PlayAnimatedArtwork,
            games = GamesLink.Present,
            openingGlobe = config.OpeningGlobe,
            isAdmin = user.HasPermission(PermissionKind.IsAdministrator),
            globes,
        });
    }

    /// <summary>A file of a built globe (layout, tables, atlas pages).</summary>
    [HttpGet("data/{id}/{file}")]
    [Authorize]
    public ActionResult GetData(string id, string file)
    {
        // images.json and animated-files.json hold paths of files on the server, only the server needs them.
        if (!DataFile.IsMatch(file) || file is "images.json" or "animated-files.json" || Viewable(id) is null)
        {
            return NotFound();
        }

        var info = new FileInfo(Path.Combine(Service.GlobeFolder(id), file));
        if (!info.Exists)
        {
            return NotFound();
        }

        // The data changes only when the globe is built again: revalidate with an ETag from time and size.
        Response.Headers.CacheControl = "no-cache";
        var tag = new EntityTagHeaderValue("\"" + info.LastWriteTimeUtc.Ticks.ToString("x") + "-" + info.Length.ToString("x") + "\"");
        var type = info.Extension == ".json" ? "application/json" : "application/octet-stream";
        return PhysicalFile(info.FullName, type, info.LastWriteTimeUtc, tag);
    }

    /// <summary>The sharp stage of sixteen neighbouring tiles, one after the other, read from the shared image store.</summary>
    [HttpGet("data/{id}/bundle/{index:int}")]
    [Authorize]
    public ActionResult GetBundle(string id, int index, [FromQuery] string? build)
    {
        if (Viewable(id) is null || index < 0)
        {
            return NotFound();
        }

        // Compacting renumbers the records; the stamp, the globe's numbers and the store are read on the same side of it.
        var store = Service.Store();
        using var output = new MemoryStream();
        using (store.Reading())
        {
            // Tile numbers mean something else after a rebuild, so a client still on the old layout gets nothing.
            if (build != Service.Fingerprint(id))
            {
                return Conflict();
            }

            var recordsFile = Path.Combine(Service.GlobeFolder(id), "records.bin");
            if (!System.IO.File.Exists(recordsFile))
            {
                return NotFound();
            }

            var table = System.IO.File.ReadAllBytes(recordsFile);
            var tiles = table.Length / 8;
            var first = index * GlobeBuilder.BundleSize;
            if (first >= tiles)
            {
                return NotFound();
            }

            for (var tile = first; tile < Math.Min(tiles, first + GlobeBuilder.BundleSize); tile++)
            {
                var kind = ArtworkKind.All[BitConverter.ToInt32(table, tile * 8)];
                var record = BitConverter.ToInt32(table, tile * 8 + 4);
                output.Write(store.ReadSharp(kind, record));
            }
        }

        Response.Headers.CacheControl = "no-cache";
        return File(output.ToArray(), "application/octet-stream");
    }

    /// <summary>The animated original behind a tile, for the overlay once the globe turns to it. The path comes from
    /// the globe's own list, never from the request.</summary>
    [HttpGet("data/{id}/original/{tile:int}")]
    [Authorize]
    public ActionResult GetOriginal(string id, int tile)
    {
        if (Viewable(id) is null)
        {
            return NotFound();
        }

        var list = Path.Combine(Service.GlobeFolder(id), "animated-files.json");
        if (!System.IO.File.Exists(list))
        {
            return NotFound();
        }

        var files = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(System.IO.File.ReadAllText(list));
        if (files is null || !files.TryGetValue(tile.ToString(System.Globalization.CultureInfo.InvariantCulture), out var file) || !System.IO.File.Exists(file))
        {
            return NotFound();
        }

        var type = Path.GetExtension(file).ToLowerInvariant() switch
        {
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".png" or ".apng" => "image/png",
            _ => "application/octet-stream",
        };
        Response.Headers.CacheControl = "no-cache";
        return PhysicalFile(file, type, true);
    }

    /// <summary>Play Jump: where the home video of a backdrop tile starts, as runtime x N / divisor with N the number in
    /// the backdrop's file name ("…-backdrop7.jpg"); 204 when this tile does not jump. Everything comes from the
    /// globe's own data, nothing but the tile number from the request.</summary>
    [HttpGet("data/{id}/jump/{tile:int}")]
    [Authorize]
    public ActionResult GetJump(string id, int tile, [FromQuery] string? build, [FromServices] ILibraryManager libraryManager)
    {
        var globe = Viewable(id);
        if (globe is null || !JumpActive(globe) || tile < 0)
        {
            return NoContent();
        }

        var folder = Service.GlobeFolder(id);
        var store = Service.Store();
        string? file;
        using (store.Reading())
        {
            // Tile numbers mean something else after a rebuild.
            if (build != Service.Fingerprint(id))
            {
                return NoContent();
            }

            var recordsFile = Path.Combine(folder, "records.bin");
            var table = System.IO.File.Exists(recordsFile) ? System.IO.File.ReadAllBytes(recordsFile) : Array.Empty<byte>();
            if ((long)(tile + 1) * 8 > table.Length)
            {
                return NoContent();
            }

            var kind = ArtworkKind.All[BitConverter.ToInt32(table, tile * 8)];
            if (kind.ImageType != MediaBrowser.Model.Entities.ImageType.Backdrop)
            {
                return NoContent();
            }

            file = store.FileOf(kind, BitConverter.ToInt32(table, tile * 8 + 4));
        }

        var match = file is null ? null : BackdropNumber.Match(Path.GetFileNameWithoutExtension(file));
        if (match is null || !match.Success || !int.TryParse(match.Groups[1].Value, out var number)
            || !Selection.InChoice(number, globe.JumpNumbers) || number >= globe.JumpDivisor)
        {
            return NoContent();
        }

        // The item of the tile, from the globe's tables: tiles.bin holds 16 floats per tile, the fourth is the item.
        using var tiles = System.IO.File.OpenRead(Path.Combine(folder, "tiles.bin"));
        var entry = new byte[4];
        tiles.Seek(((long)tile * 16 + 3) * 4, SeekOrigin.Begin);
        if (tiles.Read(entry, 0, 4) != 4)
        {
            return NoContent();
        }

        var itemIndex = (int)BitConverter.ToSingle(entry, 0);
        using var items = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(Path.Combine(folder, "items.json")));
        if (itemIndex < 0 || itemIndex >= items.RootElement.GetArrayLength()
            || !Guid.TryParse(items.RootElement[itemIndex].GetProperty("id").GetString(), out var itemId)
            || libraryManager.GetItemById(itemId) is not { } video
            || video.GetType() != typeof(MediaBrowser.Controller.Entities.Video) || video.RunTimeTicks is not > 0)
        {
            return NoContent();
        }

        return Ok(new { itemId = itemId.ToString("N"), ticks = video.RunTimeTicks.Value * number / globe.JumpDivisor });
    }

    /// <summary>Play Jump works only for home videos on Play with backdrops on the globe.</summary>
    private static bool JumpActive(GlobeDefinition globe) =>
        globe.PlayJump && globe.JumpDivisor >= 2 && Plugin.Instance!.Configuration.HomeVideoAction == "Play"
        && globe.ArtworkTypes.Contains("Backdrop", StringComparer.OrdinalIgnoreCase);

    /// <summary>Build state and sizes of every globe and of the store, for the admin page.</summary>
    [HttpGet("Status")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult GetStatus()
    {
        var (total, free) = Service.Ready ? Service.Store().Sizes() : (0, 0);
        var sizes = Service.Sizes();
        return Ok(new
        {
            defaultDataFolder = Plugin.Instance!.DefaultDataFolder,
            // The games library of Playnite Bridge, empty without the Bridge: the admin page offers game settings only then.
            gamesLibrary = GamesLink.LibraryId()?.ToString("N") ?? string.Empty,
            dataFolder = Service.DataFolder,
            folderProblem = Service.FolderProblem,
            oldDataFolder = Plugin.Instance.Configuration.OldDataFolder,
            hasSchedule = Service.HasSchedule(),
            storeBytes = total,
            freeBytes = free,
            globes = GlobeService.Globes().Select(g =>
            {
                var (all, own) = sizes.GetValueOrDefault(g.Id);
                return new { id = g.Id, status = Service.Status(g.Id), bytes = all, ownBytes = own, bridgeMissing = Service.BridgeMissing(g.Id) };
            }),
        });
    }

    /// <summary>Whether a folder can become the data folder: empty, or already Globe's data of this server.</summary>
    [HttpGet("CheckFolder")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult CheckFolder([FromQuery] string path)
    {
        var target = string.IsNullOrWhiteSpace(path) ? Plugin.Instance!.DefaultDataFolder : path.Trim();
        if (Service.DataFolder == target)
        {
            return Ok(new { problem = (string?)null });
        }

        var old = Plugin.Instance!.Configuration.OldDataFolder;
        var problem = Data.DataFolder.Problem(target, Service.ServerId, Service.Ready ? Service.DataFolder : null);
        if (problem is null && Service.Ready && !string.IsNullOrWhiteSpace(old) && !string.Equals(old.TrimEnd('\\', '/'), target.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
        {
            problem = "Delete the old data folder first, or go back to it.";
        }

        return Ok(new { problem });
    }

    [HttpPost("DeleteOldFolder")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult DeleteOldFolder() => Service.DeleteOldFolder() ? NoContent() : Conflict();

    [HttpPost("Compact")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult Compact([FromServices] MediaBrowser.Model.Tasks.ITaskManager taskManager)
    {
        taskManager.QueueScheduledTask<Tasks.CompactGlobeStorageTask>();
        return NoContent();
    }

    [HttpPost("Update/{id}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult Update(string id)
    {
        if (!GlobeService.Globes().Any(g => g.Id == id))
        {
            return NotFound();
        }

        Service.Update(id);
        return NoContent();
    }

    [HttpPost("Update")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult UpdateAll()
    {
        Service.UpdateAll();
        return NoContent();
    }

    private User? CurrentUser()
    {
        var claim = User.Claims.FirstOrDefault(c => c.Type == "Jellyfin-UserId")?.Value;
        return Guid.TryParse(claim, out var id) && id != Guid.Empty ? _userManager.GetUserById(id) : null;
    }

    private GlobeDefinition? Viewable(string id)
    {
        if (!GlobeService.ValidId(id) || !Service.Ready)
        {
            return null;
        }

        var user = CurrentUser();
        var globe = Plugin.Instance!.Configuration.Globes.FirstOrDefault(g => g.Id == id);
        return globe is not null && user is not null && Service.MayView(globe, user) ? globe : null;
    }

    private ActionResult Resource(string name, string contentType)
    {
        var stream = typeof(GlobeController).Assembly.GetManifestResourceStream(name);
        if (stream is null)
        {
            return NotFound();
        }

        // A new plugin build gets a new module id, so the browser asks and three.js comes again only after an update.
        Response.Headers.CacheControl = "no-cache";
        var tag = new EntityTagHeaderValue("\"" + typeof(GlobeController).Assembly.ManifestModule.ModuleVersionId.ToString("N") + "\"");
        return File(stream, contentType, null, tag);
    }
}
