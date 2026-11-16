using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using SDemo.App.Meeting.Asr;
using SDemo.App.Meeting.Contracts;
using SDemo.App.Meeting.Core;
using SDemo.App.Meeting.Storage;

namespace SDemo.App.Meeting.Viewer;

public static class App
{
    public static IServiceProvider Services { get; private set; } = null!;

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            var dataDir = args.FirstOrDefault(a => a.StartsWith("--data-dir=", StringComparison.OrdinalIgnoreCase))?
                .Split('=', 2)[1];
            if (!string.IsNullOrWhiteSpace(dataDir))
                Environment.SetEnvironmentVariable(MeetingConstants.DataDirEnvVar, dataDir);

            Services = BuildServices();

            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var window = new MainWindow();
            app.MainWindow = window;
            app.Run(window);
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"启动失败：\n{ex}", "会议", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }

    private static IServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddWpfBlazorWebView();

        var root = MeetingConstants.ResolveDataDir();
        Directory.CreateDirectory(root);
        var database = MeetingDatabase.Open(Path.Combine(root, MeetingConstants.DbFileName));

        services.AddSingleton(database);
        services.AddSingleton<MeetingSession>();
        services.AddSingleton<MeetingService>();
        services.AddSingleton<MeetingQuery>();
        services.AddSingleton<TranscriptEditor>();
        services.AddSingleton(new MeetingModelCatalog());

        return services.BuildServiceProvider();
    }
}
