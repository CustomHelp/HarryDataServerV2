using System.Globalization;
using System.Windows;
using System.Windows.Media;
using HarryShared.Theming;

namespace HarryPareto;

/// <summary>
/// Connection dialog: edit IP/Port/DB/User/Password (task E1), test the connection with a status
/// lamp, and on success return the settings (the caller persists them, DPAPI-encrypted).
/// </summary>
public partial class ConnectionDialog : Window
{
    /// <summary>Populated only when the user connected successfully (DialogResult == true).</summary>
    public ParetoSettings Result { get; private set; }

    public ConnectionDialog(ParetoSettings settings)
    {
        ThemeManager.Initialize();
        InitializeComponent();
        Result = settings;

        IpBox.Text = settings.Ip;
        PortBox.Text = settings.Port.ToString(CultureInfo.InvariantCulture);
        DbBox.Text = settings.Database;
        UserBox.Text = settings.User;
        PwBox.Password = settings.Password;
    }

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        var candidate = new ParetoSettings
        {
            Ip = IpBox.Text.Trim(),
            Port = int.TryParse(PortBox.Text.Trim(), out var p) ? p : 3306,
            Database = string.IsNullOrWhiteSpace(DbBox.Text) ? "camera_data" : DbBox.Text.Trim(),
            User = UserBox.Text.Trim(),
            Password = PwBox.Password,
            RefreshSeconds = Result.RefreshSeconds,
        };

        SetStatus(Brushes.Orange, "Connecting …");
        IsEnabled = false;
        try
        {
            var ok = await new ParetoDb(candidate).CanConnectAsync().ConfigureAwait(true);
            if (ok)
            {
                Result = candidate;
                DialogResult = true;
                Close();
                return;
            }
            SetStatus(Brushes.OrangeRed, "Connection failed — check the data.");
        }
        catch (Exception ex)
        {
            SetStatus(Brushes.OrangeRed, "Error: " + ex.Message);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void SetStatus(Brush brush, string text)
    {
        StatusLed.Fill = brush;
        StatusText.Text = text;
    }
}
