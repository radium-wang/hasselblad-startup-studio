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
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "X2DStartupStudio", "Logs", "usb-diagnostics");
            var version = typeof(Program).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion.Split('+')[0];
            try
            {
                Directory.CreateDirectory(folder);
                var result = WindowsUsbDiagnostics.Collect(version);
                File.WriteAllText(Path.Combine(folder, "result.json"), result);
            }
            catch
            {
                Environment.ExitCode = 1;
                try
                {
                    Directory.CreateDirectory(folder);
                    File.WriteAllText(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(new { schema = 1, version, metadataOnly = true, cameraEndpointAccessed = false, failed = true, message = "Windows USB 设备登记读取失败，请重新运行诊断。" }));
                }
                catch { /* A locked-down profile may also deny the per-user log directory. */ }
            }
            return;
        }
        using var mutex = new Mutex(true, "HasselbladStartupStudio-Windows", out var acquired);
        if (!acquired) { Environment.ExitCode = 2; return; }
        if (args.Contains("--transfer-diagnostics"))
        {
            RunTransferDiagnostic();
            return;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
    private static void RunTransferDiagnostic()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "X2DStartupStudio", "Logs", "transfer-diagnostics");
        try
        {
            Directory.CreateDirectory(folder);
            using var report = new StreamWriter(Path.Combine(folder, "result.txt"), false, new System.Text.UTF8Encoding(true)) { AutoFlush = true };
            void Log(string text) => report.WriteLine(DateTime.Now.ToString("O") + " " + text);
            var version = typeof(Program).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion.Split('+')[0];
            Log("Version=" + version + "; read-only transfer diagnostic; runtime USB mode may change");
            try
            {
                Log("Before=" + WindowsUsbDiagnostics.Collect(version));
                var resources = Path.Combine(AppContext.BaseDirectory, "Resources");
                using var link = new ManagedCameraLink(resources, Log, Log);
                var controller = new CameraController(link, resources, Path.Combine(folder, "unused-backup"));
                TransferDiagnostic.Run(controller, link, Log);
                Log("RESULT=PASS");
            }
            catch (Exception e)
            {
                Environment.ExitCode = 1;
                Log("RESULT=FAIL " + AdbDiagnosticText.Sanitize(e.Message, []));
            }
            finally
            {
                try { Log("After=" + WindowsUsbDiagnostics.Collect(version)); }
                catch { Log("After snapshot unavailable"); }
            }
        }
        catch { Environment.ExitCode = 1; }
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
                        Check.That(bottom != null && connect != null && bottom.Value.Y <= window.Bounds.Height - 20 && connect.Value.Y < 100, "Windows 原生窗口顶部连接或底部操作按钮超出可见区域。");
                        var settings = window.GetVisualDescendants().OfType<ScrollViewer>().Single(c => c.Name == "ImageSettingsScroll");
                        var editor = window.Editor.TranslatePoint(default, window)!.Value;
                        var canvas = window.Preview.TranslatePoint(default, window)!.Value;
                        Check.That(window.Editor.BasicOnly && window.Editor.IsVisible && window.Preview.IsVisible && editor.X < canvas.X && settings.Viewport.Height >= 50, "基础裁剪或相机画布预览超出布局。");
                        if (desktop.Args.Contains("--smoke-scroll")) { settings.ScrollToEnd(); window.UpdateLayout(); Check.That(settings.Offset.Y > 0, "小窗口中的图片设置无法滚动。"); }
                        using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height)); bitmap.Render(window);
                        bitmap.Save(Path.Combine(folder, "native-windows-ui.png"), PngBitmapEncoderOptions.Default);
                        File.WriteAllText(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(new { passed = true, windowsNativeUi = true, deviceAccessed = false, driverTested = false, macLayout = true, width = window.Bounds.Width, height = window.Bounds.Height })); desktop.Shutdown(0);
                    }
                    catch (Exception e) { File.WriteAllText(Path.Combine(folder, "failure.txt"), e.Message); desktop.Shutdown(1); }
                };
                timer.Start();
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
