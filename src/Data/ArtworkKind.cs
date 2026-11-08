using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Globe.Data;

/// <summary>
/// One artwork type on the globe: its Jellyfin image type, its standard aspect and the sizes of its three stages.
/// Sizes are multiples of 4 (BC blocks), the sharp stage keeps about 150,000 pixels per image.
/// </summary>
public sealed record ArtworkKind(
    int Index,
    string Name,
    ImageType ImageType,
    double Aspect,
    bool Transparent,
    int SharpWidth,
    int SharpHeight,
    int SmallWidth,
    int SmallHeight,
    int TinyWidth,
    int TinyHeight,
    bool Selectable = true)
{
    // Poster, backdrop, thumb and banner follow Jellyfin's card shapes; logo, clearart and disc fanart.tv's canvases.
    // The last four are no artwork type of their own: they hold photos and home videos that keep their own shape.
    public static readonly ArtworkKind[] All =
    {
        new(0, "Poster", ImageType.Primary, 2.0 / 3.0, false, 320, 480, 80, 120, 20, 30),
        new(1, "Backdrop", ImageType.Backdrop, 16.0 / 9.0, false, 512, 288, 128, 72, 32, 18),
        new(2, "Thumb", ImageType.Thumb, 16.0 / 9.0, false, 512, 288, 128, 72, 32, 18),
        new(3, "Banner", ImageType.Banner, 1000.0 / 185.0, false, 864, 160, 216, 40, 54, 10),
        new(4, "Logo", ImageType.Logo, 800.0 / 310.0, true, 640, 248, 160, 62, 40, 16),
        new(5, "Clearart", ImageType.Art, 1000.0 / 562.0, true, 512, 288, 128, 72, 32, 18),
        new(6, "Disc", ImageType.Disc, 1.0, true, 384, 384, 96, 96, 24, 24),
        new(7, "Shape34", ImageType.Primary, 3.0 / 4.0, false, 336, 448, 84, 112, 20, 28, false),
        new(8, "Shape11", ImageType.Primary, 1.0, false, 384, 384, 96, 96, 24, 24, false),
        new(9, "Shape43", ImageType.Primary, 4.0 / 3.0, false, 448, 336, 112, 84, 28, 20, false),
        new(10, "Shape32", ImageType.Primary, 3.0 / 2.0, false, 480, 320, 120, 80, 28, 20, false),
    };

    /// <summary>The order of a title's images; it makes good columns (poster alone, logo with clearart, banner with
    /// thumb and a backdrop, backdrops in pairs).</summary>
    public static readonly ArtworkKind[] TitleOrder =
    {
        All[0], All[6], All[4], All[5], All[3], All[2], All[1],
    };

    public static ArtworkKind Poster => All[0];

    public static ArtworkKind Thumb => All[2];

    /// <summary>The shapes a photo or home video can keep, from tall to wide.</summary>
    public static readonly ArtworkKind[] Shapes = { All[0], All[7], All[8], All[9], All[10], All[2] };

    public static ArtworkKind? ByName(string name) =>
        All.FirstOrDefault(k => k.Selectable && string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The shape closest to a picture's aspect; a 4:3 photo stays 4:3, a panorama ends at 16:9.</summary>
    public static ArtworkKind NearestShape(double aspect) =>
        Shapes.OrderBy(k => Math.Abs(Math.Log(k.Aspect / aspect))).First();

    /// <summary>The nearer of the two card shapes, poster or thumb.</summary>
    public static ArtworkKind NearestCard(double aspect) =>
        Math.Abs(Math.Log(Poster.Aspect / aspect)) <= Math.Abs(Math.Log(Thumb.Aspect / aspect)) ? Poster : Thumb;

    /// <summary>Bytes of the sharp stage: two mip levels, BC1 (4 bits per pixel) or BC3 (8 bits per pixel).</summary>
    public int SharpBytes => (SharpWidth * SharpHeight + SharpWidth * SharpHeight / 4) / (Transparent ? 1 : 2);

    public int SmallBytes => SmallWidth * SmallHeight * 4;

    public int TinyBytes => TinyWidth * TinyHeight * 4;

    /// <summary>One record of the image store: sharp stage, then small and tiny as raw RGBA.</summary>
    public int RecordBytes => SharpBytes + SmallBytes + TinyBytes;

    public static IReadOnlyList<ArtworkKind> Parse(IEnumerable<string> names) =>
        names.Select(ByName).OfType<ArtworkKind>().Distinct().OrderBy(k => k.Index).ToList();
}
