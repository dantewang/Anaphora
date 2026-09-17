using Anaphora.App.Editor;
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
    private MainWindow? window;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            try
            {
                var library = new RotationLibrary();
                library.Seed(Path.Combine(AppContext.BaseDirectory, "rotations"));

                host = new AnaphoraHost(AppOptions.Current, AppSettings.Load(), library);
                host.Start();
                window = new MainWindow(new MainViewModel(host));
                TrayIcon.SetIcons(this, [BuildTray(host, desktop)]);
                window.Show();
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

    private void ShowWindow()
    {
        if (window is null)
        {
            return;
        }

        window.Show();
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    /// <summary>
    /// Quick controls that work without opening the main window. The overlay
    /// itself ignores the mouse, so these have to live somewhere else.
    /// </summary>
    private TrayIcon BuildTray(AnaphoraHost host, IClassicDesktopStyleApplicationLifetime desktop)
    {
        var open = new NativeMenuItem("打开主界面");
        open.Click += (_, _) => ShowWindow();

        var status = new NativeMenuItem("显示状态面板") { ToggleType = MenuItemToggleType.CheckBox };
        status.Click += (_, _) => host.UpdateSettings(host.Settings with { StatusVisible = !host.Settings.StatusVisible });

        var hide = new NativeMenuItem("读不到 HUD 时隐藏") { ToggleType = MenuItemToggleType.CheckBox };
        hide.Click += (_, _) => host.UpdateSettings(host.Settings with { HideWhenHudAbsent = !host.Settings.HideWhenHudAbsent });

        var reset = new NativeMenuItem();
        reset.Click += (_, _) => host.ResetRotation();

        var skip = new NativeMenuItem("跳过当前一步");
        skip.Click += (_, _) => host.SkipStep();

        var exit = new NativeMenuItem("退出");
        exit.Click += (_, _) =>
        {
            if (window is not null)
            {
                window.ReallyClose = true;
            }

            desktop.Shutdown();
        };

        void Sync()
        {
            status.IsChecked = host.Settings.StatusVisible;
            hide.IsChecked = host.Settings.HideWhenHudAbsent;
            reset.Header = host.ResetHotkeyText is { } hotkey ? $"轴回到开头（{hotkey}）" : "轴回到开头";
        }

        Sync();
        host.SettingsChanged += Sync;

        var tray = new TrayIcon
        {
            Icon = DrawIcon(),
            ToolTipText = "Anaphora",
            Menu = [open, new NativeMenuItemSeparator(), status, hide, new NativeMenuItemSeparator(), reset, skip, new NativeMenuItemSeparator(), exit],
        };
        tray.Clicked += (_, _) => ShowWindow();
        return tray;
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
