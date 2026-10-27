using System.Diagnostics;
using System.Text;
using System.Text.Json;
namespace StartupStudio;

public sealed record Gen2Status(string DeviceTitle,string Edition,string Current,bool CanInstall,bool CanRestore);
public sealed class Gen2Helper(string resources,string backups,Action<string> progress,Action<string> trace)
{
    private string Root => Path.Combine(resources,"gen2");
    private string Python => Path.Combine(Root,"runtime","python.exe");
    public bool Available => File.Exists(Python) && File.Exists(Path.Combine(Root,"main.py"));
    private JsonElement Run(string operation,StartupImage? image=null)
    {
        Check.That(Available,"内置二代组件缺失，请完整解压安装包。");
        var temporary=Path.Combine(Path.GetTempPath(),"startup-gen2-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temporary);
        try
        {
            var start=new ProcessStartInfo(Python) {UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8,WorkingDirectory=temporary};
            foreach(var key in start.Environment.Keys.Where(k=>k.StartsWith("PYTHON",StringComparison.OrdinalIgnoreCase)).ToArray())start.Environment.Remove(key);
            start.Environment["PYTHONHOME"]=Path.Combine(Root,"runtime");start.Environment["PYTHONNOUSERSITE"]="1";
            foreach(var arg in new[]{"-X","utf8","-P","-s","-B",Path.Combine(Root,"main.py"),"--device","--operation",operation,"--backups",backups,"--libusb",Path.Combine(AppContext.BaseDirectory,"libusb-1.0.dll"),"--adb",Path.Combine(resources,"bin","adb.exe")})start.ArgumentList.Add(arg);
            if(image!=null) {var input=Path.Combine(temporary,"image.bin");File.WriteAllBytes(input,image.Envelope());start.ArgumentList.Add("--input");start.ArgumentList.Add(input);}
            using var task=new Process {StartInfo=start};Check.That(task.Start(),"内置二代组件未能启动。");task.StandardInput.Close();
            JsonElement result=default;string? failure=null;
            var output=Task.Run(async () =>
            {
                var count=0;string? line;
                while((line=await task.StandardOutput.ReadLineAsync())!=null)
                {
                    count+=line.Length;Check.That(count<=2097152,"二代日志超出边界。");
                    using var document=JsonDocument.Parse(line);var value=document.RootElement;var kind=value.GetProperty("type").GetString();
                    if(kind=="progress")progress(value.GetProperty("message").GetString()!);
                    if(kind=="error")failure=value.GetProperty("message").GetString();
                    if(kind=="result")result=value.GetProperty("data").Clone();
                }
            });
            var errors=Task.Run(async () => {var data=new StringBuilder();var block=new char[4096];int n;while((n=await task.StandardError.ReadAsync(block))>0) {var keep=Math.Min(n,262144-data.Length);if(keep>0)data.Append(block,0,keep);}return data.ToString();});
            try
            {
                if(!task.WaitForExit(240000))throw new StudioException("二代操作超时，请完整关机再开机后重新检测；保留电脑恢复备份。");
                output.GetAwaiter().GetResult();trace(errors.GetAwaiter().GetResult());
                Check.That(failure==null,failure??"二代操作未完成。");
                Check.That(task.ExitCode==0 && result.ValueKind!=JsonValueKind.Undefined,"内置二代组件运行失败，请保留日志和恢复备份。");return result;
            }
            finally {if(!task.HasExited){task.Kill();task.WaitForExit(5000);}}
        }
        finally {Directory.Delete(temporary,true);}
    }
    public bool Probe() => Available && Run("probe").GetProperty("gen2").GetBoolean();
    public Gen2Status Operate(int operation,StartupImage? image) => Check.Decode<Gen2Status>(Encoding.UTF8.GetBytes(Run(operation==0?"inspect":operation==1?"install":"restore",operation==1?image:null).GetRawText()));
}
