using System;
using System.Buffers.Binary;

namespace Jellyfin.Plugin.Globe.Data;

/// <summary>
/// Small BC1 (DXT1) and BC3 (DXT5) encoders; the GPU decodes both directly. 4×4 blocks: BC1 8 bytes of colour,
/// BC3 8 bytes of alpha before them. The two end colours of a block lie on the main axis of its colours (power
/// iteration), pulled in by 1/16 of the range. BC3 expects premultiplied colours.
/// </summary>
public static class BlockCompression
{
    /// <summary>Encodes RGB (3 bytes per pixel, width and height multiples of 4) block row by block row.</summary>
    public static byte[] EncodeBc1(byte[] rgb, int width, int height)
    {
        var output = new byte[width / 4 * (height / 4) * 8];
        var block = new float[16, 3];
        var offset = 0;
        for (var by = 0; by < height; by += 4)
        {
            for (var bx = 0; bx < width; bx += 4)
            {
                for (var p = 0; p < 16; p++)
                {
                    var i = ((by + p / 4) * width + bx + p % 4) * 3;
                    block[p, 0] = rgb[i];
                    block[p, 1] = rgb[i + 1];
                    block[p, 2] = rgb[i + 2];
                }

                EncodeColour(block, output.AsSpan(offset, 8));
                offset += 8;
            }
        }

        return output;
    }

    /// <summary>Encodes premultiplied RGBA (4 bytes per pixel, width and height multiples of 4) as BC3.</summary>
    public static byte[] EncodeBc3(byte[] rgba, int width, int height)
    {
        var output = new byte[width / 4 * (height / 4) * 16];
        var block = new float[16, 3];
        Span<byte> alpha = stackalloc byte[16];
        var offset = 0;
        for (var by = 0; by < height; by += 4)
        {
            for (var bx = 0; bx < width; bx += 4)
            {
                for (var p = 0; p < 16; p++)
                {
                    var i = ((by + p / 4) * width + bx + p % 4) * 4;
                    block[p, 0] = rgba[i];
                    block[p, 1] = rgba[i + 1];
                    block[p, 2] = rgba[i + 2];
                    alpha[p] = rgba[i + 3];
                }

                EncodeAlpha(alpha, output.AsSpan(offset, 8));
                EncodeColour(block, output.AsSpan(offset + 8, 8));
                offset += 16;
            }
        }

        return output;
    }

