using System.Windows;

namespace HarryPareto;

/// <summary>
/// Entry point for HarryPareto. The window builds its own view model and connects on load
/// (auto-connect with saved settings, else the connection dialog) — see <see cref="MainViewModel"/>.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            new MainWindow().Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"HarryPareto konnte nicht gestartet werden:\n\n{ex.Message}",
                "Startfehler", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }
}
