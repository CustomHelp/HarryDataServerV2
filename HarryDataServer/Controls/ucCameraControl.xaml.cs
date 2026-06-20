using System.Windows.Controls;

namespace HarryDataServer.Controls;

/// <summary>One camera control (LEDs, telegram counter, last 3 telegrams, reconnect).
/// DataContext is a CameraViewModel.</summary>
public partial class ucCameraControl : UserControl
{
    public ucCameraControl() => InitializeComponent();
}
