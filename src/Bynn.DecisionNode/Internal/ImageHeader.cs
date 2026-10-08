using System.Buffers.Binary;

namespace Bynn.DecisionNode.Internal;

/// <summary>
/// Reads an image's format and pixel size from its first bytes, without decoding it:
/// the JPEG start-of-frame segment, the PNG <c>IHDR</c> chunk, or the WebP <c>VP8 </c>,
/// <c>VP8L</c> or <c>VP8X</c> chunk.
/// </summary>
internal static class ImageHeader
{
    internal const string Jpeg = "image/jpeg";
    internal const string Png = "image/png";
    internal const string WebP = "image/webp";

    private static ReadOnlySpan<byte> PngSignature => new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>The media type of JPEG, PNG or WebP bytes, or <c>null</c> for anything else.</summary>
    internal static string? Sniff(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return Jpeg;
        }
        if (data.Length >= 8 && data[..8].SequenceEqual(PngSignature))
        {
            return Png;
        }
        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8))
        {
            return WebP;
        }
        return null;
    }

    /// <summary>
    /// The pixel width and height from the header of an image of <paramref name="mediaType"/>,
    /// or <c>null</c> when the header cannot be read.
    /// </summary>
    internal static (int Width, int Height)? Dimensions(ReadOnlySpan<byte> data, string mediaType) => mediaType switch
    {
        Png => PngDimensions(data),
        Jpeg => JpegDimensions(data),
        WebP => WebPDimensions(data),
        _ => null,
    };

    private static (int, int)? PngDimensions(ReadOnlySpan<byte> data)
    {
        // Signature (8), chunk length (4), "IHDR" (4), width (4), height (4), big-endian.
        if (data.Length < 24 || !data[12..16].SequenceEqual("IHDR"u8))
        {
            return null;
        }
        var width = BinaryPrimitives.ReadUInt32BigEndian(data[16..20]);
        var height = BinaryPrimitives.ReadUInt32BigEndian(data[20..24]);
        if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue)
        {
            return null;
        }
        return ((int)width, (int)height);
    }

    private static (int, int)? JpegDimensions(ReadOnlySpan<byte> data)
    {
        // Walk the marker segments after SOI until a start-of-frame segment.
        var i = 2;
        while (i + 1 < data.Length)
        {
            if (data[i] != 0xFF)
            {
                return null;
            }
            var marker = data[i + 1];
            if (marker == 0xFF)
            {
                // A fill byte before the marker.
                i++;
                continue;
            }
            if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7)
            {
                // Markers without a length.
                i += 2;
                continue;
            }
            if (marker is 0xD9 or 0xDA)
            {
                // End of image, or the scan began before any frame header.
                return null;
            }
            if (i + 4 > data.Length)
            {
                return null;
            }
            int length = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(i + 2, 2));
            if (length < 2)
            {
                return null;
            }
            if (IsStartOfFrame(marker))
            {
                // Length (2), precision (1), height (2), width (2).
                if (length < 7 || i + 9 > data.Length)
                {
                    return null;
                }
                int height = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(i + 5, 2));
                int width = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(i + 7, 2));
                return width == 0 || height == 0 ? null : (width, height);
            }
            i += 2 + length;
        }
        return null;
    }

    private static bool IsStartOfFrame(byte marker) =>
        marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC);

    private static (int, int)? WebPDimensions(ReadOnlySpan<byte> data)
    {
        if (data.Length < 30)
        {
            return null;
        }
        var chunk = data[12..16];
        if (chunk.SequenceEqual("VP8 "u8))
        {
            // Frame tag (3 bytes at 20), start code 9D 01 2A, then 14-bit width and height.
            if (data[23] != 0x9D || data[24] != 0x01 || data[25] != 0x2A)
            {
                return null;
            }
            var width = BinaryPrimitives.ReadUInt16LittleEndian(data[26..28]) & 0x3FFF;
            var height = BinaryPrimitives.ReadUInt16LittleEndian(data[28..30]) & 0x3FFF;
            return width == 0 || height == 0 ? null : (width, height);
        }
        if (chunk.SequenceEqual("VP8L"u8))
        {
            // Signature 0x2F, then width - 1 and height - 1 in 14 bits each.
            if (data[20] != 0x2F)
            {
                return null;
            }
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(data[21..25]);
            return ((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
        }
        if (chunk.SequenceEqual("VP8X"u8))
        {
            // Flags (4 bytes at 20), then canvas width - 1 and height - 1 in 24 bits each.
            var width = data[24] | (data[25] << 8) | (data[26] << 16);
            var height = data[27] | (data[28] << 8) | (data[29] << 16);
            return (width + 1, height + 1);
        }
        return null;
    }
}
