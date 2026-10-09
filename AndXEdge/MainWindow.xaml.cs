using System.ComponentModel;
using System.Windows;
using AndX.Edge.ViewModels;

namespace AndX.Edge;

/// <summary>
/// AndXEdge 主窗口。
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await _viewModel.InitializeAsync();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // 同步停止本机反代并释放资源，确保进程退出前释放端口（Core 内部全部 ConfigureAwait(false)，无 UI 死锁）
        _viewModel.Dispose();
    }
}
