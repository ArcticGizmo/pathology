using Avalonia.Controls;

namespace Pathology.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Icon = AppIcon.Load();
    }
}
