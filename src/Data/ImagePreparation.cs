using System;
using System.IO;
using System.Linq;
using SkiaSharp;

namespace Jellyfin.Plugin.Globe.Data;

/// <summary>A prepared picture: the store record and what it remembers about the original.</summary>
public sealed record PreparedImage(byte[] Record, ImageInfo Info);

/// <summary>
/// Turns an artwork file into a store record: brought to the type's standard shape, then the sharp stage (BC1 or BC3,
/// two mip levels) and the small and tiny stages as raw RGBA. Opaque images lose burned-in black bars when asked and are
/// cut from the centre to the shape, as Jellyfin's cards do, or shown whole on a blurred copy of themselves; transparent
/// ones are fitted whole and keep their transparency (premultiplied).
/// </summary>
public static class ImagePreparation
{
    /// <summary>Changes whenever the result for the same file and treatment changes, so old records are not reused.</summary>
    public const string Recipe = "r2";

    /// <summary>The treatment part of a store key: black bars removed or not, and the fit with its crop limit.</summary>
    public static string Treatment(bool blackBars, string fit, int cropLimit) =>
        Recipe + ";bars=" + (blackBars ? "1" : "0") + ";fit=" + fit.ToLowerInvariant() + (fit == "Adaptive" ? cropLimit.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty);

    public static PreparedImage? MakeRecord(ArtworkKind kind, string file, string treatment = "")
    {
        try
        {
            var decoded = Decode(file, kind.SharpWidth, kind.SharpHeight);
            if (decoded is null)
            {
                return null;
            }

            using var source = decoded.Value.Bitmap;
            var whole = new SKRect(0, 0, kind.SharpWidth, kind.SharpHeight);
            SKBitmap sharp;
            float[] shown, placed;
            if (kind.Transparent)
            {
                var sharpRect = FitInside(source.Width, source.Height, kind.SharpWidth, kind.SharpHeight);
                sharp = Draw(source, new SKRect(0, 0, source.Width, source.Height), kind.SharpWidth, kind.SharpHeight, sharpRect, true);
                shown = new[] { 0f, 0f, 1f, 1f };
                placed = Share(sharpRect, kind.SharpWidth, kind.SharpHeight);
            }
            else
            {
                var (fit, cropLimit, blackBars) = ParseTreatment(treatment);
                var content = (blackBars ? ContentBox(source) : null) ?? new SKRect(0, 0, source.Width, source.Height);
                if (FillsWithBlur(content, kind.Aspect, fit, cropLimit, content.Width < source.Width || content.Height < source.Height))
                {
                    var front = FitInside((int)content.Width, (int)content.Height, kind.SharpWidth, kind.SharpHeight);
                    sharp = BlurFill(source, content, kind, front);
                    shown = Share(content, source.Width, source.Height);
                    placed = Share(front, kind.SharpWidth, kind.SharpHeight);
                }
                else
                {
                    var cut = CutToAspect(content, kind.Aspect);
                    sharp = Draw(source, cut, kind.SharpWidth, kind.SharpHeight, whole, false);
                    shown = Share(cut, source.Width, source.Height);
                    placed = new[] { 0f, 0f, 1f, 1f };
                }
            }

            using var sharpStage = sharp;
            using var small = Draw(sharpStage, whole, kind.SmallWidth, kind.SmallHeight, new SKRect(0, 0, kind.SmallWidth, kind.SmallHeight), kind.Transparent);
            using var tiny = Draw(sharpStage, whole, kind.TinyWidth, kind.TinyHeight, new SKRect(0, 0, kind.TinyWidth, kind.TinyHeight), kind.Transparent);

            var rgba = sharpStage.Bytes;
            var sharpBytes = kind.Transparent
                ? BlockCompression.EncodeMips(rgba, kind.SharpWidth, kind.SharpHeight, true, 2)
                : BlockCompression.EncodeMips(ToRgb(rgba), kind.SharpWidth, kind.SharpHeight, false, 2);

            var record = new byte[kind.RecordBytes];
            sharpBytes.CopyTo(record, 0);
            small.Bytes.CopyTo(record, kind.SharpBytes);
            tiny.Bytes.CopyTo(record, kind.SharpBytes + kind.SmallBytes);
            return new PreparedImage(record, new ImageInfo(decoded.Value.Animated, shown, placed));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // SkiaSharp raises a plain Exception when it cannot get the pixels of a huge picture; one such file only drops out.
            return null;
        }
    }

    private static (string Fit, int CropLimit, bool BlackBars) ParseTreatment(string treatment)
    {
        string fit = "crop", limit = "15";
        var blackBars = false;
        foreach (var part in treatment.Split(';'))
        {
            if (part == "bars=1")
            {
                blackBars = true;
            }
            else if (part.StartsWith("fit=", StringComparison.Ordinal))
            {
                fit = new string(part[4..].TakeWhile(char.IsLetter).ToArray());
                limit = new string(part[4..].SkipWhile(char.IsLetter).ToArray());
            }
        }

        return (fit, int.TryParse(limit, out var value) ? value : 15, blackBars);
    }

