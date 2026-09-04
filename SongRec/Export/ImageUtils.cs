using System.Buffers.Binary;

namespace beatleader_songrec.Export;

/// <summary>
/// Minimal, dependency-free helpers for validating that a cover image is square and
/// converting it to a base64 data URI for embedding in a .bplist file. Supports PNG and
/// JPEG, the two formats commonly used for Beat Saber playlist covers.
/// </summary>
public static class ImageUtils
{
    /// <summary>
    /// Reads the image at <paramref name="imagePath"/>, verifies it is square, and returns it
    /// as a "data:image/&lt;type&gt;;base64,..." URI ready to embed in a .bplist file.
    /// </summary>
    /// <exception cref="FileNotFoundException">The image file does not exist.</exception>
    /// <exception cref="InvalidOperationException">
    /// The image format is unrecognized, or the image is not square.
    /// </exception>
    public static string ToSquareImageDataUri(string imagePath)
    {
        if (!File.Exists(imagePath))
        {
            throw new FileNotFoundException($"Cover image not found at '{imagePath}'.", imagePath);
        }

        var bytes = File.ReadAllBytes(imagePath);
        var (width, height, mimeType) = ReadDimensions(bytes);

        if (width != height)
        {
            throw new InvalidOperationException(
                $"Cover image at '{imagePath}' must be square, but was {width}x{height}.");
        }

        return $"data:{mimeType};base64,{Convert.ToBase64String(bytes)}";
    }

    private static (int Width, int Height, string MimeType) ReadDimensions(ReadOnlySpan<byte> bytes)
    {
        if (IsPng(bytes))
        {
            return (ReadPngWidth(bytes), ReadPngHeight(bytes), "image/png");
        }

        if (IsJpeg(bytes))
        {
            var (width, height) = ReadJpegDimensions(bytes);
            return (width, height, "image/jpeg");
        }

        throw new InvalidOperationException("Unsupported image format - only PNG and JPEG cover images are supported.");
    }

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static bool IsPng(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 8 && bytes[..8].SequenceEqual(PngSignature);

    private static int ReadPngWidth(ReadOnlySpan<byte> bytes) =>
        (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(16, 4));

    private static int ReadPngHeight(ReadOnlySpan<byte> bytes) =>
        (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(20, 4));

    private static bool IsJpeg(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xD8;

    private static (int Width, int Height) ReadJpegDimensions(ReadOnlySpan<byte> bytes)
    {
        var offset = 2;
        while (offset + 9 <= bytes.Length)
        {
            if (bytes[offset] != 0xFF)
            {
                offset++;
                continue;
            }

            var marker = bytes[offset + 1];

            // Start-of-frame markers (baseline/progressive, excluding DHT/JPG extensions) carry
            // the image dimensions.
            var isStartOfFrame = marker is >= 0xC0 and <= 0xCF && marker is not 0xC4 and not 0xC8 and not 0xCC;

            if (isStartOfFrame)
            {
                var height = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 5, 2));
                var width = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 7, 2));
                return (width, height);
            }

            if (marker is 0xD8 or 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                // Markers without a length-prefixed segment.
                offset += 2;
                continue;
            }

            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 2, 2));
            offset += 2 + segmentLength;
        }

        throw new InvalidOperationException("Could not determine JPEG image dimensions.");
    }
}