    /// <summary>Halves an image with the given bytes per pixel by averaging 2×2 pixels.</summary>
    public static byte[] Half(byte[] pixels, int width, int height, int channels)
    {
        int w = width / 2, h = height / 2;
        var output = new byte[w * h * channels];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                for (var c = 0; c < channels; c++)
                {
                    var top = (2 * y * width + 2 * x) * channels + c;
                    var bottom = top + width * channels;
                    var sum = pixels[top] + pixels[top + channels] + pixels[bottom] + pixels[bottom + channels];
                    output[(y * w + x) * channels + c] = (byte)Math.Round(sum / 4.0, MidpointRounding.ToEven);
                }
            }
        }

        return output;
    }

    /// <summary>The image and its halves, each encoded, one after the other (the mip chain of a texture).</summary>
    public static byte[] EncodeMips(byte[] pixels, int width, int height, bool transparent, int levels)
    {
        var parts = new System.Collections.Generic.List<byte[]>();
        for (var level = 0; level < levels; level++)
        {
            parts.Add(transparent ? EncodeBc3(pixels, width, height) : EncodeBc1(pixels, width, height));
            if (level < levels - 1)
            {
                pixels = Half(pixels, width, height, transparent ? 4 : 3);
                width /= 2;
                height /= 2;
            }
        }

        var total = 0;
        foreach (var part in parts)
        {
            total += part.Length;
        }

        var result = new byte[total];
        var at = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, at);
            at += part.Length;
        }

        return result;
    }

    private static void EncodeColour(float[,] block, Span<byte> output)
    {
        Span<float> mean = stackalloc float[3];
        for (var p = 0; p < 16; p++)
        {
            for (var c = 0; c < 3; c++)
            {
                mean[c] += block[p, c] / 16;
            }
        }

        Span<float> cov = stackalloc float[9];
        for (var p = 0; p < 16; p++)
        {
            for (var a = 0; a < 3; a++)
            {
                for (var b = 0; b < 3; b++)
                {
                    cov[a * 3 + b] += (block[p, a] - mean[a]) * (block[p, b] - mean[b]);
                }
            }
        }

        Span<float> axis = stackalloc float[] { 1, 1, 1 };
        Span<float> next = stackalloc float[3];
        for (var step = 0; step < 6; step++)
        {
            for (var a = 0; a < 3; a++)
            {
                next[a] = cov[a * 3] * axis[0] + cov[a * 3 + 1] * axis[1] + cov[a * 3 + 2] * axis[2];
            }

            var length = MathF.Sqrt(next[0] * next[0] + next[1] * next[1] + next[2] * next[2]) + 1e-8f;
            for (var a = 0; a < 3; a++)
            {
                axis[a] = next[a] / length;
            }
        }

        float low = float.MaxValue, high = float.MinValue;
        for (var p = 0; p < 16; p++)
        {
            var t = (block[p, 0] - mean[0]) * axis[0] + (block[p, 1] - mean[1]) * axis[1] + (block[p, 2] - mean[2]) * axis[2];
            low = Math.Min(low, t);
            high = Math.Max(high, t);
        }

        var inset = (high - low) / 16;
        var color0 = Pack565(mean, axis, high - inset);
        var color1 = Pack565(mean, axis, low + inset);
        if (color0 < color1)
        {
            (color0, color1) = (color1, color0);   // color0 > color1 selects the four-colour mode
        }

        Span<float> palette = stackalloc float[12];
        Unpack565(color0, palette[..3]);
        Unpack565(color1, palette.Slice(3, 3));
        for (var c = 0; c < 3; c++)
        {
            palette[6 + c] = (2 * palette[c] + palette[3 + c]) / 3;
            palette[9 + c] = (palette[c] + 2 * palette[3 + c]) / 3;
        }

        uint indices = 0;
        if (color0 != color1)
        {
            for (var p = 0; p < 16; p++)
            {
                var best = 0;
                var bestDistance = float.MaxValue;
                for (var e = 0; e < 4; e++)
                {
                    float dr = block[p, 0] - palette[e * 3], dg = block[p, 1] - palette[e * 3 + 1], db = block[p, 2] - palette[e * 3 + 2];
                    var distance = dr * dr + dg * dg + db * db;
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = e;
                    }
                }

                indices |= (uint)best << (2 * p);
            }
        }

        BinaryPrimitives.WriteUInt16LittleEndian(output, color0);
        BinaryPrimitives.WriteUInt16LittleEndian(output[2..], color1);
        BinaryPrimitives.WriteUInt32LittleEndian(output[4..], indices);
    }

    // Eight-value alpha: alpha0 > alpha1, six steps between them; 3-bit index per pixel.
    private static void EncodeAlpha(ReadOnlySpan<byte> alpha, Span<byte> output)
    {
        byte high = 0, low = 255;
        foreach (var a in alpha)
        {
            high = Math.Max(high, a);
            low = Math.Min(low, a);
        }

        output[0] = high;
        output[1] = low;
        ulong indices = 0;
        if (high != low)
        {
            Span<float> palette = stackalloc float[8];
            palette[0] = high;
            palette[1] = low;
            for (var i = 1; i < 7; i++)
            {
                palette[i + 1] = ((7 - i) * high + i * low) / 7f;
            }

            for (var p = 0; p < 16; p++)
            {
                var best = 0;
                var bestDistance = float.MaxValue;
                for (var e = 0; e < 8; e++)
                {
                    var distance = Math.Abs(alpha[p] - palette[e]);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = e;
                    }
                }

                indices |= (ulong)best << (3 * p);
            }
        }

        for (var i = 0; i < 6; i++)
        {
            output[2 + i] = (byte)(indices >> (8 * i));
        }
    }

    private static ushort Pack565(ReadOnlySpan<float> mean, ReadOnlySpan<float> axis, float t)
    {
        static int Channel(float value, int max) => (int)Math.Clamp(Math.Round(value * max / 255, MidpointRounding.ToEven), 0, max);
        var r = Channel(mean[0] + axis[0] * t, 31);
        var g = Channel(mean[1] + axis[1] * t, 63);
        var b = Channel(mean[2] + axis[2] * t, 31);
        return (ushort)((r << 11) | (g << 5) | b);
    }

    private static void Unpack565(ushort color, Span<float> rgb)
    {
        rgb[0] = (color >> 11 & 31) * 255f / 31;
        rgb[1] = (color >> 5 & 63) * 255f / 63;
        rgb[2] = (color & 31) * 255f / 31;
    }
}
