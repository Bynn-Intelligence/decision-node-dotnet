using Bynn.DecisionNode.Internal;

namespace Bynn.DecisionNode.Tests;

/// <summary>Image files the tests read, copied next to the test assembly.</summary>
public static class Fixtures
{
    public static byte[] Read(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}

/// <summary>The pixel size read from each format's header, without decoding the image.</summary>
public class ImageHeaderTests
{
    [Theory]
    [InlineData("baseline-3x2.jpg", "image/jpeg", 3, 2)]
    [InlineData("exif-17x9.jpg", "image/jpeg", 17, 9)]
    [InlineData("progressive-5x4.jpg", "image/jpeg", 5, 4)]
    [InlineData("rgb-7x3.png", "image/png", 7, 3)]
    [InlineData("header-only-8000x6000.png", "image/png", 8000, 6000)]
    [InlineData("lossy-9x5.webp", "image/webp", 9, 5)]
    [InlineData("lossless-6x4.webp", "image/webp", 6, 4)]
    [InlineData("alpha-8x2.webp", "image/webp", 8, 2)]
    public void ReadsTheFormatAndPixelSize(string file, string mediaType, int width, int height)
    {
        var data = Fixtures.Read(file);
        Assert.Equal(mediaType, ImageHeader.Sniff(data));
        Assert.Equal((width, height), ImageHeader.Dimensions(data, mediaType));
    }

    [Fact]
    public void TrailingBytesDoNotChangeTheHeader()
    {
        foreach (var file in new[] { "baseline-3x2.jpg", "rgb-7x3.png", "lossy-9x5.webp" })
        {
            var data = Fixtures.Read(file);
            var padded = new byte[600_000];
            data.CopyTo(padded, 0);
            var type = ImageHeader.Sniff(padded)!;
            Assert.Equal(ImageHeader.Dimensions(data, type), ImageHeader.Dimensions(padded, type));
        }
    }

    [Fact]
    public void JpegFillBytesBeforeAMarkerAreSkipped()
    {
        var data = Fixtures.Read("baseline-3x2.jpg").ToList();
        // FF FF FF E0 ... : fill bytes before the APP0 marker.
        data.InsertRange(2, new byte[] { 0xFF, 0xFF });
        Assert.Equal((3, 2), ImageHeader.Dimensions(data.ToArray(), ImageHeader.Jpeg));
    }

    [Fact]
    public void AGifIsNoKnownFormat()
    {
        Assert.Null(ImageHeader.Sniff(Fixtures.Read("pixel.gif")));
    }

    [Fact]
    public void TruncatedHeadersCannotBeRead()
    {
        foreach (var file in new[] { "baseline-3x2.jpg", "progressive-5x4.jpg", "rgb-7x3.png", "lossy-9x5.webp", "lossless-6x4.webp", "alpha-8x2.webp" })
        {
            var data = Fixtures.Read(file);
            var type = ImageHeader.Sniff(data)!;
            // The signature alone: the type is known, the size is not.
            var cut = type == ImageHeader.Png ? 8 : type == ImageHeader.Jpeg ? 4 : 16;
            Assert.Null(ImageHeader.Dimensions(data.AsSpan(0, cut), type));
        }
    }

    [Fact]
    public void AJpegWhoseScanStartsBeforeAFrameHeaderCannotBeRead()
    {
        // SOI, then SOS straight away.
        var data = new byte[] { 0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x08, 0, 0, 0, 0, 0, 0, 0, 0 };
        Assert.Null(ImageHeader.Dimensions(data, ImageHeader.Jpeg));
    }

    [Fact]
    public void AJpegWithAGarbledSegmentCannotBeRead()
    {
        var data = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00, 0x12, 0x34, 0x56 };
        Assert.Null(ImageHeader.Dimensions(data, ImageHeader.Jpeg));
    }

    [Fact]
    public void AWebPCanvasOver40MegapixelsIsReadFromVp8X()
    {
        // RIFF....WEBP VP8X, flags, canvas width - 1 and height - 1 in 24 bits each.
        var data = new byte[30];
        "RIFF"u8.CopyTo(data);
        "WEBPVP8X"u8.CopyTo(data.AsSpan(8));
        WriteUInt24(data, 24, 9999);
        WriteUInt24(data, 27, 4999);
        Assert.Equal((10000, 5000), ImageHeader.Dimensions(data, ImageHeader.WebP));
    }

    [Fact]
    public async Task AnImageOver40MegapixelsIsRefusedFromItsHeaderAlone()
    {
        var stub = new StubHandler((_, _) => Http.Ok());
        using var dn = Http.Client(stub);
        var error = await Assert.ThrowsAsync<BadRequestError>(() => dn.DecideAsync("s", Http.OneTruth(), new[]
        {
            Image.FromBytes("huge", Fixtures.Read("header-only-8000x6000.png")),
        }));
        Assert.Contains("40 megapixels", error.Message);
        Assert.Empty(stub.Requests);
    }

    private static void WriteUInt24(byte[] data, int at, int value)
    {
        data[at] = (byte)value;
        data[at + 1] = (byte)(value >> 8);
        data[at + 2] = (byte)(value >> 16);
    }
}
