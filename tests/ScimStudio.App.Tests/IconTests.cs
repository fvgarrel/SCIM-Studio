using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ScimStudio.App.Controls;

namespace ScimStudio.App.Tests;

/// <summary>
/// The application's icons are the logo as the interface draws it, rendered here rather than drawn twice. The tests write them to
/// <c>artifacts/icon</c>; the build uses the copies in <c>src/ScimStudio.App/Assets</c> (Windows) and <c>packaging/macos</c> (macOS),
/// replaced by hand when the logo changes.
/// </summary>
public sealed class IconTests {
    private static readonly int[] WindowsSizes = [16, 24, 32, 48, 64, 128, 256];

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47];

    /// <summary>What an icns file holds, as iconutil writes it: a type per size and density, each a PNG.</summary>
    private static readonly (string Type, int Size)[] AppleEntries = [
        ("icp4", 16), ("icp5", 32), ("ic11", 32), ("ic12", 64), ("ic07", 128),
        ("ic13", 256), ("ic08", 256), ("ic14", 512), ("ic09", 512), ("ic10", 1024),
    ];

    /// <summary>
    /// How much of its canvas a macOS icon fills: Apple's grid puts an 824-point body on 1024, and an icon drawn to the edge looks a size
    /// too large beside every other one in the Dock.
    /// </summary>
    private const double APPLE_BODY = 824.0 / 1024.0;

    [AvaloniaFact]
    public void The_logo_renders_as_a_windows_icon_at_every_size() {
        var folder = Folder();
        var images = new List<byte[]>();
        foreach (var size in WindowsSizes) {
            var png = Render(size, 1);
            images.Add(png);
            File.WriteAllBytes(Path.Combine(folder, $"scimstudio-{size}.png"), png);
        }

        File.WriteAllBytes(Path.Combine(folder, "scimstudio.ico"), WindowsIcon(images));
    }

    [AvaloniaFact]
    public void The_logo_renders_as_a_macos_icon_that_reads_back_whole() {
        var images = AppleEntries.Select(entry => (entry.Type, Png: Render(entry.Size, APPLE_BODY))).ToList();
        var icns = AppleIcon(images);
        File.WriteAllBytes(Path.Combine(Folder(), "ScimStudio.icns"), icns);

        Assert.Equal("icns", Encoding.ASCII.GetString(icns, 0, 4));
        Assert.Equal(icns.Length, BinaryPrimitives.ReadInt32BigEndian(icns.AsSpan(4)));

        var offset = 8;
        foreach (var (type, size) in AppleEntries) {
            Assert.Equal(type, Encoding.ASCII.GetString(icns, offset, 4));
            var length = BinaryPrimitives.ReadInt32BigEndian(icns.AsSpan(offset + 4));
            var png = icns.AsSpan(offset + 8, length - 8);

            Assert.True(png[..4].SequenceEqual(PngSignature), $"{type} is no PNG");
            Assert.Equal(size, BinaryPrimitives.ReadInt32BigEndian(png[16..]));
            offset += length;
        }

        Assert.Equal(icns.Length, offset);
    }

    /// <summary>The logo centred on a transparent square, filling the given share of it, as PNG.</summary>
    /// <param name="size">The edge of the square in pixels.</param>
    /// <param name="fill">How much of the edge the logo takes.</param>
    private static byte[] Render(int size, double fill) {
        var logo = new Logo {
            Size = Math.Round(size * fill),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var canvas = new Panel { Width = size, Height = size, Background = Brushes.Transparent, Children = { logo } };
        var window = new Window { Width = size, Height = size, Content = canvas, Background = Brushes.Transparent };
        Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        using var bitmap = new RenderTargetBitmap(new PixelSize(size, size));
        bitmap.Render(canvas);
        window.Close();

        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }

    /// <summary>An ICO file of PNG images, which Windows reads since Vista: a header, a directory entry per image, then the images.</summary>
    /// <param name="images">The images, in the order of <see cref="WindowsSizes"/>.</param>
    private static byte[] WindowsIcon(List<byte[]> images) {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write((short)0);
        writer.Write((short)1);
        writer.Write((short)images.Count);

        var offset = 6 + (16 * images.Count);
        for (var i = 0; i < images.Count; i++) {
            var edge = WindowsSizes[i] >= 256 ? 0 : WindowsSizes[i];
            writer.Write((byte)edge);
            writer.Write((byte)edge);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((short)1);
            writer.Write((short)32);
            writer.Write(images[i].Length);
            writer.Write(offset);
            offset += images[i].Length;
        }

        foreach (var image in images) {
            writer.Write(image);
        }

        return stream.ToArray();
    }

    /// <summary>An icns file: the magic and the file's length, then per image its type, its length and the PNG - big-endian throughout.</summary>
    /// <param name="images">The images with their icns types.</param>
    private static byte[] AppleIcon(List<(string Type, byte[] Png)> images) {
        using var stream = new MemoryStream();
        Span<byte> number = stackalloc byte[4];

        stream.Write("icns"u8);
        BinaryPrimitives.WriteInt32BigEndian(number, 8 + images.Sum(image => 8 + image.Png.Length));
        stream.Write(number);

        foreach (var (type, png) in images) {
            stream.Write(Encoding.ASCII.GetBytes(type));
            BinaryPrimitives.WriteInt32BigEndian(number, 8 + png.Length);
            stream.Write(number);
            stream.Write(png);
        }

        return stream.ToArray();
    }

    private static string Folder() {
        var folder = Path.Combine(Root(), "artifacts", "icon");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string Root([CallerFilePath] string source = "") {
        var directory = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ScimStudio.slnx"))) {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
