using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace SDemo.App.Meeting.Host;

public partial class MainWindow : Window
{
    private readonly IServiceProvider _services;
    private readonly string? _webViewDataFolder;

    public MainWindow(IServiceProvider services, string? webViewDataFolder = null)
    {
        _services = services;
        _webViewDataFolder = webViewDataFolder;
        InitializeComponent();

        Web.BlazorWebViewInitializing += (_, args) =>
        {
            // 每个实例/数据目录一个 user data folder：多实例不抢缓存，崩溃残留也不会把新实例卡死
            if (!string.IsNullOrWhiteSpace(_webViewDataFolder))
                args.UserDataFolder = _webViewDataFolder;

            // 这台机器上 GPU 合成偶尔会让 WebView 只画一片背景色，需要时可用环境变量关掉
            if (Environment.GetEnvironmentVariable("SDEMO_MEETING_DISABLE_GPU") is "1" or "true")
            {
                args.EnvironmentOptions ??= new CoreWebView2EnvironmentOptions();
                args.EnvironmentOptions.AdditionalBrowserArguments =
                    (args.EnvironmentOptions.AdditionalBrowserArguments ?? string.Empty) + " --disable-gpu";
            }
        };

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Program.Log("窗口 Loaded");

        // 组件在进程内跑，服务直接来自组合根
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
                Program.Log($"导航完成 success={nav.IsSuccess} status={nav.HttpStatusCode}");

            core.ProcessFailed += (_, fail) =>
                Program.Log($"WebView2 进程异常：{fail.ProcessFailedKind} {fail.Reason}");

            // 页面空白时靠这条区分"没渲染"和"渲染了没内容"（组件炸掉不会自己说）
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            timer.Tick += async (_, _) =>
            {
                timer.Stop();
                try
                {
                    var probe = await core.ExecuteScriptAsync(
                        "JSON.stringify({text: document.body.innerText.length, app: (document.getElementById('app')||{}).innerHTML ? 'yes' : 'no'})");
                    Program.Log($"页面探查 {probe}");
                }
                catch (Exception ex)
                {
                    Program.Log($"页面探查失败：{ex.Message}");
                }
            };
            timer.Start();
        };
    }
}
