using System.Windows;

namespace HarryAnalysis;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => { ScanBox.Focus(); };
    }
}
