using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Globe.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>What a double-click on a movie does: "Details", "Play" or "Trailer".</summary>
    public string MovieAction { get; set; } = "Details";

    /// <summary>With "Trailer" and a movie without any trailer: "Details" or "Play".</summary>
    public string NoTrailerAction { get; set; } = "Details";

    /// <summary>Series, seasons, collections and home videos: "Details" or "Play".</summary>
    public string SeriesAction { get; set; } = "Details";

    public string SeasonAction { get; set; } = "Details";

    public string CollectionAction { get; set; } = "Details";

    public string HomeVideoAction { get; set; } = "Details";

    /// <summary>Photos: "Details" or "Viewer" (Jellyfin's photo player).</summary>
    public string PhotoAction { get; set; } = "Viewer";

    /// <summary>Games of Playnite Bridge: "Details", "Play" or "OpenInPlaynite".</summary>
    public string GameAction { get; set; } = "Details";

    /// <summary>A tile whose picture is animated plays the original once the globe turns to it.</summary>
    public bool PlayAnimatedArtwork { get; set; } = true;

    /// <summary>The globe that opens first: empty for the one used last, else a globe id.</summary>
    public string OpeningGlobe { get; set; } = string.Empty;

    /// <summary>Where the globe data lives; empty for Jellyfin's metadata folder + "Globe".</summary>
    public string DataFolder { get; set; } = string.Empty;

    /// <summary>The automatic update also compacts the image store once a quarter of it is free.</summary>
    public bool CompactOnUpdate { get; set; }

    /// <summary>A data folder used before, kept until the admin deletes it.</summary>
    public string OldDataFolder { get; set; } = string.Empty;

    public GlobeDefinition[] Globes { get; set; } = Array.Empty<GlobeDefinition>();
}

/// <summary>One globe as the admin put it together.</summary>
public class GlobeDefinition
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Library ids (Jellyfin's virtual folder ItemId).</summary>
    public string[] Libraries { get; set; } = Array.Empty<string>();

    /// <summary>Folders inside home video libraries as "libraryId:folderId".</summary>
    public string[] Folders { get; set; } = Array.Empty<string>();

    /// <summary>"Only": the ticked folders and everything below them; "Except": the whole library without them.</summary>
    public string FolderMode { get; set; } = "Only";

    /// <summary>Home video libraries: "Both", "Videos" or "Photos".</summary>
    public string Media { get; set; } = "Both";

    /// <summary>Artwork types: Poster, Backdrop, Thumb, Logo, Clearart, Banner, Disc.</summary>
    public string[] ArtworkTypes { get; set; } = { "Poster" };

    /// <summary>Seasons of series and movies of collections join their title.</summary>
    public bool IncludeChildren { get; set; }

    /// <summary>With children: a movie of a collection also stands on its own.</summary>
    public bool CollectionTitlesAlone { get; set; }

    /// <summary>Which backdrops of a title, by number in Jellyfin's list (1 = the main one): "1", "2-" (all but the main
    /// one), "2-5,8".</summary>
    public string Backdrops { get; set; } = "1";

    /// <summary>"Random" or one of Jellyfin's sort fields (SortName, CommunityRating, CriticRating, DateCreated,
    /// OfficialRating, PremiereDate, Runtime, DateLastContentAdded).</summary>
    public string Sort { get; set; } = "SortName";

    public bool Descending { get; set; }

    /// <summary>Where the order begins: "Front" (in front on the equator), "NorthPole" or "SouthPole".</summary>
    public string Start { get; set; } = "Front";

    /// <summary>How the globe is seen on opening: "Side", "FirstTitle", "Above" or "Below".</summary>
    public string StartView { get; set; } = "Side";

    /// <summary>With Random: keep the tiles of a title together.</summary>
    public bool Cluster { get; set; } = true;

    /// <summary>With Random: "Never", "Automatic" (on the automatic update) or "Always" (on every update).</summary>
    public string Reshuffle { get; set; } = "Automatic";

    /// <summary>The highest parental rating as Jellyfin names it ("DE-12"); empty for no limit.</summary>
    public string MaxRating { get; set; } = string.Empty;

    /// <summary>Titles without a rating Jellyfin knows stay out.</summary>
    public bool LeaveOutUnrated { get; set; }

    /// <summary>Burned-in black bars are removed from every opaque picture.</summary>
    public bool BlackBars { get; set; } = true;

    /// <summary>Photos and home videos: "Crop", "OwnShape", "BlurFill" or "Adaptive".</summary>
    public string PictureFit { get; set; } = "Adaptive";

    /// <summary>Adaptive crops a picture that loses at most this share (percent) and fills any other.</summary>
    public int CropLimit { get; set; } = 15;

    /// <summary>Game covers: "Crop" (cut to 2:3), "OwnShape", "BlurFill" or "Adaptive".</summary>
    public string CoverFit { get; set; } = "Crop";

    /// <summary>Games: "Main" (the main version of each game) or "All" (every version).</summary>
    public string Versions { get; set; } = "Main";

    /// <summary>Games: the versions of a game stand together at the main version's place.</summary>
    public bool KeepVersionsTogether { get; set; } = true;

    /// <summary>Which discs of a game, by number like the backdrops; movies keep their one disc.</summary>
    public string Discs { get; set; } = "1";

    /// <summary>The automatic update checks this globe.</summary>
    public bool AutoUpdate { get; set; }

    /// <summary>Background and globe colour, "#rrggbb".</summary>
    public string Color { get; set; } = "#000000";
}