    /// <summary>
    /// Whether the content goes whole onto a blurred copy instead of being cut. Blur Fill always does so (unless the shape
    /// already fits); Adaptive when cutting would lose more than the crop limit; every other fit only for content with
    /// burned-in bars that is narrower than the tile, so a picture between side bars is never cut at its sides.
    /// </summary>
    private static bool FillsWithBlur(SKRect content, double aspect, string fit, int cropLimit, bool hadBars)
    {
        var ratio = content.Width / content.Height / aspect;
        var loss = 1 - Math.Min(ratio, 1 / ratio);
        return fit switch
        {
            "blurfill" => loss > 0.02,
            "adaptive" => loss > cropLimit / 100.0,
            _ => hadBars && ratio < 0.98,
        };
    }

    /// <summary>
    /// Finds burned-in black bars: a row or column is a bar when it is almost
    /// black (mean below 14) and even (spread below 6); content that keeps 98 % in both directions has no bars; a picture
    /// that is dark all over stays as it is. Returns the content without the bars, or null.
    /// </summary>
    public static SKRect? ContentBox(SKBitmap bitmap)
    {
        int w = bitmap.Width, h = bitmap.Height;
        var pixels = bitmap.Bytes;
        double[] colSum = new double[w], colSquares = new double[w], rowSum = new double[h], rowSquares = new double[h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                var grey = 0.299 * pixels[i] + 0.587 * pixels[i + 1] + 0.114 * pixels[i + 2];
                colSum[x] += grey;
                colSquares[x] += grey * grey;
                rowSum[y] += grey;
                rowSquares[y] += grey * grey;
            }
        }

        static bool Dark(double sum, double squares, int n)
        {
            var mean = sum / n;
            return mean < 14 && Math.Sqrt(Math.Max(0, squares / n - mean * mean)) < 6;
        }

        var darkColumns = Enumerable.Range(0, w).Select(x => Dark(colSum[x], colSquares[x], h)).ToArray();
        var darkRows = Enumerable.Range(0, h).Select(y => Dark(rowSum[y], rowSquares[y], w)).ToArray();
        if (darkColumns.All(d => d) || darkRows.All(d => d))
        {
            return null;
        }

        int left = Array.IndexOf(darkColumns, false), right = Array.LastIndexOf(darkColumns, false) + 1;
        int top = Array.IndexOf(darkRows, false), bottom = Array.LastIndexOf(darkRows, false) + 1;
        if (right - left >= 0.98 * w && bottom - top >= 0.98 * h)
        {
            return null;
        }

