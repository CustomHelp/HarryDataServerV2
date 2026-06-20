using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using HarryDataServer.Services;

namespace HarryDataServer.ViewModels;

/// <summary>One collage thumbnail: a frozen image plus its generation timestamp.</summary>
public sealed class CollageThumbVm
{
    public required BitmapImage Image { get; init; }
    public required string Timestamp { get; init; }
}

/// <summary>
/// Holds the last 4 generated collages as thumbnails for ucCollageControl. The
/// underlying <see cref="Queue{T}"/> keeps at most 4 frozen <see cref="BitmapImage"/>s;
/// the bound collection is rebuilt on the UI thread via the dispatcher after each new
/// collage so it can be displayed safely.
/// </summary>
public sealed class CollageViewModel
{
    private const int MaxThumbnails = 4;
    private const int ThumbnailWidth = 160;

    private readonly Queue<BitmapImage> _recent = new(MaxThumbnails);
    private readonly Queue<string> _timestamps = new(MaxThumbnails);

    public CollageViewModel(ICollageService collage)
    {
        collage.CollageGenerated += OnCollageGenerated;
    }

    public ObservableCollection<CollageThumbVm> Thumbnails { get; } = new();

    // Raised from a background (Task.Run) thread when a collage JPG has been written.
    private void OnCollageGenerated(string path, DateTime generatedAt)
    {
        var image = TryLoad(path);
        if (image is null)
            return;

        var stamp = generatedAt.ToString("HH:mm:ss");

        // Marshal the collection update onto the UI thread.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            return;

        dispatcher.Invoke(() =>
        {
            _recent.Enqueue(image);
            _timestamps.Enqueue(stamp);
            while (_recent.Count > MaxThumbnails) _recent.Dequeue();
            while (_timestamps.Count > MaxThumbnails) _timestamps.Dequeue();

            Thumbnails.Clear();
            var images = _recent.ToArray();
            var stamps = _timestamps.ToArray();
            // Newest first.
            for (var i = images.Length - 1; i >= 0; i--)
                Thumbnails.Add(new CollageThumbVm { Image = images[i], Timestamp = stamps[i] });
        });
    }

    /// <summary>Load a frozen thumbnail (OnLoad so the file handle is released immediately).</summary>
    private static BitmapImage? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.DecodePixelWidth = ThumbnailWidth; // decode small to keep memory low
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze(); // cross-thread safe
            return bmp;
        }
        catch
        {
            return null;
        }
    }
}
