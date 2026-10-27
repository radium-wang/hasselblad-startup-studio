using System.Net;
using System.Net.Sockets;
using System.Text;

// Offline process fixture. This executable has no USB or ADB dependencies.
Console.OutputEncoding = new UTF8Encoding(false);
Console.SetError(new StreamWriter(Console.OpenStandardError(),new UTF8Encoding(false)) {AutoFlush=true});
// Gen2 process-protocol fixture; it never imports or opens a device backend.
if(args.Contains("--operation"))
{
    var control=File.ReadAllText(args.Single(x=>x.EndsWith("main.py",StringComparison.Ordinal)));
    Console.Error.WriteLine("本地进程替身诊断");
    if(control=="failure") {Console.WriteLine("{\"type\":\"error\",\"message\":\"测试错误：保留恢复备份\"}");return 2;}
    if(control=="malformed") {Console.WriteLine("not-json");return 0;}
    if(control=="empty")return 1;
    if(!args.Take(5).SequenceEqual(new[]{"-X","utf8","-P","-s","-B"}))return 8;
    var operation=args[Array.IndexOf(args,"--operation")+1];
    Console.WriteLine("{\"type\":\"progress\",\"message\":\"正在核对二代画面\"}");
    if(operation=="probe")Console.WriteLine("{\"type\":\"result\",\"data\":{\"gen2\":true}}");
    else
    {
        if(operation=="install")
        {
            var data=File.ReadAllBytes(args[Array.IndexOf(args,"--input")+1]);
            if(data.Length<32 || Encoding.ASCII.GetString(data,0,8)!="X2DLOGO3")return 9;
        }
        Console.WriteLine("{\"type\":\"result\",\"data\":{\"deviceTitle\":\"X2D II 100C\",\"edition\":\"标准版\",\"current\":\"原厂开机画面\",\"canInstall\":true,\"canRestore\":false}}");
    }
    return 0;
}
if (args.Length == 4 && args[0] == "-L" && args[2] == "nodaemon" && args[3] == "server")
{
    if (Environment.GetEnvironmentVariable("STARTUP_STUDIO_FAKE_FAIL") == "1") return 1;
    var port = int.Parse(args[1].Split(':')[^1]);
    using var listener = new TcpListener(IPAddress.Loopback, port); listener.Start();
    while (true) { using var client = listener.AcceptTcpClient(); using var stream = client.GetStream(); var request = new byte[16]; stream.ReadExactly(request); if (Encoding.ASCII.GetString(request) == "000chost:version") stream.Write(Encoding.ASCII.GetBytes("OKAY00040029")); }
}
if (args.Length >= 4 && args[0] == "-H") { Console.Write(args[1] + ":" + args[3]); return 0; }
if (args.Length > 0 && args[0] == "-d") args = args[1..];
switch (args.FirstOrDefault())
{
    case "large": using (var output = Console.OpenStandardOutput()) output.Write(new byte[262144]); break;
    case "denied": Console.Error.Write("Permission denied"); return 1;
    case "many": Console.Error.Write("more than one device/emulator"); return 1;
    case "slow": Thread.Sleep(3000); break;
    case "argument": Console.Write(args[1]); break;
}
return 0;
