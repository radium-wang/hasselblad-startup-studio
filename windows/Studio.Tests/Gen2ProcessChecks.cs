using StartupStudio;
using System.Text.Json;

static class Gen2ProcessChecks
{
    public static void Run(Action<string,Action> test,string temporary)
    {
        test("gen2-usb-diagnostic-is-private-metadata",()=>
        {
            var physical=@"USB\VID_2756&PID_000B\PRIVATE-ID";
            var report=WindowsUsbDiagnostics.Report(new[]{new WindowsUsbNode(physical,physical,"usbccgp",0),new WindowsUsbNode(@"USB\VID_2756&PID_000B&MI_04\PRIVATE-ID",physical,"WinUSB",0),new WindowsUsbNode(@"USB\VID_2756&PID_000B&MI_06\PRIVATE-ID",physical,"WinUSB",0)},"offline");
            using var document=JsonDocument.Parse(report);var value=document.RootElement;
            Check.That(value.GetProperty("physicalCameras").GetInt32()==1 && value.GetProperty("nodes").GetArrayLength()==3 && report.Contains("X2D II 100C") && report.Contains("04") && report.Contains("06") && !report.Contains("PRIVATE-ID"),"Gen2 diagnostic model/topology/privacy failed");
        });
        test("gen2-diagnostic-does-not-expand-gen1-transfer-gate",()=>Check.That(WindowsAdbInterface.PhysicalProductId(@"USB\VID_2756&PID_000B\fixture")==0,"Gen1 gate expanded"));
        var root=Path.Combine(temporary,"二代 进程测试");var helper=Path.Combine(root,"gen2");var runtime=Path.Combine(helper,"runtime");Directory.CreateDirectory(runtime);
        var source=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","Studio.FakeAdb","release"));
        foreach(var file in Directory.GetFiles(source))File.Copy(file,Path.Combine(runtime,Path.GetFileName(file)));
        var executable=Path.Combine(runtime,"python.exe");File.Copy(Path.Combine(source,"Studio.FakeAdb"+(OperatingSystem.IsWindows()?".exe":"")),executable);
        if(!OperatingSystem.IsWindows())File.SetUnixFileMode(executable,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        var main=Path.Combine(helper,"main.py");File.WriteAllText(main,"ok");var messages=new List<string>();var traces=new List<string>();
        var bridge=new Gen2Helper(root,Path.Combine(temporary,"Backups"),messages.Add,traces.Add);
        var oldRoot=Environment.GetEnvironmentVariable("DOTNET_ROOT");
        try
        {
            if(Path.GetFileNameWithoutExtension(Environment.ProcessPath)=="dotnet")Environment.SetEnvironmentVariable("DOTNET_ROOT",Path.GetDirectoryName(Environment.ProcessPath));
            test("gen2-process-probe",()=>Check.That(bridge.Probe(),"probe failed"));
            test("gen2-process-status-and-utf8",()=> {var s=bridge.Operate(0,null);Check.That(s.DeviceTitle=="X2D II 100C" && s.Edition=="标准版" && s.CanInstall && messages.Contains("正在核对二代画面") && traces.Any(t=>t.Contains("本地进程替身诊断")),"UTF8 status lost");});
            test("gen2-process-image-envelope-forwarded",()=>bridge.Operate(1,new StartupImage(1,1,new byte[]{1,2,3,255},4)));
            test("gen2-process-restore-dispatch",()=>bridge.Operate(2,null));
            foreach(var mode in new[]{"failure","malformed","empty"})
            {
                var chosen=mode;
                test("gen2-process-rejects-"+mode,()=>
                {
                    File.WriteAllText(main,chosen);var rejected=false;
                    try {bridge.Operate(0,null);} catch(Exception e) when(e is StudioException or JsonException) {rejected=true;if(chosen=="failure")Check.That(e.Message.Contains("测试错误：保留恢复备份"),"error text lost");}
                    Check.That(rejected,"invalid helper output accepted");
                });
            }
        }
        finally {Environment.SetEnvironmentVariable("DOTNET_ROOT",oldRoot);}
    }
}
