import Foundation
import AppKit

private func expectFailure(_ body: () throws -> Void) throws {
    var failed=false;do {try body()} catch {failed=true};try require(failed,"Expected rejection did not occur")
}
func runSelfTests() throws {
    let resources=Bundle.main.resourceURL!
    try require(physicalCameras("List of devices attached\nlocal device usb:1 device:eagle2_ec1706_native\n1.2.3.4:5555 device usb:2 device:eagle2_ec1706_native\nphone device usb:3 device:phone\nwaiting offline usb:4 device:eagle2_ec1706_native")==["local"],"ADB selection accepted a wireless, foreign or offline device")
    let adapter=try JSONDecoder().decode(Adapter.self,from:Data(contentsOf:resources.appendingPathComponent("adapter.json")))
    let patch=try Data(contentsOf:resources.appendingPathComponent("patch.bin"));try adapter.validate(patch)
    try require(adapter.adapterSHA256=="768b6f5d53cccbd6db90b053f1614d7de363462c4ed360ee8ffd10c0611d14ef","RGB adapter mismatch")
    let catalog=try AdapterCatalog(resources:resources,current:adapter)
    try require(catalog.adapters[AdapterCatalog.legacyHash] != nil,"Legacy gray recovery adapter absent")
    let workspace=URL(fileURLWithPath:CommandLine.arguments.drop(while:{$0 != "--self-test-root"}).dropFirst().first ?? NSTemporaryDirectory()).appendingPathComponent("startup-studio-selftest-"+UUID().uuidString)
    try FileManager.default.createDirectory(at:workspace,withIntermediateDirectories:true)
    defer {try? FileManager.default.removeItem(at:workspace)}
    let stockURL=CommandLine.arguments.drop(while:{$0 != "--stock"}).dropFirst().first
    var count=0
    func pass(_ name:String) {count+=1;print("PASS",name)}
    if let stockURL,let legacy=catalog.adapters[AdapterCatalog.legacyHash] {
        let stock=try Data(contentsOf:URL(fileURLWithPath:stockURL))
        let historicalPatch=try Data(contentsOf:resources.appendingPathComponent("legacy").appendingPathComponent(AdapterCatalog.legacyHash).appendingPathComponent("patch.bin"))
        _=try legacy.apply(to:stock,patch:historicalPatch)
        final class NoCamera:CameraLink {func command(_ text:String,timeout:UInt32) throws -> String {throw StudioError("Offline test must not use USB")}}
        for variant in [0,2] {
            let baseline=CameraSnapshot(factoryHash:String(repeating:variant==2 ? "a":"b",count:64),variant:variant,displayKey:variant==2 ? "KEY=Mg==\nRC=0":"KEY=MA==\nRC=0",metadata:"755 0 0 2051168 123\nu:object_r:system_file:s0",libraryHash:adapter.originalSHA256)
            let legacyRecord=RecoveryRecord(snapshot:baseline,adapter:legacy,root:"/tmp/x2d-l3-1234abcd")
            let installed=CameraSnapshot(factoryHash:baseline.factoryHash,variant:variant,displayKey:baseline.displayKey,metadata:baseline.metadata,libraryHash:legacy.adapterSHA256)
            try legacyRecord.validate(installed,adapter:catalog.adapter(for:legacyRecord))
            try require(Scripts.restore(record:legacyRecord,adapter:catalog.adapter(for:legacyRecord),root:legacyRecord.roots[0]).contains(legacy.adapterSHA256),"Historical restore would use the RGB adapter")
            let currentRecord=RecoveryRecord(snapshot:baseline,adapter:adapter,root:"/tmp/x2d-l3-abcdef12")
            let controller=try CameraController(link:NoCamera(),resources:resources,backupRoot:workspace.appendingPathComponent("legacy-host-\(variant)"))
            try controller.saveHost(legacyRecord,stock:stock)
            let baselineURL=controller.folder(baseline).appendingPathComponent("baseline.json"),first=try Data(contentsOf:baselineURL)
            try controller.saveHost(currentRecord,stock:stock)
            try require(try Data(contentsOf:baselineURL)==first,"Historical first backup overwritten by RGB transaction")
            pass("historical-gray-recovery-and-immutable-baseline-\(variant)")
        }
    }
    pass("adb-physical-device-selection")
    for model in [CameraModel.x2d,.cfv100] {
        let pid=String(format:"0x%04X",model.usbProductID)
        try require(try CameraModel.detect(platform:"eagle2_ec1706_native",pid:pid,product:model.rawValue,transportPID:model.usbProductID)==model,"Wrong model identification")
        pass("hardware-model-"+model.rawValue)
    }
    for (platform,pid,product,transport) in [("eagle2_ec1706_native","0x0009","CFV 100C",UInt16(9)),("eagle2_ec1706_native","0x000A","X2D 100C",UInt16(10)),("eagle2_ec1706_native","0x000A","CFV 100C",UInt16(9)),("other","0x000A","CFV 100C",UInt16(10)),("eagle2_ec1706_native","0x000A","CFV II 50C",UInt16(10))] {
        try expectFailure {_=try CameraModel.detect(platform:platform,pid:pid,product:product,transportPID:transport)}
        pass("wrong-hardware-model-refused-"+pid+"-"+product)
    }
    try expectFailure {try CameraModel.cfv100.verifyVariant(2)};pass("unsupported-cfv-variant-refused")
    final class SnapshotFixture:CameraLink {
        var usbProductID:UInt16?=10
        var product="CFV 100C",pid="0x000A",libraryHash=Adapter.stockHash
        var commands=[String]()
        func command(_ text:String,timeout:UInt32) throws -> String {
            commands.append(text)
            switch text {
            case "getprop ro.product.device":return "eagle2_ec1706_native"
            case "getprop hbl.usb_pid":return pid
            case "getprop hbl.usb_prod_name":return product
            case "sha256sum /factory_data/settings.ini":return String(repeating:"b",count:64)+"  settings.ini"
            case "prodconfig-tool --print 2>&1 | sed -n '/^SuVariant:/p'":return "SuVariant: 0"
            case "sha256sum "+CameraPaths.library:return libraryHash+"  library"
            default:
                if text.contains("unrd -p ") {return "KEY=\nRC=255"}
                if text.hasPrefix("stat -c '") {return "755 0 0 2051168 123"}
                if text.hasPrefix("ls -Z ") {return "u:object_r:system_file:s0"}
                if text.hasPrefix("[ -f ") || text.hasPrefix("[ ! -L ") {return "0"}
                if text.hasPrefix("[ -e ") {return "1"}
                throw StudioError("Unexpected command in read-only fixture")
            }
        }
    }
    let modelFixture=SnapshotFixture(),modelController=try CameraController(link:SnapshotFixture(),resources:resources,backupRoot:workspace.appendingPathComponent("model-inspect"))
    let (cfvSnapshot,cfvRecord)=try modelController.inspect()
    try require(cfvSnapshot.cameraModel == .cfv100 && cfvRecord==nil,"CFV inspection mislabeled the camera")
    pass("cfv-read-only-controller-inspection")
    modelFixture.libraryHash=String(repeating:"e",count:64)
    let unknownController=try CameraController(link:modelFixture,resources:resources,backupRoot:workspace.appendingPathComponent("unknown-model-inspect"))
    try expectFailure {_=try unknownController.inspect()}
    try require(!modelFixture.commands.contains(where:{$0.contains("mount ") || $0.contains("setprop ") || $0.contains("printf 3")}),"Unknown firmware caused mutation")
    pass("cfv-unknown-firmware-refused-before-write")
    let fakeADB=workspace.appendingPathComponent("adb-fixture")
    try Data("#!/bin/sh\ncase \"$1\" in\nlarge) /usr/bin/head -c 262144 /dev/zero;;\ndenied) echo 'Permission denied' >&2;exit 1;;\nslow) exec /bin/sleep 3;;\nesac\n".utf8).write(to:fakeADB)
    try FileManager.default.setAttributes([.posixPermissions:0o700],ofItemAtPath:fakeADB.path)
    let process=ADBProcess(fakeADB)
    try require(try process.run(["large"],selected:false).count==262144,"ADB child output blocked or truncated")
    pass("adb-large-child-output-without-pipe-deadlock")
    try expectFailure {_=try process.run(["denied"],selected:false)};pass("adb-permission-failure-refused")
    try expectFailure {_=try process.run(["slow"],timeout:0.1,selected:false)};pass("adb-client-timeout")
    try require(safeReadRetry("sha256sum /system/lib64/weston/eagle-backend.so") && safeReadRetry("getprop sys.usb.state") && !safeReadRetry("sha256sum /system/a;rm /system/a") && !safeReadRetry("mount -o remount,rw /system") && !safeReadRetry("printf 3 > /system/a"),"Retry accepted a mutating command")
    try require(!readableFailure("USB LIBUSB_ERROR_TIMEOUT (-7)").contains("LIBUSB"),"User-facing error kept protocol details")
    pass("read-only-retry-and-readable-failure")
    let pixels=Data((0..<321*239).map {UInt8($0%256)}),gray=GrayImage(width:321,height:239,pixels:pixels)
    let data=try gray.envelope();try require(data.count==32+321*239 && String(data:data.prefix(8),encoding:.utf8)=="X2DLOGO3","Gray envelope mismatch");pass("gray-envelope")
    let decoded=try GrayImage.decode(data);try require(decoded.pixels==gray.pixels && decoded.width==gray.width,"Resource decoder changed pixels");pass("resource-roundtrip")
    var broken=data;broken[32]^=1;try expectFailure {_=try GrayImage.decode(broken)};pass("damaged-resource-refused")
    try expectFailure { _=try GrayImage(width:1025,height:1,pixels:Data(count:1025)).envelope() };pass("oversized-image-refused")
    try expectFailure { _=try GrayImage(width:1024,height:769,pixels:Data(count:1024*769)).envelope() };pass("oversized-height-refused")
    let crop=cropped(gray,rect:CGRect(x:1,y:1,width:17,height:13));try require(crop.width==17 && crop.height==13 && crop.pixels[0]==gray.pixels[322],"Pixel crop changed scale or coordinates");pass("crop-exact-pixels")
    let png=workspace.appendingPathComponent("test.png")
    let rep=NSBitmapImageRep(cgImage:cgStartup(gray)!);try rep.representation(using:.png,properties:[:])!.write(to:png)
    let imported=try importStartup(png);try require(imported.width==gray.width && imported.height==gray.height && imported.channels==4,"Import resized image");pass("image-import-dimensions")
    let rgb=GrayImage(width:2,height:1,pixels:Data([3,2,1,0,30,20,10,0]),channels:4)
    let rgbData=try rgb.envelope(),rgbDecoded=try GrayImage.decode(rgbData)
    try require(rgbData[8]==2 && rgbDecoded.pixels==rgb.pixels && rgbDecoded.channels==4,"RGB resource roundtrip failed");pass("rgb-resource-roundtrip")
    try require(rgb.grayscale().pixels==Data([2,18]),"RGB to gray conversion changed pixels");pass("rgb-grayscale-conversion")
    let rgbCrop=cropped(rgb,rect:CGRect(x:1,y:0,width:1,height:1));try require(rgbCrop.channels==4 && rgbCrop.pixels==Data([30,20,10,0]),"RGB crop changed pixels");pass("rgb-crop-exact-pixels")
    let rgbPNG=workspace.appendingPathComponent("rgb.png")
    let rgbRep=NSBitmapImageRep(cgImage:cgStartup(rgb)!);try rgbRep.representation(using:.png,properties:[:])!.write(to:rgbPNG)
    let rgbImported=try importStartup(rgbPNG);try require(rgbImported.pixels==rgb.pixels,"RGB import/export changed pixels");pass("rgb-import-export-exact")
    final class LocalChunkLink:CameraLink {
        let bytes:Data;var shortened=false;var badSize=false;var chunks=0
        init(_ bytes:Data) {self.bytes=bytes}
        func command(_ text:String,timeout:UInt32) throws -> String {
            if text.hasPrefix("stat -c %s ") {return badSize ? "999999999":String(bytes.count)}
            try require(text.hasPrefix("toybox dd if=") && text.contains("bs=16384") && timeout==30000,"Unexpected file read command")
            let part=text.components(separatedBy:"skip=")[1].components(separatedBy:" ")[0]
            guard let block=Int(part) else {throw StudioError("Invalid chunk")}
            chunks+=1;let start=block*16384,end=min(bytes.count,start+16384)
            var data=Data(bytes[start..<end]);if shortened && !data.isEmpty {data.removeLast()}
            return data.base64EncodedString()
        }
    }
    let fileBytes=Data((0..<40001).map {UInt8($0%251)}),chunkLink=LocalChunkLink(Data((0..<40001).map {UInt8($0%251)}))
    let chunkController=try CameraController(link:chunkLink,resources:resources,backupRoot:workspace.appendingPathComponent("read-only-test"))
    try require(try chunkController.read(CameraPaths.library)==fileBytes && chunkLink.chunks==3,"Bounded read changed data or chunk count");pass("large-file-bounded-read")
    chunkLink.shortened=true;try expectFailure {_=try chunkController.read(CameraPaths.library)};pass("short-file-chunk-refused")
    chunkLink.badSize=true;try expectFailure {_=try chunkController.read(CameraPaths.library)};pass("oversized-file-read-refused")
    try require(commandStage("printf '%s' 'private-upload' >> /tmp/x2d-l3-test/data")=="上传数据块" && commandStage("getprop ro.product.device")=="机型检测","Diagnostics exposed command payload")
    try require(DiagnosticEntry(message:"USB local test").line.hasSuffix("USB local test"),"Diagnostic format lost event");pass("diagnostic-stage-without-raw-payload")
    let originalHash=adapter.originalSHA256,currentHash=adapter.adapterSHA256
    for (cameraModel,variant,key) in [(CameraModel.x2d,0,"KEY=\nRC=255"),(.x2d,0,"KEY=MA==\nRC=0"),(.x2d,2,"KEY=Mg==\nRC=0"),(.cfv100,0,"KEY=\nRC=255"),(.cfv100,0,"KEY=MA==\nRC=0")] {
        let baseline=CameraSnapshot(factoryHash:String(repeating:variant==2 ? "a":"b",count:64),variant:variant,displayKey:key,metadata:"755 0 0 2051168 123\nu:object_r:system_file:s0",libraryHash:originalHash,model:cameraModel)
        let record=RecoveryRecord(snapshot:baseline,adapter:adapter,root:"/tmp/x2d-l3-1234abcd")
        var persistentStage=record;persistentStage.roots=["/blackbox/.x2d-l3-1234abcd"]
        try persistentStage.validate(baseline,adapter:adapter)
        persistentStage.roots=["/blackbox/other-app"]
        try expectFailure {try persistentStage.validate(baseline,adapter:adapter)}
        let active=CameraSnapshot(factoryHash:baseline.factoryHash,variant:variant,displayKey:key,metadata:baseline.metadata,libraryHash:currentHash,model:cameraModel)
        let crossModel=CameraSnapshot(factoryHash:baseline.factoryHash,variant:variant,displayKey:key,metadata:baseline.metadata,libraryHash:currentHash,model:cameraModel == .x2d ? .cfv100:.x2d)
        try expectFailure {try record.validate(crossModel,adapter:adapter)};pass("cross-model-record-refused-\(cameraModel.rawValue)-\(variant)")
        if cameraModel == .x2d {
            var oldJSON=try JSONSerialization.jsonObject(with:JSONEncoder().encode(record)) as! [String:Any]
            var oldBaseline=oldJSON["baseline"] as! [String:Any];oldBaseline.removeValue(forKey:"model");oldJSON["baseline"]=oldBaseline
            let oldRecord=try JSONDecoder().decode(RecoveryRecord.self,from:JSONSerialization.data(withJSONObject:oldJSON))
            try oldRecord.validate(active,adapter:adapter)
            try require(oldRecord.baseline==baseline,"Legacy X2D backup no longer compares equal")
            pass("legacy-x2d-record-preserved-\(variant)")
        }
        try record.validate(active,adapter:adapter)
        let install=Scripts.install(record:record,adapter:adapter,imageHash:digest(data),root:record.roots[0],current:originalHash)
        let restore=Scripts.restore(record:record,adapter:adapter,root:record.roots[0])
        for script in [install,restore] {
            try require(!script.contains("unrd -s") && !script.contains("prodconfig-tool --set"),"Factory writer included")
            let file=workspace.appendingPathComponent(UUID().uuidString+".sh");try Data(script.utf8).write(to:file)
            let proc=Process();proc.executableURL=URL(fileURLWithPath:"/bin/sh");proc.arguments=["-n",file.path];try proc.run();proc.waitUntilExit();try require(proc.terminationStatus==0,"Invalid shell script")
        }
        pass("original-variant-\(variant)-key-presence-and-scripts")
        let wrong=CameraSnapshot(factoryHash:String(repeating:"c",count:64),variant:variant,displayKey:key,metadata:baseline.metadata,libraryHash:currentHash,model:cameraModel)
        try expectFailure {try record.validate(wrong,adapter:adapter)};pass("wrong-camera-refused-\(variant)-\(key == "KEY=\nRC=255")")
        let changed=CameraSnapshot(factoryHash:baseline.factoryHash,variant:variant,displayKey:key+"changed",metadata:baseline.metadata,libraryHash:currentHash,model:cameraModel)
        try expectFailure {try record.validate(changed,adapter:adapter)};pass("factory-key-change-refused-\(variant)-\(key == "KEY=\nRC=255")")
        let unknown=CameraSnapshot(factoryHash:baseline.factoryHash,variant:variant,displayKey:key,metadata:baseline.metadata,libraryHash:String(repeating:"e",count:64),model:cameraModel)
        try expectFailure {try record.validate(unknown,adapter:adapter)};pass("unknown-library-refused-\(variant)-\(key == "KEY=\nRC=255")")
        try expectFailure {try verifyDisplayKey("KEY=MA==\nRC=0",variant:2)};pass("earth-identity-with-ordinary-key-refused-\(variant)-\(key == "KEY=\nRC=255")")
        if let stockURL {
            let stock=try Data(contentsOf:URL(fileURLWithPath:stockURL));_=try adapter.apply(to:stock,patch:patch)
            final class NoUSBLink: CameraLink {func command(_ text:String,timeout:UInt32) throws -> String {throw StudioError("Unexpected USB access in local test")}}
            let controller=try CameraController(link:NoUSBLink(),resources:resources,backupRoot:workspace.appendingPathComponent("Backups-"+UUID().uuidString))
            try controller.saveHost(record,stock:stock)
            let first=try Data(contentsOf:controller.folder(record.baseline).appendingPathComponent("baseline.json"))
            var replacement=record;replacement.roots.append("/tmp/x2d-l3-abcdef12");try controller.saveHost(replacement,stock:stock)
            try require(try Data(contentsOf:controller.folder(record.baseline).appendingPathComponent("baseline.json"))==first,"First backup overwritten")
            try controller.markActive(replacement)
            try require(try controller.fallbackRecord(active)==replacement,"Host recovery fallback lost transaction")
            pass("immutable-first-backup-and-host-fallback-\(variant)-\(key == "KEY=\nRC=255")")
            try testShellTransactions(workspace:workspace,record:record,adapter:adapter,stock:stock,patch:patch,image:data)
            pass("install-replace-restore-and-interruption-filesystem-\(variant)-\(key == "KEY=\nRC=255")")
        }
    }
    let receipt:[String:Any]=["nativeCases":count,"deviceAccessed":false,"factoryWrites":false,"passed":true]
    let result=URL(fileURLWithPath:CommandLine.arguments.drop(while:{$0 != "--report"}).dropFirst().first ?? workspace.appendingPathComponent("report.json").path)
    try JSONSerialization.data(withJSONObject:receipt,options:[.prettyPrinted,.sortedKeys]).write(to:result)
    print("PASS: \(count) native and filesystem checks; USB not accessed")
}
private func testShellTransactions(workspace:URL,record:RecoveryRecord,adapter:Adapter,stock:Data,patch:Data,image:Data) throws {
    let base=workspace.appendingPathComponent(UUID().uuidString);try FileManager.default.createDirectory(at:base,withIntermediateDirectories:true)
    let system=base.appendingPathComponent("system"),temp=base.appendingPathComponent("tmp/x2d-l3-1234abcd"),factory=base.appendingPathComponent("factory_data")
    try FileManager.default.createDirectory(at:system.appendingPathComponent("lib64/weston"),withIntermediateDirectories:true)
    try FileManager.default.createDirectory(at:system.appendingPathComponent("bin"),withIntermediateDirectories:true)
    try FileManager.default.createDirectory(at:temp,withIntermediateDirectories:true)
    try FileManager.default.createDirectory(at:factory,withIntermediateDirectories:true)
    func local(_ p:String) -> URL {URL(fileURLWithPath:base.path+p)}
    let fakeWeston=system.appendingPathComponent("bin/weston");try Data("#!/bin/sh\nexit 0\n".utf8).write(to:fakeWeston);try FileManager.default.setAttributes([.posixPermissions:0o755],ofItemAtPath:fakeWeston.path)
    let context=record.baseline.metadata.components(separatedBy:"\n").last!
    let encodedKey=String(record.baseline.displayKey.components(separatedBy:"\n")[0].dropFirst(4)),rc=record.baseline.displayKey.components(separatedBy:"RC=").last!
    let firstKey=String(data:Data(base64Encoded:encodedKey) ?? Data(),encoding:.utf8) ?? ""
    let shim="""
    mount() { return 0; }
    start() { return 0; }
    stop() { return 0; }
    chown() { return 0; }
    sleep() { return 0; }
    sync() { return 0; }
    getprop() { echo running; }
    unrd() { printf '%s' \(shellQuote(firstKey)); return \(rc); }
    sha256sum() { case "$1" in */factory_data/settings.ini) echo '\(record.baseline.factoryHash)  settings.ini';; *) /usr/bin/shasum -a 256 "$1";; esac; }
    ls() { printf '%s\\n' '\(context)'; }
    stat() { printf '%s\\n' '755 0 0 2051168 123'; }
    toybox() { cmd=$1;shift; "$cmd" "$@"; }
    """
    func translate(_ script:String) -> String {
        script.replacingOccurrences(of:"/system",with:base.path+"/system").replacingOccurrences(of:"/factory_data",with:base.path+"/factory_data").replacingOccurrences(of:"/tmp/x2d-l3-1234abcd",with:temp.path).replacingOccurrences(of:"export PATH=/system/bin:/system/xbin:/sbin",with:"")
    }
    func run(_ script:String) throws -> Int32 {
        let source=translate(script).replacingOccurrences(of:"set -eu",with:"set -eu\n"+shim).replacingOccurrences(of:"export PATH="+base.path+"/system/bin:"+base.path+"/system/xbin:/sbin",with:"export PATH=/usr/bin:/bin:/usr/sbin:/sbin")
        let file=base.appendingPathComponent("transaction.sh");try Data(source.utf8).write(to:file)
        let p=Process();p.executableURL=URL(fileURLWithPath:"/bin/sh");p.arguments=[file.path];p.standardOutput=FileHandle.nullDevice;let errors=Pipe();p.standardError=errors;try p.run();let log=errors.fileHandleForReading.readDataToEndOfFile();p.waitUntilExit();if p.terminationStatus != 0 && p.terminationStatus != 85 { print("Transaction exit",p.terminationStatus,String(data:log.suffix(3500),encoding:.utf8) ?? "") };return p.terminationStatus
    }
    func reset() throws {
        for path in CameraPaths.owned {try? FileManager.default.removeItem(at:local(path))}
        try stock.write(to:local(CameraPaths.library));try FileManager.default.setAttributes([.posixPermissions:0o755,.modificationDate:Date(timeIntervalSince1970:123)],ofItemAtPath:local(CameraPaths.library).path)
        try patch.write(to:temp.appendingPathComponent("patch"));try image.write(to:temp.appendingPathComponent("image"))
    }
    let install=Scripts.install(record:record,adapter:adapter,imageHash:digest(image),root:record.roots[0],current:adapter.originalSHA256)
    let restore=Scripts.restore(record:record,adapter:adapter,root:record.roots[0])
    try reset();try require(try run(install)==0,"Filesystem installation failed")
    try require(digest(Data(contentsOf:local(CameraPaths.library)))==adapter.adapterSHA256,"Installed library hash mismatch")
    try require(digest(Data(contentsOf:local(CameraPaths.backup)))==adapter.originalSHA256,"Original backup overwritten")
    let update=Scripts.install(record:record,adapter:adapter,imageHash:digest(image),root:record.roots[0],current:adapter.adapterSHA256)
    try require(try run(update)==0,"Repeated image installation failed")
    try require(digest(Data(contentsOf:local(CameraPaths.backup)))==adapter.originalSHA256,"Repeated install overwrote baseline")
    try require(try run(restore)==0 && run(restore)==0,"Restore or repeated restore failed")
    try require(digest(Data(contentsOf:local(CameraPaths.library)))==adapter.originalSHA256,"Original library not returned")
    let attrs=try FileManager.default.attributesOfItem(atPath:local(CameraPaths.library).path)
    try require(attrs[.posixPermissions] as? Int == 0o755 && (attrs[.modificationDate] as? Date)?.timeIntervalSince1970==123,"Original attributes not returned")
    for path in [CameraPaths.mode,CameraPaths.image,CameraPaths.imageStage,CameraPaths.stage,CameraPaths.backup] {try require(!FileManager.default.fileExists(atPath:local(path).path),"Own artifact remained")}
    try FileManager.default.setAttributes([.modificationDate:Date(timeIntervalSince1970:999)],ofItemAtPath:local(CameraPaths.library).path)
    try require(try run(restore)==0,"Restore did not repair known stock metadata without a remaining device backup")
    let repaired=try FileManager.default.attributesOfItem(atPath:local(CameraPaths.library).path)
    try require((repaired[.modificationDate] as? Date)?.timeIntervalSince1970==123,"First-backup file timestamp was not restored")
    try reset();try Data("partial-backup".utf8).write(to:local(CameraPaths.backup))
    try require(try run(restore)==0 && digest(Data(contentsOf:local(CameraPaths.library)))==adapter.originalSHA256,"Partial backup prevented intact original recovery")
    // Interrupt the actual generated shell before/after each persistent mutation boundary.
    let boundaries=["cp -p "+CameraPaths.backup,"toybox dd", "LD_PRELOAD=", "cp "+record.roots[0]+"/image", "stop camera-gui", "mv -f "+CameraPaths.stage, "mv -f "+CameraPaths.imageStage, "printf '%s' 3", "chmod 644 "+CameraPaths.mode, "\nstart weston\n"]
    for boundary in boundaries {
        try reset()
        guard let range=install.range(of:boundary) else {throw StudioError("Missing interruption boundary")}
        let partial=String(install[..<range.lowerBound])+"\nexit 85\n"
        try require(try run(partial)==85,"Interruption was not injected at a complete shell boundary")
        try require(try run(restore)==0,"Interrupted install could not restore at "+boundary)
        try require(digest(Data(contentsOf:local(CameraPaths.library)))==adapter.originalSHA256,"Interrupted restore lost factory library")
    }
}
