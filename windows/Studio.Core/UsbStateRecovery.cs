using System.Diagnostics;
namespace StartupStudio;

public static class UsbStateRecovery
{
    public const string Normal = "rndis,mass_storage,bulk,acm";
    public const string Transfer = Normal + ",adb";
    public static string RestoreCommand(string original)
    {
        Check.That(original is Normal or Transfer, "USB 原状态无效，禁止恢复未知配置。");
        return "/system/bin/toybox nohup /system/bin/sh -c 'sleep 1;setprop sys.usb.config none;sleep 1;setprop sys.usb.config " + original + "' >/dev/null 2>&1 &";
    }
    public static bool Transient(string message) => new[] { "LIBUSB_ERROR_NO_DEVICE", "LIBUSB_ERROR_TIMEOUT", "LIBUSB_ERROR_IO", "LIBUSB_ERROR_BUSY", "LIBUSB_ERROR_ACCESS", "LIBUSB_ERROR_NOT_FOUND", "LIBUSB_ERROR_NOT_SUPPORTED", "未检测到支持的相机", "工厂 WinUSB 接口未就绪", "Windows 相机设备关系读取失败", "工厂接口与物理相机不匹配", "USB 会话已断开" }.Any(message.Contains);
    public static string Category(string message) =>
        message.Contains("LIBUSB_ERROR_TIMEOUT") ? "timeout" :
        message.Contains("LIBUSB_ERROR_BUSY") ? "busy" :
        message.Contains("LIBUSB_ERROR_ACCESS") || message.Contains("LIBUSB_ERROR_NOT_FOUND") || message.Contains("LIBUSB_ERROR_NOT_SUPPORTED") ? "driver" :
        message.Contains("LIBUSB_ERROR_IO") ? "io" : "disconnected";

    public static void Restore(string original, Func<IUsbCameraLink> open, Action<ICameraLink> verify, Action<string> trace, Func<long>? clock = null, Action<int>? wait = null, int timeout = 25000)
    {
        var command = RestoreCommand(original); var watch = Stopwatch.StartNew(); clock ??= () => watch.ElapsedMilliseconds; wait ??= Thread.Sleep;
        var start = clock(); long nextSend = start; var attempts = 0; string? lastCategory = null;
        while (clock() - start < timeout)
        {
            try
            {
                using var link = open(); verify(link);
                var config = link.Command("getprop sys.usb.config", 2000); var state = link.Command("getprop sys.usb.state", 2000);
                if (config == original && state == original) { trace("USB restore confirmed"); return; }
                if (config == Transfer && clock() >= nextSend && attempts < 3)
                {
                    attempts++; nextSend = clock() + 5000;
                    trace("USB restore request attempt=" + attempts);
                    try { link.Command(command, 5000); }
                    catch (StudioException e) when (e.Message.Contains("LIBUSB_ERROR_NO_DEVICE") || e.Message.Contains("LIBUSB_ERROR_TIMEOUT")) { trace("USB restore request disconnected; confirmation pending"); }
                }
            }
            catch (StudioException e) when (Transient(e.Message))
            {
                var category = Category(e.Message);
                if (category != lastCategory) { trace("USB restore reconnect category=" + category + " detail=" + e.Message); lastCategory = category; }
            }
            wait(250);
        }
        trace("USB restore unconfirmed; attempts=" + attempts + " reconnect=" + (lastCategory ?? "state-pending"));
        throw new StudioException("USB 原状态恢复未确认；请完整关机再开机后重新检测。");
    }
}
