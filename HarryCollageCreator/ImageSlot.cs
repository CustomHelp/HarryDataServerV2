using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace HarryCollageCreator;

/// <summary>
/// One image placement being authored. Mirrors the server's <c>CollageImageSpec</c>
/// (the [ImageN] section of Collage.ini) plus the concrete sample file used for the
/// live preview. Any property change is observable so the canvas re-renders.
/// </summary>
public partial class ImageSlot : ObservableObject
{
    /// <summary>Concrete sample bitmap on disk used to render the preview (not stored in Collage.ini).</summary>
    [ObservableProperty] private string _sourceFilePath = string.Empty;

    /// <summary>Filename template written to Collage.ini (with the &lt;serial_pattern&gt; token).</summary>
    [ObservableProperty] private string _templateName = string.Empty;

    /// <summary>Match keyword(s) — the controller name; all tokens must appear in a filename at runtime.</summary>
    [ObservableProperty] private string _keyName = string.Empty;

    [ObservableProperty] private int _posX;
    [ObservableProperty] private int _posY;
    [ObservableProperty] private double _scale = 1.0;
    [ObservableProperty] private double _zoom = 1.0;
    [ObservableProperty] private int _cropX;
    [ObservableProperty] private int _cropY;
    [ObservableProperty] private int _cropWidth;
    [ObservableProperty] private int _cropHeight;
    [ObservableProperty] private bool _mirrorX;
    [ObservableProperty] private bool _mirrorY;

    public string FileName => string.IsNullOrEmpty(SourceFilePath) ? "(no image)" : Path.GetFileName(SourceFilePath);

    /// <summary>Short label for the slot list.</summary>
    public string Label => string.IsNullOrWhiteSpace(KeyName) ? FileName : $"{KeyName} · {FileName}";

    partial void OnSourceFilePathChanged(string value)
    {
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(Label));
    }

    partial void OnKeyNameChanged(string value) => OnPropertyChanged(nameof(Label));
}
