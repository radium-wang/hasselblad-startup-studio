import Foundation
import CryptoKit

struct StudioError: LocalizedError {
    let message: String
    init(_ message: String) { self.message = message }
    var errorDescription: String? { message }
}
func require(_ condition: @autoclosure () throws -> Bool, _ message: String) throws {
    if try !condition() { throw StudioError(message) }
}
func digest(_ data: Data) -> String { SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined() }
func validHash(_ s: String) -> Bool { s.range(of: "^[0-9a-f]{64}$", options: .regularExpression) != nil }
func shellQuote(_ s: String) -> String { "'" + s.replacingOccurrences(of: "'", with: "'\\''") + "'" }
func fnv(_ data: Data) -> UInt32 { data.reduce(2166136261) { ($0 ^ UInt32($1)) &* 16777619 } }
extension Data {
    mutating func u32(_ value: UInt32) { var v = value.littleEndian; Swift.withUnsafeBytes(of: &v) { append(contentsOf: $0) } }
}
struct GrayImage {
    let width: Int, height: Int, pixels: Data, channels: Int
    init(width: Int, height: Int, pixels: Data, channels: Int = 1) {
        self.width=width;self.height=height;self.pixels=pixels;self.channels=channels
    }
    static func decode(_ data: Data) throws -> GrayImage {
        try require(data.count >= 32 && data.prefix(8) == Data("X2DLOGO3".utf8), "图片资源格式错误。")
        func word(_ at:Int) -> UInt32 {data[at..<at+4].enumerated().reduce(0) {$0 | UInt32($1.element)<<UInt32(8*$1.offset)}}
        let w=Int(word(12)),h=Int(word(16)),channels=word(8)==2 ? 4 : 1
        try require([1,2].contains(word(8)) && word(28)==0 && (1...1024).contains(w) && (1...768).contains(h) && word(20)==UInt32(w*h*channels) && data.count==32+w*h*channels,"图片资源尺寸或长度不符。")
        let pixels=Data(data.dropFirst(32));try require(fnv(pixels)==word(24),"图片资源校验失败。")
        return GrayImage(width:w,height:h,pixels:pixels,channels:channels)
    }
    func envelope() throws -> Data {
        try require((1...1024).contains(width) && (1...768).contains(height) && [1,4].contains(channels) && pixels.count == width * height * channels, "图片尺寸或像素数据不符。")
        var d = Data("X2DLOGO3".utf8)
        for v in [UInt32(channels==4 ? 2 : 1), UInt32(width), UInt32(height), UInt32(pixels.count), fnv(pixels), 0] { d.u32(v) }
        d.append(pixels); return d
    }
    func grayscale() -> GrayImage {
        if channels==1 {return self}
        var result=Data(count:width*height)
        for i in 0..<width*height {
            let at=i*4
            result[i]=UInt8((299*Int(pixels[at+2])+587*Int(pixels[at+1])+114*Int(pixels[at])+500)/1000)
        }
        return GrayImage(width:width,height:height,pixels:result)
    }
}
struct PatchPart: Codable { let offset: Int, size: Int }
struct Adapter: Codable {
    let mode: Int, originalSHA256: String, adapterSHA256: String, originalSize: Int, patches: [PatchPart]
    static let stockHash = "a0ac02a51d87d08fa61fd0d1e79af15db248af4b5b32603b9483d09f9b7f6217"
    func validate(_ patch: Data) throws {
        try require(mode == 3 && originalSHA256 == Self.stockHash && validHash(adapterSHA256), "适配层清单不匹配。")
        try require(patches.map(\.size).reduce(0,+) == patch.count && originalSize == 2051168, "适配层数据不完整。")
        try require(patches.count == 4 && patches.map(\.offset) == [152,513444,513748,1939088] && patches.prefix(3).map(\.size) == [16,4,4] && patches.last!.size <= 10608 && patches.allSatisfy { $0.size > 0 && $0.offset + $0.size <= originalSize }, "适配层写入范围错误。")
    }
    func apply(to stock: Data, patch: Data) throws -> Data {
        try validate(patch); try require(digest(stock) == originalSHA256, "原厂库不匹配。")
        var result = stock, at = 0
        for part in patches { result.replaceSubrange(part.offset..<part.offset+part.size, with: patch[at..<at+part.size]); at += part.size }
        try require(digest(result) == adapterSHA256, "适配层完整校验失败。")
        return result
    }
}
struct AdapterCatalog {
    static let legacyHash="a2edfe28ad8dbf91ed19fb87526cc6eceb21ceb00a40347ee58e9afaec6929e0"
    let adapters:[String:Adapter]
    init(resources:URL,current:Adapter) throws {
        var found=[current.adapterSHA256:current]
        let folder=resources.appendingPathComponent("legacy").appendingPathComponent(Self.legacyHash)
        if FileManager.default.fileExists(atPath:folder.path) {
            let old=try JSONDecoder().decode(Adapter.self,from:Data(contentsOf:folder.appendingPathComponent("adapter.json")))
            try old.validate(Data(contentsOf:folder.appendingPathComponent("patch.bin")))
            try require(old.adapterSHA256==Self.legacyHash,"历史恢复适配层清单不符。")
            found[old.adapterSHA256]=old
        }
        adapters=found
    }
    func adapter(for record:RecoveryRecord) throws -> Adapter {
        guard let value=adapters[record.adapterHash] else {throw StudioError("恢复包对应的历史适配层缺失，请使用原版本软件恢复；首次备份已保留。")}
        return value
    }
    func recognizes(_ hash:String) -> Bool {hash==Adapter.stockHash || adapters[hash] != nil}
}
enum CameraPaths {
    static let library = "/system/lib64/weston/eagle-backend.so"
    static let backup = library + ".logo3-original"
    static let stage = library + ".logo3-next"
    static let mode = "/system/lib64/weston/x2d-startup-mode3"
    static let image = "/system/lib64/weston/x2d-startup-gray3.bin"
    static let imageStage = image + ".next"
    static let journal = "/system/lib64/weston/x2d-startup-owner3.json"
    static let journalStage = journal + ".next"
    static let owned = [backup,stage,mode,image,imageStage,journal,journalStage]
}
func verifyDisplayKey(_ response: String, variant: Int) throws {
    let lines=response.components(separatedBy:"\n"),status=lines.last ?? ""
    try require(lines.first?.hasPrefix("KEY=") == true,"原厂显示键响应无效。")
    let encoded=String(lines.dropLast().joined().dropFirst(4));guard let raw=Data(base64Encoded:encoded) else {throw StudioError("原厂显示键读取不完整。")}
    let text=String(data:raw,encoding:.utf8)?.trimmingCharacters(in:.whitespacesAndNewlines.union(.controlCharacters))
    if status=="RC=255" && variant==0 && (raw.isEmpty || text=="unrd_get_length failed") {return}
    try require(status=="RC=0", "原厂显示键状态与机型不符，已停止安装。")
    var binary:UInt32=UInt32.max
    if raw.count==4 {binary=raw.enumerated().reduce(UInt32(0)) {$0 | UInt32($1.element)<<UInt32(8*$1.offset)}}
    try require(text==String(variant) || binary==UInt32(variant), "产品版本与开机图选择不一致，需先核对原厂配置。")
}
enum CameraModel:String,Codable {
    case x2d="X2D 100C",cfv100="CFV 100C"
    var usbProductID:UInt16 {self == .x2d ? 0x0009:0x000a}
    var title:String {self == .x2d ? "X2D 100C":"907X / CFV 100C"}
    static func detect(platform:String,pid:String,product:String,transportPID:UInt16?=nil) throws -> CameraModel {
        try require(platform=="eagle2_ec1706_native","当前相机平台不受支持。")
        let expected:CameraModel
        switch (pid.lowercased(),product) {
        case ("0x0009","X2D 100C"):expected = .x2d
        case ("0x000a","CFV 100C"):expected = .cfv100
        default:throw StudioError("USB 产品标识与机型名称不匹配，已停止操作。")
        }
        if let transportPID {try require(transportPID==expected.usbProductID,"USB 设备与相机机型标识不一致，已停止操作。")}
        return expected
    }
    func verifyVariant(_ variant:Int) throws {
        try require(self == .x2d ? [0,2].contains(variant):variant==0,"当前机型的原厂版本尚不受支持，已停止操作。")
    }
}
func identifyCamera(_ link:CameraLink) throws -> CameraModel {
    try CameraModel.detect(platform:link.command("getprop ro.product.device",timeout:12000),pid:link.command("getprop hbl.usb_pid",timeout:12000),product:link.command("getprop hbl.usb_prod_name",timeout:12000),transportPID:link.usbProductID)
}
struct CameraSnapshot: Codable, Equatable {
    let factoryHash: String, variant: Int, displayKey: String, metadata: String
    let libraryHash: String
    // Missing model denotes a pre-0.1.4 X2D record, never a CFV record.
    let model:CameraModel?
    init(factoryHash:String,variant:Int,displayKey:String,metadata:String,libraryHash:String,model:CameraModel?=nil) {
        self.factoryHash=factoryHash;self.variant=variant;self.displayKey=displayKey;self.metadata=metadata;self.libraryHash=libraryHash;self.model=model
    }
    var cameraModel:CameraModel {model ?? .x2d}
    var edition: String { cameraModel == .cfv100 ? "标准版":variant == 2 ? "大地版" : "普通版" }
    static func ==(a:CameraSnapshot,b:CameraSnapshot) -> Bool {
        a.cameraModel==b.cameraModel && a.factoryHash==b.factoryHash && a.variant==b.variant && a.displayKey==b.displayKey && a.metadata==b.metadata && a.libraryHash==b.libraryHash
    }
}
struct RecoveryRecord: Codable, Equatable {
    let schema: Int
    let baseline: CameraSnapshot
    let adapterHash: String
    var roots: [String]
    init(snapshot: CameraSnapshot, adapter: Adapter, root: String) {
        schema = 1; baseline = snapshot; adapterHash = adapter.adapterSHA256; roots = [root]
    }
    func validate(_ current: CameraSnapshot, adapter: Adapter) throws {
        try require(schema == 1 && adapterHash == adapter.adapterSHA256 && baseline.libraryHash == adapter.originalSHA256, "恢复包版本或原厂库不匹配。")
        try require(validHash(baseline.factoryHash) && baseline.factoryHash == current.factoryHash, "这份恢复包不属于当前相机，或原厂配置已发生变化。")
        try require(baseline.cameraModel==current.cameraModel,"恢复包的机型与当前相机不符，禁止跨机型恢复。")
        try baseline.cameraModel.verifyVariant(baseline.variant)
        try verifyDisplayKey(baseline.displayKey,variant:baseline.variant)
        try require(baseline.variant == current.variant && baseline.displayKey == current.displayKey, "原厂版本信息已变化，已停止操作以保留当前状态。")
        try require(current.libraryHash == adapter.originalSHA256 || current.libraryHash == adapter.adapterSHA256, "当前绘图库不是已知原厂库或本 App 适配层。")
        try require([0,2].contains(baseline.variant) && baseline.metadata.range(of:"^[0-7]{3,4} [0-9]+ [0-9]+ 2051168 [0-9]+\\nu:object_r:[A-Za-z0-9_]+:s0$",options:.regularExpression) != nil, "恢复包中的原厂属性无效。")
        try require(!roots.isEmpty && roots.count <= 32 && roots.allSatisfy { $0.range(of: "^(/tmp/x2d-l3-|/blackbox/\\.x2d-l3-)[0-9a-f]{8}$", options: .regularExpression) != nil }, "恢复包路径清单无效。")
    }
}
protocol CameraLink: AnyObject {
    func command(_ text: String, timeout: UInt32) throws -> String
    var usbProductID:UInt16? {get}
}
extension CameraLink {var usbProductID:UInt16? {nil}}
final class USBLogRelay {
    let write: (String)->Void
    init(_ write:@escaping (String)->Void) {self.write=write}
}
private func usbLogCallback(_ message:UnsafePointer<CChar>?,_ context:UnsafeMutableRawPointer?) {
    guard let message,let context else {return}
    Unmanaged<USBLogRelay>.fromOpaque(context).takeUnretainedValue().write(String(cString:message))
}
func commandStage(_ command:String)->String {
    if ["getprop ro.product.device","getprop hbl.usb_pid","getprop hbl.usb_prod_name"].contains(command) {return "机型检测"}
    if command.contains("prodconfig-tool --print") {return "原厂版本读取"}
    if command.contains("unrd ") {return "原厂显示键核对"}
    if command.hasPrefix("sha256sum ") {return command.contains("/factory_data/") ? "原厂配置指纹核对":"文件哈希核对"}
    if command.hasPrefix("stat ") {return "文件属性读取"}
    if command.hasPrefix("ls -Z ") {return "安全标签读取"}
    if command.hasPrefix("toybox base64 ") || command.hasPrefix("toybox dd ") {return "文件分块读取"}
    if command.hasPrefix("printf '%s' ") {return "上传数据块"}
    if command.hasPrefix("nohup sh ") {return "启动相机任务"}
    if command.contains("/result") {return "相机任务状态读取"}
    if command.contains("/proc/") || command.contains("init.svc.") || command=="pidof weston" {return "运行状态核对"}
    if command.hasPrefix("rm ") {return "安装文件清理"}
    if command.contains("mount -o remount") {return "系统挂载状态操作"}
    return "安装与恢复文件检查"
}
func safeReadRetry(_ text:String) -> Bool {
    if text.range(of:"^(getprop [a-zA-Z0-9_.-]+|sha256sum /[A-Za-z0-9/_.-]+|ls -Z /[A-Za-z0-9/_.-]+|stat -c (%s|'%a %u %g %s %Y') /[A-Za-z0-9/_.-]+)$",options:.regularExpression) != nil {return true}
    return text=="prodconfig-tool --print 2>&1 | sed -n '/^SuVariant:/p'"
}
final class USBLink: CameraLink {
    private var session: UnsafeMutableRawPointer?
    private let relay: USBLogRelay
    init(log:@escaping (String)->Void = {_ in}) throws {
        relay=USBLogRelay(log)
        try connect()
    }
    private func connect() throws {
        var e = [CChar](repeating:0,count:512)
        guard let p=sc_usb_open_logged(usbLogCallback,Unmanaged.passUnretained(relay).toOpaque(),&e,e.count) else {throw StudioError(String(cString:e))}
        session=p
    }
    deinit {if let session {sc_usb_close(session)}}
    var usbProductID:UInt16? {session.map {sc_usb_product_id($0)}}
    func command(_ text: String, timeout: UInt32 = 12000) throws -> String {
        try require(text.utf8.count <= 231, "内部 USB 命令过长。")
        let stage=commandStage(text);relay.write("阶段："+stage)
        for attempt in 0..<2 {
            var e=[CChar](repeating:0,count:512)
            guard let session else {throw StudioError("USB 会话已断开。")}
            if let p=sc_usb_shell(session,text,timeout,&e,e.count) {
                defer {sc_free(p)};return String(cString:p).trimmingCharacters(in:.whitespacesAndNewlines)
            }
            let failure=String(cString:e)
            if attempt==0 && safeReadRetry(text) && ["LIBUSB_ERROR_TIMEOUT","LIBUSB_ERROR_IO","LIBUSB_ERROR_NO_DEVICE"].contains(where:{failure.contains($0)}) {
                relay.write("只读检查未取得确认，重新打开 USB 会话后重试一次")
                sc_usb_close(session);self.session=nil;try connect();continue
            }
            throw StudioError("【"+stage+"】"+failure)
        }
        throw StudioError("相机读取未完成。")
    }
}
final class CameraController {
    let link: CameraLink, adapter: Adapter, patch: Data, catalog: AdapterCatalog
    let backupRoot: URL
    var progress: (String,Double) -> Void = {_,_ in}
    init(link: CameraLink, resources: URL, backupRoot: URL) throws {
        self.link=link;self.backupRoot=backupRoot
        adapter=try JSONDecoder().decode(Adapter.self,from:Data(contentsOf:resources.appendingPathComponent("adapter.json")))
        patch=try Data(contentsOf:resources.appendingPathComponent("patch.bin"));try adapter.validate(patch)
        catalog=try AdapterCatalog(resources:resources,current:adapter)
    }
    func call(_ c: String, _ timeout: UInt32 = 12000) throws -> String { try link.command(c,timeout:timeout) }
    func hash(_ p: String) throws -> String {
        let s=try call("sha256sum " + p).components(separatedBy:.whitespaces)[0];try require(validHash(s),"无法校验相机文件。");return s
    }
    func exists(_ p: String) throws -> Bool { try call("[ -e " + p + " ] || [ -L " + p + " ];echo $?") == "0" }
    func snapshot() throws -> CameraSnapshot {
        let model=try identifyCamera(link)
        let factoryHash=try hash("/factory_data/settings.ini")
        let variantText=try call("prodconfig-tool --print 2>&1 | sed -n '/^SuVariant:/p'")
        let match=variantText.range(of:"[0-9]+$",options:.regularExpression)
        guard let r=match,let variant=Int(variantText[r]),[0,2].contains(variant) else { throw StudioError("无法确认原厂普通／大地版本，已停止安装。") }
        try model.verifyVariant(variant)
        let key=try call("set -o pipefail;v=$(unrd -p /dev/block/by-name/env -g su_variant 2>/dev/null | toybox base64);r=$?;printf 'KEY=%s\\nRC=%s' \"$v\" \"$r\"")
        try verifyDisplayKey(key,variant:variant)
        // Keep the full, read-only response and original key presence as the baseline.
        let metadata=try call("stat -c '%a %u %g %s %Y' " + CameraPaths.library) + "\n" + label(CameraPaths.library)
        try require(try call("[ -f " + CameraPaths.library + " ] && [ ! -L " + CameraPaths.library + " ];echo $?") == "0", "绘图库不是原始常规文件。")
        let libraryHash=try hash(CameraPaths.library)
        return CameraSnapshot(factoryHash:factoryHash,variant:variant,displayKey:key,metadata:metadata,libraryHash:libraryHash,model:model)
    }
    func label(_ path: String) throws -> String {
        let text=try call("ls -Z " + path)
        guard let r=text.range(of:"u:object_r:[A-Za-z0-9_]+:s0",options:.regularExpression) else { throw StudioError("无法核对原厂文件标签。") }
        return String(text[r])
    }
    func read(_ path: String, timeout: UInt32 = 30000) throws -> Data {
        let sizeText=try call("stat -c %s "+path)
        guard let size=Int(sizeText),(0...4*1024*1024).contains(size) else {throw StudioError("文件大小无效，读取已停止。")}
        if size>16384,let files=link as? FileCameraLink {return try files.readFile(path,size:size)}
        var result=Data(capacity:size)
        // Bound each command's output instead of waiting for a multi-megabyte HblShell stream.
        let chunk=16384
        for offset in stride(from:0,to:size,by:chunk) {
            let s=try call("toybox dd if="+path+" bs=16384 skip="+String(offset/chunk)+" count=1 2>/dev/null | toybox base64",timeout).filter {!$0.isWhitespace}
            guard let d=Data(base64Encoded:s),d.count==min(chunk,size-offset) else {throw StudioError("相机文件分块读取不完整。")}
            result.append(d)
        }
        return result
    }
    func record() throws -> RecoveryRecord? {
        if try !exists(CameraPaths.journal) { return nil }
        do { return try JSONDecoder().decode(RecoveryRecord.self,from:read(CameraPaths.journal)) }
        catch { throw StudioError("恢复记录损坏；请保留电脑恢复包，暂勿安装。") }
    }
    func folder(_ s: CameraSnapshot) -> URL { backupRoot.appendingPathComponent(s.factoryHash) }
    func saveHost(_ record: RecoveryRecord, stock: Data? = nil) throws {
        let dir=folder(record.baseline);try FileManager.default.createDirectory(at:dir,withIntermediateDirectories:true,attributes:[.posixPermissions:0o700])
        let baselineURL=dir.appendingPathComponent("baseline.json"),stockURL=dir.appendingPathComponent("original-backend.so")
        if FileManager.default.fileExists(atPath:baselineURL.path) {
            let old=try JSONDecoder().decode(RecoveryRecord.self,from:Data(contentsOf:baselineURL))
            _=try catalog.adapter(for:old);_=try catalog.adapter(for:record)
            try require(old.baseline == record.baseline,"已有首次恢复基线不符，禁止覆盖。")
        } else {
            try JSONEncoder().encode(record).write(to:baselineURL,options:.atomic)
            try FileManager.default.setAttributes([.posixPermissions:0o600],ofItemAtPath:baselineURL.path)
        }
        if let data=stock {
            try require(digest(data)==adapter.originalSHA256,"原厂备份校验失败。")
            if FileManager.default.fileExists(atPath:stockURL.path) { try require(digest(Data(contentsOf:stockURL))==adapter.originalSHA256,"首次原库备份损坏。") }
            else { try data.write(to:stockURL,options:.atomic);try FileManager.default.setAttributes([.posixPermissions:0o600],ofItemAtPath:stockURL.path) }
        }
        try JSONEncoder().encode(record).write(to:dir.appendingPathComponent("transaction.json"),options:.atomic)
    }
    func markActive(_ record: RecoveryRecord) throws {
        try Data("pending".utf8).write(to:folder(record.baseline).appendingPathComponent("active"),options:.atomic)
    }
    func fallbackRecord(_ snapshot: CameraSnapshot) throws -> RecoveryRecord? {
        let dir=folder(snapshot)
        if !FileManager.default.fileExists(atPath:dir.appendingPathComponent("active").path) {return nil}
        return try JSONDecoder().decode(RecoveryRecord.self,from:Data(contentsOf:dir.appendingPathComponent("transaction.json")))
    }
    func checkHost(_ record: RecoveryRecord) throws {
        let dir=folder(record.baseline)
        let old=try JSONDecoder().decode(RecoveryRecord.self,from:Data(contentsOf:dir.appendingPathComponent("baseline.json")))
        _=try catalog.adapter(for:old);_=try catalog.adapter(for:record)
        try require(old.baseline==record.baseline,"电脑备份与相机恢复记录不符。")
        try require(digest(Data(contentsOf:dir.appendingPathComponent("original-backend.so")))==adapter.originalSHA256,"电脑原厂备份缺失或损坏。")
    }
    func checkFactory(_ record: RecoveryRecord) throws {
        try require(try identifyCamera(link)==record.baseline.cameraModel,"相机机型发生变化，已停止操作。")
        try require(try hash("/factory_data/settings.ini") == record.baseline.factoryHash,"原厂配置变化，已停止操作。")
        try require(try call("set -o pipefail;v=$(unrd -p /dev/block/by-name/env -g su_variant 2>/dev/null | toybox base64);r=$?;printf 'KEY=%s\\nRC=%s' \"$v\" \"$r\"") == record.baseline.displayKey,"原厂显示键变化，已停止操作。")
    }
    func upload(_ data: Data, to path: String) throws {
        if let files=link as? FileCameraLink {try files.writeFile(data,to:path);return}
        var length=0
        let compressed: Data = try data.withUnsafeBytes { b in
            guard let p=sc_gzip(b.bindMemory(to:UInt8.self).baseAddress,data.count,&length) else { throw StudioError("本地压缩失败。") }
            defer { sc_free(p) };return Data(bytes:p,count:length)
        }
        let target=path + ".b64",encoded=compressed.base64EncodedString()
        try require(try call(": > " + target + ";echo READY") == "READY","无法准备暂存文件。")
        let chunkSize=144 - (target.utf8.count > 50 ? 32 : 0),chars=Array(encoded)
        for at in stride(from:0,to:chars.count,by:chunkSize) {
            let part=String(chars[at..<min(at+chunkSize,chars.count)])
            try require(try call("printf '%s' " + shellQuote(part) + " >> " + target + ";echo OK") == "OK","传输中断。")
            progress("正在传输图片与安装文件",Double(at)/Double(max(chars.count,1)))
        }
        let body="set -e;toybox base64 -d " + target + " | gzip -dc > " + path + ";echo READY"
        try require(try call(body)=="READY","暂存文件解码失败。")
        try require(try hash(path)==digest(data),"暂存文件校验失败。")
    }
    func writeJournal(_ r: RecoveryRecord, root: String) throws {
        let encoded = try JSONEncoder().encode(r)
        try upload(encoded,to:root+"/owner")
        try require(try call("mount -o remount,rw /system;echo $?")=="0","系统分区无法写入。")
        defer { _=try? call("sync;mount -o remount,ro /system") }
        let script="set -e;cp " + root + "/owner " + CameraPaths.journalStage + ";chmod 600 " + CameraPaths.journalStage
        try require(try call(script+";echo OK")=="OK","恢复记录写入失败。")
        try require(try hash(CameraPaths.journalStage)==digest(encoded),"恢复记录校验失败。")
        try require(try call("mv -f " + CameraPaths.journalStage + " " + CameraPaths.journal + ";sync;echo OK")=="OK","恢复记录提交失败。")
    }
    func makeRoot() throws -> String {
        let root="/blackbox/.x2d-l3-" + UUID().uuidString.replacingOccurrences(of:"-",with:"").prefix(8).lowercased()
        try require(try !exists(root),"暂存目录已存在，已停止操作。")
        return root
    }
    func createRoot(_ root: String, record: RecoveryRecord) throws {
        try require(try call("umask 077;mkdir " + root + " && printf '%s' " + record.baseline.factoryHash + " > " + root + "/owner-id;echo $?") == "0", "无法创建安装暂存目录。")
    }
    func cleanRoot(_ root: String, record: RecoveryRecord) throws {
        if try !exists(root) {return}
        try require(try call("[ ! -L " + root + " ];echo $?") == "0", "暂存目录归属异常，未删除。")
        try require(try call("cat " + root + "/owner-id 2>/dev/null") == record.baseline.factoryHash,"暂存目录归属不符，未删除。")
        try require(try call("rm -rf " + root + ";echo $?") == "0","暂存目录清理失败。")
    }
    func runScript(_ text: String, root: String, name: String) throws {
        try upload(Data(text.utf8),to:root+"/"+name)
        // Keep the camera job alive across an accidental USB disconnect.
        try require(try call("nohup sh " + root + "/" + name + " > " + root + "/log 2>&1 </dev/null & echo STARTED",1000)=="STARTED","相机任务未启动。")
        for _ in 0..<90 {
            Thread.sleep(forTimeInterval:0.5)
            let status=try call("if [ -f " + root + "/result ];then cat " + root + "/result;fi",2000)
            if status=="OK" { return }
            if status=="FAILED" { throw StudioError("相机任务校验失败，请使用恢复按钮。") }
        }
        throw StudioError("相机任务未确认完成，请重新连接后恢复。")
    }
    func servicesAndMapping(_ active: Bool) throws {
        try require(try call("getprop init.svc.weston") == "running" && call("getprop init.svc.camera-gui") == "running","显示服务尚未恢复。")
        let mounts=try call("cat /proc/mounts | sed -n '\\| /system |p'")
        try require(mounts.split(separator:" ").count>3 && mounts.split(separator:" ")[3].hasPrefix("ro,"),"系统未恢复只读。")
        let pid=try call("pidof weston");try require(pid.range(of:"^[0-9]+$",options:.regularExpression) != nil,"无法确认显示进程。")
        let maps=try call("cat /proc/"+pid+"/maps | sed -n '\\|eagle-backend.so|p'")
        let lines=maps.split(separator:"\n").filter { $0.contains("r-xp 00000000") && $0.hasSuffix(CameraPaths.library) }
        guard lines.count==1,let hex=lines[0].split(separator:"-").first,let base=UInt64(hex,radix:16) else { throw StudioError("无法核对实际加载的绘图库。") }
        let stock=try Data(contentsOf:folder(try snapshot()).appendingPathComponent("original-backend.so"))
        let want=active ? try adapter.apply(to:stock,patch:patch) : stock
        for offset in [513444,513748] {
            let got=try call("od -An -tx1 -N4 -j " + String(base+UInt64(offset)) + " /proc/" + pid + "/mem")
            let expected=want[offset..<offset+4].map { String(format:"%02x",$0) }.joined(separator:" ")
            try require(got.split(whereSeparator:{$0.isWhitespace}).joined(separator:" ")==expected,"实际绘图库分支核验失败。")
        }
    }
    func inspect() throws -> (CameraSnapshot,RecoveryRecord?) {
        let s=try snapshot();try require(catalog.recognizes(s.libraryHash),"当前固件或绘图库不受支持。")
        for p in CameraPaths.owned { try require(try call("[ ! -L " + p + " ];echo $?") == "0", "检测到异常链接，禁止修改。") }
        let r=try record() ?? fallbackRecord(s)
        if let r { try r.validate(s,adapter:catalog.adapter(for:r));try checkHost(r) }
        else { for p in CameraPaths.owned { try require(try !exists(p),"检测到未归属的旧修改文件，需先完成原试验恢复。") };try require(s.libraryHash==adapter.originalSHA256,"缺少这台相机的恢复记录。") }
        if r == nil && FileManager.default.fileExists(atPath:folder(s).appendingPathComponent("original-backend.so").path) {try servicesAndMapping(false)}
        return (s,r)
    }
    func install(_ image: GrayImage) throws {
        let envelope=try image.envelope(),(s,old)=try inspect()
        try require(old == nil || old!.adapterHash==adapter.adapterSHA256,"检测到旧版灰度安装；请先使用本软件恢复原厂，再安装新版彩色画面。首次备份不会覆盖。")
        progress("正在保存并核对首次恢复备份",0)
        let root=try makeRoot()
        var r:RecoveryRecord
        if var previous=old {
            try require(try hash(CameraPaths.backup)==adapter.originalSHA256,"机内原库备份损坏。")
            previous.roots.append(root);r=previous;try saveHost(r)
        } else {
            let data=try read(CameraPaths.library);r=RecoveryRecord(snapshot:s,adapter:adapter,root:root);try saveHost(r,stock:data)
        }
        try r.validate(s,adapter:adapter)
        do {
            try markActive(r);try createRoot(root,record:r);try writeJournal(r,root:root)
            progress("正在上传自定义画面",0.4)
            try upload(patch,to:root+"/patch")
            try upload(envelope,to:root+"/image")
            try checkFactory(r)
            let script=Scripts.install(record:r,adapter:adapter,imageHash:digest(envelope),root:root,current:s.libraryHash)
            progress("正在安装并核对显示服务",0.7)
            try runScript(script,root:root,name:"install.sh")
            try require(try hash(CameraPaths.library)==adapter.adapterSHA256 && hash(CameraPaths.image)==digest(envelope),"安装读回校验失败。")
            try require(try read(CameraPaths.mode)==Data("3".utf8),"独立模式3未启用。")
            try checkFactory(r);try servicesAndMapping(true)
            // Remove every recorded RAM staging root; persistent journal remains for recovery.
            for path in r.roots { try cleanRoot(path,record:r) }
        } catch {
            progress("安装未完成，正在尝试恢复原厂",0)
            if (try? restore()) != nil { throw StudioError("安装未通过检查，已自动恢复原厂。请保留恢复包并反馈结果。") }
            throw error
        }
    }
    func restore() throws {
        let (s,optional)=try inspect()
        guard var r=optional else { try require(s.libraryHash==adapter.originalSHA256,"缺少恢复记录。");return }
        let restoreAdapter=try catalog.adapter(for:r)
        let root=try makeRoot();r.roots.append(root);try r.validate(s,adapter:restoreAdapter);try saveHost(r);try markActive(r);try createRoot(root,record:r);try writeJournal(r,root:root);try checkFactory(r)
        if try exists(CameraPaths.backup) {
            if try hash(CameraPaths.backup) != adapter.originalSHA256 {try require(s.libraryHash==adapter.originalSHA256 && s.metadata==r.baseline.metadata,"原厂备份哈希不符，禁止恢复。")}
        }
        else { try require(s.libraryHash==adapter.originalSHA256,"机内原库备份缺失，已停止操作。") }
        progress("正在恢复这台相机的原厂画面",0)
        try runScript(Scripts.restore(record:r,adapter:restoreAdapter,root:root),root:root,name:"restore.sh")
        let restored=try snapshot();try require(restored.libraryHash==adapter.originalSHA256 && restored.metadata==r.baseline.metadata,"原厂库或文件属性未完全恢复。")
        try checkFactory(r);try servicesAndMapping(false)
        // The original has been loaded and validated before removing the recovery record.
        try require(try call("mount -o remount,rw /system;echo $?")=="0","无法完成最后清理。")
        defer { _=try? call("sync;mount -o remount,ro /system") }
        for p in CameraPaths.owned { _=try call("rm -f " + p) }
        _=try call("sync;mount -o remount,ro /system")
        for path in r.roots { try cleanRoot(path,record:r) }
        for p in CameraPaths.owned+r.roots { try require(try !exists(p),"恢复成功，但实验文件尚未清理完成。") }
        try FileManager.default.removeItem(at:folder(r.baseline).appendingPathComponent("active"))
        progress("已恢复原厂，安装文件已清理",1)
    }
}
enum Scripts {
    static func metadata(_ record:RecoveryRecord,path:String) -> String {
        let lines=record.baseline.metadata.components(separatedBy:"\n"),parts=(lines.first ?? "").split(separator:" ").map(String.init)
        guard parts.count==5,let epoch=TimeInterval(parts[4]),lines.count==2,
              parts[0].range(of:"^[0-7]{3,4}$",options:.regularExpression) != nil,
              parts[1...2].allSatisfy({$0.range(of:"^[0-9]+$",options:.regularExpression) != nil}),
              lines[1].range(of:"^u:object_r:[A-Za-z0-9_]+:s0$",options:.regularExpression) != nil else {return "exit 33\n"}
        let formatter=DateFormatter();formatter.locale=Locale(identifier:"en_US_POSIX");formatter.timeZone=TimeZone(secondsFromGMT:0);formatter.dateFormat="yyyyMMddHHmm.ss"
        let stamp=formatter.string(from:Date(timeIntervalSince1970:epoch))
        return "chmod \(parts[0]) \(path)\nchown \(parts[1]):\(parts[2]) \(path)\nTZ=UTC toybox touch -m -t \(stamp) \(path)\n[ \"$(stat -c '%a %u %g %s %Y' \(path))\" = \(shellQuote(parts.joined(separator:" "))) ]\ncase \"$(ls -Z \(path))\" in *\(lines[1])*) ;; *) exit 32;; esac\n"
    }
    static func header(_ root: String) -> String { """
    #!/system/bin/sh
    set -eu
    export PATH=/system/bin:/system/xbin:/sbin
    finish() { rc=$?; mount -o remount,ro /system; if [ "$rc" = 0 ]; then echo OK > \(root)/result; else start weston; start camera-gui; echo FAILED > \(root)/result; fi; }
    trap finish EXIT
    hashok() { set -- $(sha256sum "$1") "$2"; [ "$1" = "$3" ]; }
    """ }
    static func factory(_ record: RecoveryRecord) -> String {
        "hashok /factory_data/settings.ini " + record.baseline.factoryHash + "\n" + "[ \"$(set +e;set -o pipefail;v=$(unrd -p /dev/block/by-name/env -g su_variant 2>/dev/null | toybox base64);r=$?;printf 'KEY=%s\\nRC=%s' \"$v\" \"$r\")\" = " + shellQuote(record.baseline.displayKey) + " ]\n"
    }
    static func restart() -> String { """
    sync
    mount -o remount,ro /system
    start weston
    sleep 3
    [ "$(getprop init.svc.weston)" = running ]
    start camera-gui
    sleep 3
    [ "$(getprop init.svc.camera-gui)" = running ]
    """ }
    static func install(record: RecoveryRecord,adapter: Adapter,imageHash: String,root: String,current: String) -> String {
        let p=CameraPaths.self
        var s=header(root)+"\n"+factory(record)+"hashok " + p.library + " " + current + "\n"
        s += "hashok \(root)/image \(imageHash)\nmount -o remount,rw /system\n"
        s += "if [ ! -e \(p.backup) ]; then hashok \(p.library) \(adapter.originalSHA256); cp -p \(p.library) \(p.backup);\n"+metadata(record,path:p.backup)+"fi\nhashok \(p.backup) \(adapter.originalSHA256)\nsync\n"
        s += "cp -p \(p.backup) \(p.stage)\n"
        var skip=0
        for part in adapter.patches { s += "toybox dd if=\(root)/patch of=\(p.stage) bs=1 skip=\(skip) seek=\(part.offset) count=\(part.size) conv=notrunc 2>/dev/null\n";skip += part.size }
        let expectedLabel = record.baseline.metadata.components(separatedBy:"\n").last ?? ""
        s += "hashok \(p.stage) \(adapter.adapterSHA256)\ncase \"$(ls -Z \(p.stage))\" in *\(expectedLabel)*) ;; *) exit 32;; esac\n"
        s += "LD_PRELOAD=\(p.stage) /system/bin/weston --version >/dev/null 2>&1\n"
        s += "cp \(root)/image \(p.imageStage)\nchmod 644 \(p.imageStage)\nhashok \(p.imageStage) \(imageHash)\nsync\n"
        s += "case \"$(ls -Z \(p.imageStage))\" in *\(expectedLabel)*) ;; *) exit 32;; esac\n"
        s += "stop camera-gui\nstop weston\nmv -f \(p.stage) \(p.library)\nmv -f \(p.imageStage) \(p.image)\nprintf '%s' 3 > \(p.mode)\nchmod 644 \(p.mode)\n"
        s += "case \"$(ls -Z \(p.mode))\" in *\(expectedLabel)*) ;; *) exit 32;; esac\n"
        s += restart()+"\n"+factory(record)+"hashok \(p.library) \(adapter.adapterSHA256)\nhashok \(p.backup) \(adapter.originalSHA256)\n"
        return s
    }
    static func restore(record: RecoveryRecord,adapter: Adapter,root: String) -> String {
        let p=CameraPaths.self
        return header(root)+"\n"+factory(record)+"""
        current=$(sha256sum \(p.library));current=${current%% *}
        case "$current" in \(adapter.originalSHA256)|\(adapter.adapterSHA256)) ;; *) exit 31;; esac
        usebackup=0
        if [ -e \(p.backup) ]; then if hashok \(p.backup) \(adapter.originalSHA256); then usebackup=1; else hashok \(p.library) \(adapter.originalSHA256); fi; fi
        stop camera-gui
        stop weston
        mount -o remount,rw /system
        if [ "$usebackup" = 1 ]; then mv -f \(p.backup) \(p.library); fi
        hashok \(p.library) \(adapter.originalSHA256)
        \(metadata(record,path:p.library))
        rm -f \(p.mode) \(p.image) \(p.imageStage) \(p.stage)
        \(restart())
        \(factory(record))
        """
    }
}
