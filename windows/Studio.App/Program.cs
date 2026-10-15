using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using System.Text.Json;

namespace StartupStudio;
public static class Program
{
    [STAThread] public static void Main(string[] args)
    {
        if (args.Contains("--usb-diagnostics"))
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "usb-diagnostics"); Directory.CreateDirectory(folder);
            var version = typeof(Program).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion.Split('+')[0];
            try { File.WriteAllText(Path.Combine(folder, "result.json"), WindowsUsbDiagnostics.Collect(version)); }
            catch { File.WriteAllText(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(new { schema = 1, version, metadataOnly = true, cameraEndpointAccessed = false, failed = true, message = "Windows USB 设备登记读取失败，请重新运行诊断。" })); Environment.ExitCode = 1; }
            return;
        }
        using var mutex = new Mutex(true, "HasselbladStartupStudio-Windows", out var acquired);
        if (!acquired) return;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<StudioApplication>().UsePlatformDetect();
}
public sealed class StudioApplication : Application
{
    public override void Initialize() { RequestedThemeVariant = ThemeVariant.Light; Styles.Add(new FluentTheme()); }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new StudioWindow(); desktop.MainWindow = window;
            if (desktop.Args?.Contains("--smoke-ui") == true)
            {
                window.LinkFactory = (_, _) => throw new StudioException("离线启动检查禁止 USB 访问。");
                var pixels = new byte[640 * 480 * 4];
                byte[][] bars = [[0, 0, 255], [0, 255, 0], [255, 0, 0], [0, 255, 255], [255, 255, 0], [255, 0, 255]];
                for (var y = 0; y < 480; y++) for (var x = 0; x < 640; x++)
                {
                    var color = bars[Math.Min(5, x * 6 / 640)]; var at = (y * 640 + x) * 4;
                    for (var c = 0; c < 3; c++) pixels[at + c] = (byte)(color[c] * (y < 360 ? 1 : .5));
                }
                window.SetImage(new StartupImage(640, 480, pixels, 4), "离线 RGB 测试图");
                if (desktop.Args.Contains("--smoke-crop")) window.Mode.SelectedIndex = 1;
                if (desktop.Args.Contains("--smoke-draw")) window.Editor.DrawNewSelection = true;
                if (desktop.Args.Contains("--smoke-small")) { window.Width = 1080; window.Height = 640; }
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop(); var folder = Path.Combine(AppContext.BaseDirectory, "smoke-results"); Directory.CreateDirectory(folder);
                    try
                    {
                        Check.That(OperatingSystem.IsWindows() && window.IsVisible && window.Bounds.Width > 1000 && !window.Install.IsEnabled && !window.Restore.IsEnabled, "Windows 原生窗口启动检查失败。");
                        var bottom = window.Install.TranslatePoint(new Point(0, window.Install.Bounds.Height), window);
                        var connect = window.Connect.TranslatePoint(default, window);
                        Check.That(bottom != null && connect != null && bottom.Value.Y <= window.Bounds.Height - 20 && connect.Value.Y > window.Bounds.Height - 120, "Windows 原生窗口底部操作按钮超出可见区域。");
                        var thumbnail = window.GetVisualDescendants().OfType<CameraPreview>().Single(c => c.Name == "OutputThumbnail");
                        var settings = window.GetVisualDescendants().OfType<ScrollViewer>().Single(c => c.Name == "ImageSettingsScroll");
                        var menu = window.GetVisualDescendants().OfType<StackPanel>().Single(c => c.Name == "ImageSettings");
                        var before = thumbnail.TranslatePoint(default, window)!.Value;
                        var settingsTop = settings.TranslatePoint(default, window)!.Value;
                        Check.That(thumbnail.Bounds.Height >= 110 && thumbnail.Bounds.Width >= 260 && before.Y + thumbnail.Bounds.Height <= settingsTop.Y && settings.Viewport.Height >= 50, "成品预览或设置菜单被遮挡。");
                        settings.ScrollToEnd(); window.UpdateLayout();
                        Check.That(thumbnail.TranslatePoint(default, window) == before && settings.Offset.Y > 0, "滚动菜单移动了固定成品预览或菜单无法滚动。");
                        var bar = settings.GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Orientation == Avalonia.Layout.Orientation.Vertical);
                        var barLeft = bar.TranslatePoint(default, window)!.Value.X;
                        var menuRight = menu.TranslatePoint(new Point(menu.Bounds.Width, 0), window)!.Value.X;
                        Check.That(bar.IsVisible && menuRight + 8 <= barLeft, "滚动条遮挡图片设置菜单。");
                        if (window.Editor.IsVisible)
                        {
                            var button = window.DrawCrop.TranslatePoint(default, window)!.Value;
                            var editor = window.Editor.TranslatePoint(default, window)!.Value;
                            Check.That(window.DrawCrop.IsVisible && window.DrawCrop.IsEnabled && button.X >= editor.X && button.X + window.DrawCrop.Bounds.Width <= editor.X + window.Editor.Bounds.Width && button.Y + window.DrawCrop.Bounds.Height <= editor.Y, "自由画框按钮超出裁切单元右上角。");
                        }
                        if (!desktop.Args.Contains("--smoke-scroll")) { settings.ScrollToHome(); window.UpdateLayout(); }
                        using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height)); bitmap.Render(window);
                        bitmap.Save(Path.Combine(folder, "native-windows-ui.png"), PngBitmapEncoderOptions.Default);
                        File.WriteAllText(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(new { passed = true, windowsNativeUi = true, deviceAccessed = false, driverTested = false, pinnedPreview = true, menuScrollbarSeparated = true, width = window.Bounds.Width, height = window.Bounds.Height, thumbnailHeight = thumbnail.Bounds.Height })); desktop.Shutdown(0);
                    }
                    catch (Exception e) { File.WriteAllText(Path.Combine(folder, "failure.txt"), e.Message); desktop.Shutdown(1); }
                };
                timer.Start();
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
