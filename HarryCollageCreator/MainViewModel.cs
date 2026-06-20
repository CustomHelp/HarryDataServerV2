using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HarryShared.Config;
using Microsoft.Win32;

namespace HarryCollageCreator;

/// <summary>
/// HarryCollageCreator view model: a visual editor that authors a Collage.ini layout.
/// Load sample BMPs as image slots, place/zoom/crop/mirror them on a canvas with a
/// live GDI+ composite (same semantics as the server's renderer), then save Collage.ini.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private static readonly Regex ControllerPattern = new(@"M\d+_ST\d+_KF\d+", RegexOptions.IgnoreCase);

    private readonly HarryConfig? _config;
    private bool _suspendRender;

    public MainViewModel(HarryConfig? config)
    {
        _config = config;
        if (config is not null && !string.IsNullOrWhiteSpace(config.CollageIniPath))
            CurrentFile = config.CollageIniPath;

        Slots.CollectionChanged += (_, _) => Render();
        Render();
    }

    public string AppName => "HarryCollageCreator — Collage.ini Editor";
    public string AppVersion => "v" + (GetType().Assembly.GetName().Version?.ToString(3) ?? "2.0.0");

    public ObservableCollection<ImageSlot> Slots { get; } = new();

    [ObservableProperty] private int _canvasWidth = 320;
    [ObservableProperty] private int _canvasHeight = 650;
    [ObservableProperty] private string _backgroundColor = "White";
    [ObservableProperty] private BitmapSource? _preview;
    [ObservableProperty] private string _statusMessage = "Add sample BMP images, arrange them, then save Collage.ini.";
    [ObservableProperty] private string _currentFile = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveSlotCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveDownCommand))]
    [NotifyCanExecuteChangedFor(nameof(FitToCanvasCommand))]
    [NotifyCanExecuteChangedFor(nameof(CenterCommand))]
    private ImageSlot? _selectedSlot;

    partial void OnCanvasWidthChanged(int value) => Render();
    partial void OnCanvasHeightChanged(int value) => Render();
    partial void OnBackgroundColorChanged(string value) => Render();

    partial void OnSelectedSlotChanged(ImageSlot? oldValue, ImageSlot? newValue)
    {
        if (oldValue is not null) oldValue.PropertyChanged -= OnSlotPropertyChanged;
        if (newValue is not null) newValue.PropertyChanged += OnSlotPropertyChanged;
        Render(); // re-render to move the selection outline
    }

    private void OnSlotPropertyChanged(object? sender, PropertyChangedEventArgs e) => Render();

    private void Render()
    {
        if (_suspendRender)
            return;
        try
        {
            Preview = CollagePreviewRenderer.Render(
                CanvasWidth, CanvasHeight, BackgroundColor, Slots,
                SelectedSlot is null ? -1 : Slots.IndexOf(SelectedSlot));
        }
        catch (Exception ex)
        {
            StatusMessage = "Render failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private void AddImages()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Image files|*.bmp;*.png;*.jpg;*.jpeg|All files|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog() != true)
            return;

        foreach (var file in dialog.FileNames)
            Slots.Add(MakeSlot(file));

        SelectedSlot = Slots.LastOrDefault();
        StatusMessage = $"Added {dialog.FileNames.Length} image(s). {Slots.Count} slot(s) total.";
    }

    /// <summary>Build a slot for a sample file with sensible defaults (centred, guessed KeyName/TemplateName).</summary>
    private ImageSlot MakeSlot(string file)
    {
        var name = Path.GetFileName(file);
        var key = ControllerPattern.Match(name) is { Success: true } m ? m.Value : string.Empty;

        // TemplateName uses the <serial_pattern> token in place of a leading 12-char serial.
        var template = name;
        if (name.Length > 13 && name[12] == '_')
            template = "<serial_pattern>" + name[12..];

        var slot = new ImageSlot
        {
            SourceFilePath = file,
            KeyName = key,
            TemplateName = template,
            PosX = CanvasWidth / 2,
            PosY = CanvasHeight / 2,
            Scale = 1.0,
            Zoom = 1.0,
        };
        slot.PropertyChanged += OnSlotPropertyChanged;
        return slot;
    }

    private bool HasSelection => SelectedSlot is not null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RemoveSlot()
    {
        if (SelectedSlot is null) return;
        var idx = Slots.IndexOf(SelectedSlot);
        SelectedSlot.PropertyChanged -= OnSlotPropertyChanged;
        Slots.Remove(SelectedSlot);
        SelectedSlot = Slots.Count == 0 ? null : Slots[Math.Min(idx, Slots.Count - 1)];
        StatusMessage = $"Removed slot. {Slots.Count} remaining.";
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void MoveUp()
    {
        var i = Slots.IndexOf(SelectedSlot!);
        if (i > 0) { Slots.Move(i, i - 1); Render(); }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void MoveDown()
    {
        var i = Slots.IndexOf(SelectedSlot!);
        if (i >= 0 && i < Slots.Count - 1) { Slots.Move(i, i + 1); Render(); }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Center()
    {
        if (SelectedSlot is null) return;
        SelectedSlot.PosX = CanvasWidth / 2;
        SelectedSlot.PosY = CanvasHeight / 2;
    }

    /// <summary>Reset crop to the whole image and pick a Scale so it fits the canvas.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void FitToCanvas()
    {
        if (SelectedSlot is null || !File.Exists(SelectedSlot.SourceFilePath))
            return;
        try
        {
            using var img = System.Drawing.Image.FromFile(SelectedSlot.SourceFilePath);
            _suspendRender = true;
            SelectedSlot.CropX = SelectedSlot.CropY = SelectedSlot.CropWidth = SelectedSlot.CropHeight = 0;
            SelectedSlot.Zoom = 1.0;
            SelectedSlot.Scale = Math.Min((double)CanvasWidth / img.Width, (double)CanvasHeight / img.Height);
            SelectedSlot.PosX = CanvasWidth / 2;
            SelectedSlot.PosY = CanvasHeight / 2;
        }
        finally
        {
            _suspendRender = false;
            Render();
        }
    }

    [RelayCommand]
    private void New()
    {
        foreach (var s in Slots) s.PropertyChanged -= OnSlotPropertyChanged;
        Slots.Clear();
        SelectedSlot = null;
        CanvasWidth = 320;
        CanvasHeight = 650;
        BackgroundColor = "White";
        CurrentFile = string.Empty;
        StatusMessage = "New empty layout.";
    }

    [RelayCommand]
    private void Open()
    {
        var dialog = new OpenFileDialog { Filter = "Collage.ini (*.ini)|*.ini|All files|*.*" };
        if (!string.IsNullOrWhiteSpace(CurrentFile))
            dialog.InitialDirectory = Path.GetDirectoryName(CurrentFile);
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var doc = CollageIniIo.Load(dialog.FileName);
            _suspendRender = true;
            foreach (var s in Slots) s.PropertyChanged -= OnSlotPropertyChanged;
            Slots.Clear();
            CanvasWidth = doc.CanvasWidth;
            CanvasHeight = doc.CanvasHeight;
            BackgroundColor = doc.BackgroundColor;
            foreach (var slot in doc.Slots)
            {
                slot.PropertyChanged += OnSlotPropertyChanged;
                Slots.Add(slot);
            }
            CurrentFile = dialog.FileName;
            _suspendRender = false;
            Render();
            StatusMessage = $"Loaded {Slots.Count} slot(s) from {Path.GetFileName(dialog.FileName)}. " +
                            "Assign a sample image to each slot to preview it.";
        }
        catch (Exception ex)
        {
            _suspendRender = false;
            StatusMessage = "Open failed: " + ex.Message;
        }
    }

    /// <summary>Assign / replace the sample image used to preview the selected slot.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void AssignImage()
    {
        if (SelectedSlot is null) return;
        var dialog = new OpenFileDialog { Filter = "Image files|*.bmp;*.png;*.jpg;*.jpeg|All files|*.*" };
        if (dialog.ShowDialog() != true)
            return;
        SelectedSlot.SourceFilePath = dialog.FileName;
        StatusMessage = $"Assigned {Path.GetFileName(dialog.FileName)} to the selected slot.";
    }

    [RelayCommand]
    private void Save()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Collage.ini (*.ini)|*.ini|All files|*.*",
            FileName = string.IsNullOrWhiteSpace(CurrentFile) ? "Collage.ini" : Path.GetFileName(CurrentFile),
        };
        if (!string.IsNullOrWhiteSpace(CurrentFile))
            dialog.InitialDirectory = Path.GetDirectoryName(CurrentFile);
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var doc = new CollageDocument
            {
                CanvasWidth = CanvasWidth,
                CanvasHeight = CanvasHeight,
                BackgroundColor = BackgroundColor,
            };
            doc.Slots.AddRange(Slots);
            CollageIniIo.Save(dialog.FileName, doc);
            CurrentFile = dialog.FileName;
            StatusMessage = $"Saved {Slots.Count} slot(s) to {Path.GetFileName(dialog.FileName)}.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Save failed: " + ex.Message;
            MessageBox.Show(ex.Message, "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Export the current composite as a PNG (for visual comparison against a V1 collage).</summary>
    [RelayCommand]
    private void ExportPreview()
    {
        if (Preview is null) return;
        var dialog = new SaveFileDialog { Filter = "PNG image (*.png)|*.png", FileName = "collage-preview.png" };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            using var fs = new FileStream(dialog.FileName, FileMode.Create);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(Preview));
            encoder.Save(fs);
            StatusMessage = $"Exported preview to {Path.GetFileName(dialog.FileName)}.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Export failed: " + ex.Message;
        }
    }
}
