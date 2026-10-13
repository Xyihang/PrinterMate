using System.Windows;
using PrinterMate.App.ViewModels;

namespace PrinterMate.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }
}
