using System.Windows;

namespace SDemo.App.Meeting.Host;

public partial class MainWindow : Window
{
    private readonly IServiceProvider _services;

    public MainWindow(IServiceProvider services)
    {
        _services = services;
        InitializeComponent();

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Program.Log("窗口 Loaded");

        // 组件在进程内跑，服务直接来自组合根（Loaded 时给，控件已建好）
        Web.Services = _services;

        var view = Web.WebView;
        if (view is null)
        {
            Program.Log("WebView 还没建好，跳过诊断钩子");
            return;
        }

        view.CoreWebView2InitializationCompleted += (_, args) =>
        {
            if (!args.IsSuccess)
            {
                Program.Log($"WebView2 初始化失败：{args.InitializationException?.Message}");
                return;
            }

            var core = view.CoreWebView2;
            if (core is null)
            {
                Program.Log("CoreWebView2 为空");
                return;
            }

            Program.Log($"WebView2 就绪，入口页 {core.Source}");
            core.NavigationCompleted += (_, nav) =>
                Program.Log($"导航完成 success={nav.IsSuccess} status={nav.HttpStatusCode} error={nav.WebErrorStatus}");
            core.ProcessFailed += (_, fail) =>
                Program.Log($"WebView2 进程异常：{fail.ProcessFailedKind} {fail.Reason}");
        };
    }
}
