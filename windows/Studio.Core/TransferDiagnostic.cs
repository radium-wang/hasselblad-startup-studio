namespace StartupStudio;

public static class TransferDiagnostic
{
    // Only the runtime USB mode may change. No installation, journal, backup or
    // remote temporary file is created. The downloaded bytes remain in memory.
    public static void Run(CameraController controller, IManagedCameraLink link, Action<string> log)
    {
        Exception? failure = null;
        try
        {
            log("只读检测：核对机型、原厂身份及绘图库");
            var snapshot = controller.Snapshot();
            Check.That(snapshot.LibraryHash == Adapter.StockHash, "只读检测仅用于原厂绘图库；当前状态不符，未开始传输。");
            log("只读检测：下载原厂绘图库并校验");
            var data = controller.Read(CameraPaths.Library);
            Check.That(data.Length == controller.Adapter.OriginalSize && Check.Hash(data) == snapshot.LibraryHash,
                "只读检测：下载内容大小或 SHA-256 不符。");
            log("只读检测：下载大小和 SHA-256 均通过");
        }
        catch (Exception e) { failure = e; }
        try { link.Finish(); }
        catch (Exception e)
        {
            throw new StudioException((failure == null ? "" : failure.Message + "\n") + "USB 收尾失败：" + e.Message);
        }
        if (failure != null) throw new StudioException(failure.Message);
        log("只读检测完成；USB 收尾成功，未安装图片");
    }
}
