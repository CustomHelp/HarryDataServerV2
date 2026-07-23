using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace HarryShared.Config;

/// <summary>
/// Small modal dialog (code-only, no XAML) that lets a customer point a companion tool at its
/// <c>Harry.ini</c> — either by browsing to the file or typing/pasting a path (task 3). Used both
/// when no config was found at startup and from the "Config-Pfad ändern…" button in the top bar.
/// On OK the chosen path is validated (file must exist) and returned via <see cref="SelectedPath"/>;
/// the caller persists it with <see cref="ConfigLocator.SaveOverride"/>.
/// </summary>
public sealed class ConfigPathDialog : Window
{
    private readonly TextBox _pathBox;

    /// <summary>The validated Harry.ini path the user accepted, or null when cancelled.</summary>
    public string? SelectedPath { get; private set; }

    public ConfigPathDialog(string toolName, string? currentPath)
    {
        Title = "Change config path — " + toolName;
        Width = 620;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        var root = new StackPanel { Margin = new Thickness(16) };

        root.Children.Add(new TextBlock
        {
            Text = "Select the Harry.ini for this tool (browse for the file or type a path). " +
                   "The choice is saved per tool.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 12) };
        var browse = new Button { Content = "Browse…", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0) };
        browse.Click += OnBrowse;
        DockPanel.SetDock(browse, Dock.Right);
        row.Children.Add(browse);

        _pathBox = new TextBox
        {
            Text = currentPath ?? string.Empty,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(4),
        };
        row.Children.Add(_pathBox);
        root.Children.Add(row);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "OK", Width = 90, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(0, 4, 0, 4), IsDefault = true };
        ok.Click += OnOk;
        var cancel = new Button { Content = "Cancel", Width = 90, Padding = new Thickness(0, 4, 0, 4), IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        Content = root;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Harry.ini",
            Filter = "Harry.ini|Harry.ini|INI files (*.ini)|*.ini|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (!string.IsNullOrWhiteSpace(_pathBox.Text))
        {
            try
            {
                var dir = Path.GetDirectoryName(_pathBox.Text);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    dlg.InitialDirectory = dir;
            }
            catch { /* ignore an unparsable current path */ }
        }
        if (dlg.ShowDialog(this) == true)
            _pathBox.Text = dlg.FileName;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var path = _pathBox.Text?.Trim();
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            MessageBox.Show(this, "This file does not exist. Please select an existing Harry.ini.",
                "Invalid path", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        SelectedPath = path;
        DialogResult = true;
    }
}
