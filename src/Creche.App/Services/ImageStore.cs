using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Creche.App.Services;

/// <summary>Stores pictures (child photos, the logo) in the local images folder, resized on import.</summary>
public static class ImageStore
{
    private static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tif", ".tiff"];
    private static readonly Dictionary<string, BitmapSource> Cache = [];

    public const string DialogFilter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff";

    public static string Folder { get; set; } = "";

    public static bool IsImage(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Decodes, shrinks to <paramref name="maxPixels"/> on the longest side, saves as JPEG and returns the file name.</summary>
    public static string Import(string sourcePath, int maxPixels)
    {
        var frame = BitmapFrame.Create(new Uri(sourcePath), BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        BitmapSource bitmap = frame;
        double scale = (double)maxPixels / Math.Max(frame.PixelWidth, frame.PixelHeight);
        if (scale < 1)
            bitmap = new TransformedBitmap(frame, new ScaleTransform(scale, scale));

        // JPEG has no transparency: flatten onto white.
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new System.Windows.Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight));
            dc.DrawImage(bitmap, new System.Windows.Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight));
        }
        var flattened = new RenderTargetBitmap(bitmap.PixelWidth, bitmap.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        flattened.Render(visual);

        string fileName = $"{Guid.NewGuid():N}.jpg";
        var encoder = new JpegBitmapEncoder { QualityLevel = 88 };
        encoder.Frames.Add(BitmapFrame.Create(flattened));
        using var stream = File.Create(Path.Combine(Folder, fileName));
        encoder.Save(stream);
        return fileName;
    }

    /// <summary>Loads a stored picture fully into memory (the file is not locked). Null when missing or unreadable.</summary>
    public static BitmapSource? Load(string? fileName, int decodeWidth = 0)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        string key = $"{fileName}|{decodeWidth}";
        if (Cache.TryGetValue(key, out var cached)) return cached;
        string path = PathOf(fileName);
        if (!File.Exists(path)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (decodeWidth > 0) image.DecodePixelWidth = decodeWidth;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            if (Cache.Count > 300) Cache.Clear();
            Cache[key] = image;
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or ArgumentException)
        {
            return null;
        }
    }

    public static string PathOf(string fileName) => Path.Combine(Folder, Path.GetFileName(fileName));
}
