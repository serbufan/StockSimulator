using StockSimulator.ViewModels;
using Wpf.Ui.Controls;

namespace StockSimulator;

/// <summary>主窗口：只负责装配 ViewModel，业务逻辑全部在 ViewModel / Core 中。</summary>
public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }
}
