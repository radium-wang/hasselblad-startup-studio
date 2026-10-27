import Foundation

struct Gen2Status:Decodable {
    let deviceTitle:String,edition:String,current:String
    let canInstall:Bool,canRestore:Bool
}
final class Gen2Host {
    let resources:URL,backups:URL,log:(String)->Void,trace:(String)->Void
    init(resources:URL,backups:URL,log:@escaping(String)->Void,trace:@escaping(String)->Void) {self.resources=resources;self.backups=backups;self.log=log;self.trace=trace}
    var root:URL {resources.appendingPathComponent("gen2")}
    var python:URL {root.appendingPathComponent("runtime/bin/python3.13")}
    var available:Bool {FileManager.default.isExecutableFile(atPath:python.path) && FileManager.default.fileExists(atPath:root.appendingPathComponent("main.py").path)}
    func run(_ operation:String,image:GrayImage?=nil) throws -> Data {
        try require(available,"内置二代组件缺失，请完整解压 App。")
        let temporary=FileManager.default.temporaryDirectory.appendingPathComponent("startup-gen2-"+UUID().uuidString)
        try FileManager.default.createDirectory(at:temporary,withIntermediateDirectories:true,attributes:[.posixPermissions:0o700])
        defer {try? FileManager.default.removeItem(at:temporary)}
        let output=temporary.appendingPathComponent("output"),errors=temporary.appendingPathComponent("errors")
        for file in [output,errors] {FileManager.default.createFile(atPath:file.path,contents:nil,attributes:[.posixPermissions:0o600])}
        let out=try FileHandle(forWritingTo:output),err=try FileHandle(forWritingTo:errors)
        defer {try? out.close();try? err.close()}
        let task=Process();task.executableURL=python;task.currentDirectoryURL=temporary
        let library=resources.deletingLastPathComponent().appendingPathComponent("Frameworks/libusb-1.0.0.dylib")
        var args=["-X","utf8","-P","-s","-B",root.appendingPathComponent("main.py").path,"--device","--operation",operation,"--backups",backups.path,"--libusb",library.path,"--adb",resources.appendingPathComponent("bin/adb").path]
        if let image {
            let input=temporary.appendingPathComponent("image.bin");try image.envelope().write(to:input,options:.atomic)
            args += ["--input",input.path]
        }
        task.arguments=args
        var environment=ProcessInfo.processInfo.environment
        for key in Array(environment.keys) where key.hasPrefix("PYTHON") {environment.removeValue(forKey:key)}
        environment["PYTHONHOME"]=root.appendingPathComponent("runtime").path;environment["PYTHONNOUSERSITE"]="1"
        task.environment=environment;task.standardInput=FileHandle.nullDevice;task.standardOutput=out;task.standardError=err
        try task.run();defer {if task.isRunning {task.terminate();task.waitUntilExit()}}
        let deadline=Date().addingTimeInterval(240);var seen=0;var result:Data?;var failure:String?
        func consume() throws {
            let data=try Data(contentsOf:output);try require(data.count<=2097152,"二代组件日志超出边界。")
            let lines=data.split(separator:10,omittingEmptySubsequences:false)
            let complete=max(0,lines.count-1)
            for index in seen..<complete {
                guard let value=try JSONSerialization.jsonObject(with:Data(lines[index])) as? [String:Any],let type=value["type"] as? String else {throw StudioError("二代组件响应格式不符。")}
                if type=="progress",let message=value["message"] as? String {log(message)}
                if type=="error" {failure=value["message"] as? String ?? "二代操作未完成。"}
                if type=="result",let object=value["data"] {result=try JSONSerialization.data(withJSONObject:object)}
            }
            seen=complete
        }
        while task.isRunning && Date()<deadline {try consume();Thread.sleep(forTimeInterval:0.1)}
        if task.isRunning {task.terminate();throw StudioError("二代操作超时，请完整关机再开机后重新检测；保留电脑恢复备份。")}
        try consume()
        let diagnostic=try Data(contentsOf:errors);trace(String(decoding:diagnostic.prefix(262144),as:UTF8.self))
        if let failure {throw StudioError(failure)}
        try require(task.terminationStatus==0 && result != nil,"内置二代组件运行失败，请保留日志和恢复备份。")
        return result!
    }
    func probe() throws -> Bool {
        if !available {return false}
        let value=try JSONSerialization.jsonObject(with:run("probe")) as? [String:Any]
        guard let found=value?["gen2"] as? Bool else {throw StudioError("二代识别结果不完整。")};return found
    }
    func operate(_ operation:Int,image:GrayImage?) throws -> Gen2Status {
        try JSONDecoder().decode(Gen2Status.self,from:run(operation==0 ? "inspect":operation==1 ? "install":"restore",image:operation==1 ? image:nil))
    }
}
