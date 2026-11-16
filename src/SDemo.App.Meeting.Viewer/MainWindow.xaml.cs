using System.Windows;

namespace SDemo.App.Meeting.Viewer;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // 组件在进程内跑，服务直接从 WPF 的依赖注入容器拿
        Web.Services = App.Services;
    }
}
