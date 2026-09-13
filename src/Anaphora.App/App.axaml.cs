using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Anaphora.App;

public partial class App : Application
{
    private AnaphoraHost? host;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            try
            {
                host = new AnaphoraHost(AppOptions.Current, AppSettings.Load());
                host.Start();
                TrayIcon.SetIcons(this, [BuildTray(host, desktop)]);
            }
            catch (Exception ex)
            {
                Log.Write($"startup failed: {ex}");
                desktop.Shutdown(1);
            }

            desktop.Exit += (_, _) => host?.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// The only controls there are until the configuration window is designed.
    /// The overlay itself ignores the mouse, so folding the status panel has to
    /// live somewhere else, and the tray is the one place that needs no design pass.
    /// </summary>
    private static TrayIcon BuildTray(AnaphoraHost host, IClassicDesktopStyleApplicationLifetime desktop)
    {
        var status = new NativeMenuItem("显示状态面板")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = host.StatusVisible,
        };
        status.Click += (_, _) => status.IsChecked = host.ToggleStatus();

        var reset = new NativeMenuItem("轴回到开头");
        reset.Click += (_, _) => host.ResetRotation();

        var skip = new NativeMenuItem("跳过当前一步");
        skip.Click += (_, _) => host.SkipStep();

        var exit = new NativeMenuItem("退出");
        exit.Click += (_, _) => desktop.Shutdown();

        return new TrayIcon
        {
            Icon = DrawIcon(),
            ToolTipText = "Anaphora",
            Menu = [status, reset, skip, new NativeMenuItemSeparator(), exit],
        };
    }

    /// <summary>An amber diamond, drawn rather than shipped: there is no icon artwork yet.</summary>
    private static WindowIcon DrawIcon()
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(32, 32), new Vector(96, 96));
        using (DrawingContext context = bitmap.CreateDrawingContext())
        {
            var diamond = new StreamGeometry();
            using (StreamGeometryContext path = diamond.Open())
            {
                path.BeginFigure(new Point(16, 2), true);
                path.LineTo(new Point(30, 16));
                path.LineTo(new Point(16, 30));
                path.LineTo(new Point(2, 16));
                path.EndFigure(true);
            }

            context.DrawGeometry(new SolidColorBrush(Color.Parse("#F0B429")), new Pen(new SolidColorBrush(Color.Parse("#0A0B0B")), 2), diamond);
        }

        return new WindowIcon(bitmap);
    }
}
