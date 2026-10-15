using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Reflection;
namespace StartupStudio;

public sealed class StudioWindow : Window
{
    private static readonly string BuildVersion = typeof(StudioWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "测试版";
    public readonly Button Connect = new() { Content = "连接相机", Name = "Connect", Padding = new Thickness(20, 12) };
    public readonly Button Import = new() { Content = "导入图片…", Name = "Import", HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(12) };
    public readonly Button Install = new() { Content = "安装自定义画面", Name = "Install", Padding = new Thickness(20, 12) };
    public readonly Button Restore = new() { Content = "恢复原厂", Name = "Restore", Padding = new Thickness(20, 12) };
    public readonly ComboBox Colors = new() { ItemsSource = new[] { "RGB 彩色 · 8-bit/通道", "黑白灰度 · 8-bit" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
    public readonly ComboBox Mode = new() { ItemsSource = new[] { "保留黑边 · 原尺寸居中", "裁剪 · 原尺寸居中" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
    public readonly CropEditor Editor = new() { Height = 180, Name = "CropEditor", BasicOnly = true };
    public readonly CameraPreview Preview = new() { Name = "CameraPreview", MinHeight = 160 };
    public readonly TextBlock Status = new() { Text = "导入图片预览，或连接相机检查", TextWrapping = TextWrapping.Wrap, Name = "Status" };
    private readonly TextBlock device = Text("等待连接相机", 14), current = Text("尚未检测", 14), filename = Text("未导入图片", 12), dimensions = Text("1024 × 768 画布", 12);
    private readonly TextBlock note = Text("仅支持第一代 X2D 100C / 907X 与 CFV 100C，原厂固件 4.2.0。", 12);
    private readonly TextBlock logs = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 22, Margin = new Thickness(0, 0, 8, 0) };
    private readonly ScrollViewer logScroll;
    private readonly Button reset = new() { Content = "重置裁剪" }, export = new() { Content = "导出成品 PNG…" };
    private readonly TextBlock previewTitle = Heading("相机屏幕预览"), previewHint = Text("黑色区域是屏幕画布；图片不拉伸、不放大。", 12);
    private readonly TextBlock editorHint = Text("保留原尺寸，图片四周由相机填充黑边。", 11);
    private readonly ProgressBar progress = new() { Minimum = 0, Maximum = 1, Height = 4 };
    private readonly Queue<string> entries = new();
    private PhotoDocument? source; private StartupImage? prepared; private bool settingImage, importing; private bool busy, ready, canRestore, canInstall;
    public string ResourcesPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "Resources");
    public string BackupRoot { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "X2DStartupStudio", "Backups");
    public string DiagnosticsRoot { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "X2DStartupStudio", "Logs");
    public Func<Action<string>, Action<string>, IManagedCameraLink> LinkFactory { get; set; }
    public StartupImage? Output => prepared;

    private static TextBlock Text(string value, double size) => new() { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#606770")) };
    private static TextBlock Heading(string value) => new() { Text = value, FontSize = 17, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 14) };
    private static Border Card(Control content) => new() { Child = content, Padding = new Thickness(20), CornerRadius = new CornerRadius(14), Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.Parse("#E4E6EA")), BorderThickness = new Thickness(1) };
    private static void Put(Grid grid, Control c, int row, int col) { Grid.SetRow(c, row); Grid.SetColumn(c, col); grid.Children.Add(c); }
    public StudioWindow()
    {
        LinkFactory = (log, trace) => new ManagedCameraLink(ResourcesPath, log, trace);
        Title = "Hasselblad Startup Studio · Windows " + BuildVersion; Width = 1260; Height = 800; MinWidth = 1080; MinHeight = 640;
        Background = new SolidColorBrush(Color.Parse("#F3F4F6")); FontFamily = new FontFamily("Microsoft YaHei UI, PingFang SC, sans-serif"); FontSize = 14;
        var layout = new Grid { RowDefinitions = new("Auto,Auto,*,Auto"), Margin = new Thickness(28), RowSpacing = 18 };
        // A native desktop can clamp the requested window height to its work
        // area. Keep the footer in the actual client area, including at high DPI.
        void FitClient() => layout.Height = Math.Max(0, ClientSize.Height - layout.Margin.Top - layout.Margin.Bottom);
        layout.VerticalAlignment = VerticalAlignment.Top;
        Opened += (_, _) => FitClient(); SizeChanged += (_, _) => FitClient();
        var heading = new Grid { ColumnDefinitions = new("Auto,*,Auto,Auto"), ColumnSpacing = 12 };
        var icon = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(11), Background = new SolidColorBrush(Color.Parse("#FAEADD")), Child = new TextBlock { Text = "▧", FontSize = 23, Foreground = new SolidColorBrush(Color.Parse("#D76B28")), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        var intro = new StackPanel { Spacing = 3 }; intro.Children.Add(new TextBlock { Text = "开机画面", FontSize = 24, FontWeight = FontWeight.SemiBold }); intro.Children.Add(Text("Hasselblad X2D / 907X · CFV 100C", 12));
        var version = new Border { Padding = new Thickness(9, 5), CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.Parse("#E9EAED")), VerticalAlignment = VerticalAlignment.Center, Child = Text("测试版 " + BuildVersion, 11) };
        Connect.VerticalAlignment = VerticalAlignment.Center;
        Put(heading, icon, 0, 0); Put(heading, intro, 0, 1); Put(heading, version, 0, 2); Put(heading, Connect, 0, 3); Put(layout, heading, 0, 0);
        var info = new Grid { ColumnDefinitions = new("*,*") }; var left = new StackPanel { Spacing = 5 }; left.Children.Add(Text("相机", 11)); left.Children.Add(device); var right = new StackPanel { Spacing = 5 }; right.Children.Add(Text("当前画面", 11)); right.Children.Add(current); Put(info, left, 0, 0); Put(info, right, 0, 1); Put(layout, Card(info), 1, 0);
        var columns = new Grid { ColumnDefinitions = new("280,*,272"), ColumnSpacing = 18 };
        var editorPanel = new StackPanel { Name = "ImageSettings", Spacing = 11, Margin = new Thickness(0, 0, 10, 0) };
        var imageHeading = Heading("准备图片"); imageHeading.Margin = default;
        editorPanel.Children.Add(imageHeading); editorPanel.Children.Add(Import); editorPanel.Children.Add(filename);
        editorPanel.Children.Add(Text("颜色", 11)); editorPanel.Children.Add(Colors);
        editorPanel.Children.Add(Text("显示方式", 11)); editorPanel.Children.Add(Mode); editorPanel.Children.Add(Editor);
        editorPanel.Children.Add(editorHint); editorPanel.Children.Add(reset);
        editorPanel.Children.Add(Text("PNG／JPEG · 宽 ≤ 1024、高 ≤ 768 像素。\n按原尺寸居中，不缩放；透明区域为黑色。", 11));
        var settingsScroll = new ScrollViewer { Name = "ImageSettingsScroll", Content = editorPanel, AllowAutoHide = false, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        Put(columns, Card(settingsScroll), 0, 0);
        var previewPanel = new Grid { RowDefinitions = new("Auto,*,Auto,Auto"), RowSpacing = 14 };
        previewTitle.Margin = default; Put(previewPanel, previewTitle, 0, 0);
        Put(previewPanel, Preview, 1, 0); Put(previewPanel, dimensions, 2, 0);
        var previewActions = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12 };
        Put(previewActions, previewHint, 0, 0); Put(previewActions, export, 0, 1); Put(previewPanel, previewActions, 3, 0);
        ToolTip.SetTip(export, "导出右侧屏幕预览中的原尺寸图片；黑色区域由相机画布填充。");
        Put(columns, Card(previewPanel), 0, 1);
        var logPanel = new Grid { RowDefinitions = new("Auto,*") }; Put(logPanel, Heading("操作日志"), 0, 0); logScroll = new ScrollViewer { Content = logs, AllowAutoHide = false, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }; Put(logPanel, logScroll, 1, 0); Put(columns, Card(logPanel), 0, 2); Put(layout, columns, 2, 0);
        var footer = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 12 }; var statusPanel = new StackPanel { Spacing = 7 }; statusPanel.Children.Add(Status); statusPanel.Children.Add(note); statusPanel.Children.Add(progress); Put(footer, statusPanel, 0, 0); Put(footer, Restore, 0, 1); Install.Background = new SolidColorBrush(Color.Parse("#D76B28")); Install.Foreground = Brushes.White; Put(footer, Install, 0, 2); Put(layout, footer, 3, 0); Content = layout;
        Connect.Click += async (_, _) => await Perform(0); Install.Click += async (_, _) => await Perform(1); Restore.Click += async (_, _) => await Perform(2);
        Import.Click += async (_, _) => await ImportFile(); export.Click += async (_, _) => await ExportFile();
        Colors.SelectionChanged += (_, _) => UpdatePreview();
        Mode.SelectionChanged += (_, _) =>
        {
            Editor.Active = Mode.SelectedIndex == 1 && !busy && !importing; UpdatePreview();
            editorHint.Text = Mode.SelectedIndex == 1 ? "拖动画框或四角调整范围；框外拖拽可重新框选。" : "保留原尺寸，图片四周由相机填充黑边。";
            if (Mode.SelectedIndex == 1 && source != null)
            {
                Status.Text = "已进入裁剪：在左侧图片上拖动画框或四角。";
                Dispatcher.UIThread.Post(() => Editor.BringIntoView(), DispatcherPriority.Loaded);
            }
        };
        Editor.Changed = _ => { if (!settingImage) UpdatePreview(); }; reset.Click += (_, _) => { if (source != null) Editor.Crop = source.FullImage; };
        Closed += (_, _) => { settingImage = true; Editor.Image = null; source?.Dispose(); source = null; prepared = null; };
        Closing += (_, e) => { if (busy || importing) { e.Cancel = true; Status.Text = importing ? "正在处理图片，请稍候。" : "操作进行中，请等待备份及 USB 状态核对完成。"; } };
        Log(BuildVersion + " · 已准备好，请连接相机"); UpdateButtons();
    }
    public void SetImage(StartupImage image, string name) => SetPhoto(PhotoDocument.FromImage(image), name);
    public void SetPhoto(PhotoDocument image, string name)
    {
        settingImage = true;
        try
        {
            var previous = source; source = image;
            try { Editor.Image = image; }
            catch { source = previous; if (!ReferenceEquals(previous, image)) image.Dispose(); Editor.Image = previous; throw; }
            if (!ReferenceEquals(previous, image)) previous?.Dispose();
        }
        finally { settingImage = false; }
        filename.Text = name + $"\n原图 {image.OriginalWidth} × {image.OriginalHeight} · {image.BitDepth}-bit";
        Status.Text = "图片已准备，按原尺寸居中预览"; UpdatePreview();
    }
    private void UpdatePreview()
    {
        if (settingImage) return;
        prepared = source == null ? null : Images.Prepare(source, Mode.SelectedIndex == 1 ? Editor.Crop : source.FullImage, Colors.SelectedIndex == 0 && !source.GrayscaleSource);
        Preview.Image = prepared;
        var image = prepared;
        dimensions.Text = image == null ? "1024 × 768 画布" : $"图片 {image.Width} × {image.Height} · {(image.Channels == 4 ? "RGB 8-bit/通道" : "8-bit 灰度")} · 1024 × 768 画布";
        Editor.InvalidateVisual(); UpdateButtons();
    }
    private void UpdateButtons()
    {
        var idle = !busy && !importing;
        Connect.IsEnabled = Import.IsEnabled = Colors.IsEnabled = Mode.IsEnabled = idle;
        Install.IsEnabled = idle && ready && canInstall && Output != null; Restore.IsEnabled = idle && ready && canRestore;
        reset.IsEnabled = idle && source != null && Mode.SelectedIndex == 1; export.IsEnabled = idle && source != null; Editor.Active = idle && Mode.SelectedIndex == 1;
    }
    public void Log(string message)
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => Log(message)); return; }
        entries.Enqueue(DateTime.Now.ToString("HH:mm:ss") + "  " + message); while (entries.Count > 200) entries.Dequeue(); logs.Text = string.Join("\n", entries); logScroll.ScrollToEnd();
    }
    private async Task ImportFile()
    {
        if (busy || importing) return;
        importing = true; UpdateButtons();
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "导入图片", AllowMultiple = false, FileTypeFilter = [new("PNG / JPEG") { Patterns = ["*.png", "*.jpg", "*.jpeg"] }] });
            if (files.Count == 0) return; var path = files[0].TryGetLocalPath(); Check.That(path != null, "请选择本地图片。"); var selected = path!;
            Status.Text = "正在解码、校正方向并适配图片，请稍候…";
            var photo = await Task.Run(() => Images.Load(selected)); SetPhoto(photo, files[0].Name);
        }
        catch (Exception e) { Status.Text = e is OutOfMemoryException ? "图片处理内存不足，请先关闭其他程序或缩小图片。" : e.Message; Log("图片导入未完成：" + Status.Text); }
        finally { importing = false; UpdateButtons(); }
    }
    private async Task ExportFile()
    {
        if (Output == null) return;
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "导出成品 PNG", SuggestedFileName = "startup.png", DefaultExtension = "png", FileTypeChoices = [new("PNG") { Patterns = ["*.png"] }] });
            if (file == null) return; var path = file.TryGetLocalPath(); Check.That(path != null, "请选择本地保存位置。"); Images.Export(Output, path!); Status.Text = "PNG 图片已导出";
        }
        catch (Exception e) { Status.Text = e.Message; Log("图片导出未完成：" + e.Message); }
    }
    public async Task Perform(int operation)
    {
        if (busy || importing) return; var chosen = Output;
        if (operation == 1 && chosen == null) { Status.Text = "请先导入图片。"; return; }
        busy = true; UpdateButtons(); progress.Value = 0;
        Status.Text = operation == 0 ? "正在连接并检查相机" : operation == 1 ? "正在准备自定义画面" : "正在准备恢复原厂画面"; Log("开始：" + Status.Text);
        var phase = "";
        var allowProgress = 1;
        try
        {
            var result = await Task.Run(() =>
            {
                var dir = DiagnosticsRoot; Directory.CreateDirectory(dir);
                using var diagnostic = new StreamWriter(Path.Combine(dir, "session-" + Guid.NewGuid().ToString("N") + ".log")); diagnostic.AutoFlush = true;
                using var link = LinkFactory(Log, text => diagnostic.WriteLine(DateTime.Now.ToString("O") + " " + text));
                var finishAttempted = false;
                try
                {
                    var c = new CameraController(link, ResourcesPath, BackupRoot);
                    c.Progress = (text, value) => { if (text != phase) { Log(text); phase = text; } Dispatcher.UIThread.Post(() => { if (Volatile.Read(ref allowProgress) != 0) { Status.Text = text; progress.Value = value; } }); };
                    var guard = c.HasMenuRetryGuard();
                    if (guard && operation != 0) Log("检测到扩展菜单保护：操作完成后，请完整关机再开机，让菜单重新加载。");
                    if (operation == 1) c.Install(chosen!); if (operation == 2) c.Restore();
                    var (s, r) = c.Inspect(); var currentText = s.Edition + "原厂画面"; var installAllowed = r == null;
                    if (s.LibraryHash != Adapter.StockHash && r != null)
                    {
                        try { Check.That(c.Read(CameraPaths.Mode).SequenceEqual("3"u8.ToArray()), "独立模式未启用。"); var installed = StartupImage.Decode(c.Read(CameraPaths.Image)); currentText = r.AdapterHash != c.Adapter.AdapterSHA256 ? "旧版灰度画面 · 请先恢复原厂" : installed.Channels == 4 ? "自定义 RGB 彩色画面" : "自定义灰度画面"; if (r.AdapterHash == c.Adapter.AdapterSHA256) { c.ServicesAndMapping(true); installAllowed = true; } }
                        catch (StudioException) { currentText = "自定义设置异常 · 原厂回退"; }
                    }
                    else if (r != null) currentText = "原厂画面 · 待清理安装记录";
                    string? cleanupFailure = null; finishAttempted = true;
                    try { link.Finish(); }
                    catch (StudioException finish) { cleanupFailure = finish.Message; diagnostic.WriteLine("操作已完成；收尾失败：" + finish.Message); }
                    return (s, r, currentText, guard, cleanupFailure, installAllowed);
                }
                catch (Exception e)
                {
                    var message = e.Message;
                    if (!finishAttempted) { finishAttempted = true; try { link.Finish(); } catch (Exception finish) { message += "\n收尾失败：" + finish.Message; } }
                    diagnostic.WriteLine(message); throw new StudioException(message);
                }
            }).ConfigureAwait(false);
            Interlocked.Exchange(ref allowProgress, 0);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                device.Text = (result.s.CameraModel == Models.Cfv ? "907X / CFV 100C" : "X2D 100C") + " · " + result.s.Edition; current.Text = result.currentText;
                ready = result.cleanupFailure == null; canRestore = result.r != null; canInstall = result.installAllowed; Connect.Content = "重新检测"; progress.Value = 1;
                Status.Text = CompletionStatus(operation, result.s.Edition, result.cleanupFailure);
                note.Text = result.guard ? "扩展菜单每次开机只加载一次；换图或恢复后，需要完整关机再开机。" : "首次原厂备份已保留；重复换图不会覆盖首次备份。"; Log((result.cleanupFailure == null ? "完成：" : "操作完成，收尾待确认：") + Status.Text);
            });
        }
        catch (Exception e)
        {
            Interlocked.Exchange(ref allowProgress, 0);
            await Dispatcher.UIThread.InvokeAsync(() => { ready = false; var message = FriendlyFailure(e.Message); Status.Text = message; Log("未完成：" + message); });
        }
        finally { await Dispatcher.UIThread.InvokeAsync(() => { busy = false; UpdateButtons(); }); }
    }
    public static string FriendlyFailure(string message)
    {
        const string cleanup = "\n收尾失败：";
        var split = message.IndexOf(cleanup, StringComparison.Ordinal);
        if (split > 0) return FriendlyFailure(message[..split]) + "\n另外，" + FriendlyFailure(message[(split + cleanup.Length)..]);
        // Older logs have no separator. Keep their first failure visible too.
        var legacySplit = message.IndexOf("\nUSB 原状态恢复未确认", StringComparison.Ordinal);
        if (legacySplit > 0) return FriendlyFailure(message[..legacySplit]) + "\n另外，" + FriendlyFailure(message[(legacySplit + 1)..]);
        if (message.Contains("自动恢复未确认")) return "安装未完成，自动恢复尚未确认。请保留电脑备份，重新连接相机后使用恢复按钮。";
        if (message.Contains("USB 原状态恢复未确认")) return "USB 连接状态恢复尚未确认，请完整关机再开机后重新检测。";
        if (message.Contains("LIBUSB_ERROR_ACCESS") || message.Contains("LIBUSB_ERROR_NOT_SUPPORTED") || message.Contains("LIBUSB_ERROR_NOT_FOUND")) return "工厂 USB 接口驱动未就绪，请查看包内 Windows 驱动说明。";
        if (message.Contains("LIBUSB_ERROR_BUSY")) return "USB 接口正被其他程序占用，请关闭其他相机工具后重试。";
        if (message.Contains("LIBUSB_ERROR_NO_DEVICE")) return "相机连接已断开，请唤醒相机并检查数据线后重新检测。";
        if (message.Contains("LIBUSB_ERROR_TIMEOUT")) return "相机未完成响应，请保持开机后重新检测。";
        if (message.Contains("LIBUSB_ERROR_IO")) return "USB 传输中断，请检查连接后重新检测。";
        if (message.Contains("libusb-1.0.dll") || message.Contains("startup-libusb")) return "内置 USB 依赖未能加载，请完整解压整个安装包后重试。";
        return message;
    }
    public static string CompletionStatus(int operation, string edition, string? cleanupFailure)
    {
        var completed = operation == 0 ? "相机检查完成" : operation == 1 ? "安装完成" : "已恢复" + edition + "原厂画面";
        if (cleanupFailure != null) return completed + "；" + FriendlyFailure(cleanupFailure);
        return operation == 0 ? completed : completed + (operation == 1 ? "，请完整关机再开机确认画面" : "，请完整关机再开机");
    }
}