        return new SKRect(left, top, right, bottom);
    }

    /// <summary>
    /// The content whole in the middle, behind it the same content cut to the tile, blurred and 15 % darker, with no edge,
    /// shadow or frame. The blur radius is 40 per 1280 pixels of the tile's longer side.
    /// </summary>
    private static SKBitmap BlurFill(SKBitmap source, SKRect content, ArtworkKind kind, SKRect front)
    {
        var bitmap = new SKBitmap(new SKImageInfo(kind.SharpWidth, kind.SharpHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var image = SKImage.FromBitmap(source);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);
        var radius = 40f * Math.Max(kind.SharpWidth, kind.SharpHeight) / 1280f;
        const float dim = 0.85f;
        using (var background = new SKPaint
        {
            ImageFilter = SKImageFilter.CreateBlur(radius, radius, SKShaderTileMode.Clamp),
            ColorFilter = SKColorFilter.CreateColorMatrix(new[]
            {
                dim, 0, 0, 0, 0,
                0, dim, 0, 0, 0,
                0, 0, dim, 0, 0,
                0, 0, 0, 1, 0,
            }),
        })
        {
            DrawPart(canvas, image, CutToAspect(content, kind.Aspect), new SKRect(0, 0, kind.SharpWidth, kind.SharpHeight), background);
        }

        using (var plain = new SKPaint())
        {
            DrawPart(canvas, image, content, front, plain);
        }

        canvas.Flush();
        return bitmap;
    }

    /// <summary>Width and height of a picture from its header, turned by its EXIF origin; null when it cannot be read.</summary>
    public static (int Width, int Height)? Size(string file)
    {
        try
        {
            using var codec = SKCodec.Create(file);
            if (codec is null)
            {
                return null;
            }

            var sideways = codec.EncodedOrigin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
            return sideways ? (codec.Info.Height, codec.Info.Width) : (codec.Info.Width, codec.Info.Height);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static float[] Share(SKRect rect, float width, float height) =>
        new[] { rect.Left / width, rect.Top / height, rect.Width / width, rect.Height / height };

    /// <summary>Decodes an image no larger than needed: JPEG can decode at a reduced scale, which keeps a 4000-pixel
    /// poster from taking a hundred megabytes of memory. Other formats decode at full size. An animated file gives its
    /// first frame.</summary>
    private static (SKBitmap Bitmap, bool Animated)? Decode(string file, int width, int height)
    {
        using var codec = SKCodec.Create(file);
        if (codec is null)
        {
            return null;
        }

        var size = codec.Info;
        var scale = Math.Min(1f, Math.Max((float)width / size.Width, (float)height / size.Height));
        var scaled = codec.GetScaledDimensions(scale);
        var info = new SKImageInfo(scaled.Width, scaled.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
        {
            bitmap.Dispose();
            return null;
        }

        return (Upright(bitmap, codec.EncodedOrigin), codec.FrameCount > 1);
    }

    /// <summary>Turns a picture the way its EXIF orientation asks for, as photos from a phone need it.</summary>
    private static SKBitmap Upright(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft)
        {
            return bitmap;
        }

        int w = bitmap.Width, h = bitmap.Height;
        var sideways = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var turned = new SKBitmap(new SKImageInfo(sideways ? h : w, sideways ? w : h, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(turned))
        {
            switch (origin)
            {
                case SKEncodedOrigin.TopRight:
                    canvas.Translate(w, 0);
                    canvas.Scale(-1, 1);
                    break;
                case SKEncodedOrigin.BottomRight:
                    canvas.Translate(w, h);
                    canvas.RotateDegrees(180);
                    break;
                case SKEncodedOrigin.BottomLeft:
                    canvas.Translate(0, h);
                    canvas.Scale(1, -1);
                    break;
                case SKEncodedOrigin.LeftTop:
                    canvas.RotateDegrees(90);
                    canvas.Scale(1, -1);
                    break;
                case SKEncodedOrigin.RightTop:
                    canvas.Translate(h, 0);
                    canvas.RotateDegrees(90);
                    break;
                case SKEncodedOrigin.RightBottom:
                    canvas.Translate(h, w);
                    canvas.RotateDegrees(-90);
                    canvas.Scale(1, -1);
                    break;
                case SKEncodedOrigin.LeftBottom:
                    canvas.Translate(0, w);
                    canvas.RotateDegrees(-90);
                    break;
            }

            canvas.DrawBitmap(bitmap, 0, 0);
        }

        bitmap.Dispose();
        return turned;
    }

    /// <summary>The centred part of a rectangle with the wanted aspect.</summary>
    private static SKRect CutToAspect(SKRect area, double aspect)
    {
        if (area.Width > area.Height * aspect)
        {
            var w = (float)(area.Height * aspect);
            return SKRect.Create(area.Left + (area.Width - w) / 2, area.Top, w, area.Height);
        }

        var h = (float)(area.Width / aspect);
        return SKRect.Create(area.Left, area.Top + (area.Height - h) / 2, area.Width, h);
    }

    /// <summary>The largest rectangle with the image's own aspect inside the target, centred.</summary>
    private static SKRect FitInside(int width, int height, int targetWidth, int targetHeight)
    {
        var scale = Math.Min((float)targetWidth / width, (float)targetHeight / height);
        float w = width * scale, h = height * scale;
        return SKRect.Create((targetWidth - w) / 2, (targetHeight - h) / 2, w, h);
    }

    private static SKBitmap Draw(SKBitmap source, SKRect sourceRect, int width, int height, SKRect target, bool transparent)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var image = SKImage.FromBitmap(source);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(transparent ? SKColors.Transparent : SKColors.Black);
        using (var paint = new SKPaint())
        {
            DrawPart(canvas, image, sourceRect, target, paint);
        }

        canvas.Flush();
        return bitmap;
    }

    private static void DrawPart(SKCanvas canvas, SKImage image, SKRect sourceRect, SKRect target, SKPaint paint)
    {
#if NET10_0_OR_GREATER
        canvas.DrawImage(image, sourceRect, target, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
#else
        // SkiaSharp 2.88 has no sampling options; High uses mipmaps, the same smooth result for large reductions.
        paint.FilterQuality = SKFilterQuality.High;
        canvas.DrawImage(image, sourceRect, target, paint);
#endif
    }

    private static byte[] ToRgb(byte[] rgba)
    {
        var rgb = new byte[rgba.Length / 4 * 3];
        for (int i = 0, j = 0; i < rgb.Length; i += 3, j += 4)
        {
            rgb[i] = rgba[j];
            rgb[i + 1] = rgba[j + 1];
            rgb[i + 2] = rgba[j + 2];
        }

        return rgb;
    }
}
