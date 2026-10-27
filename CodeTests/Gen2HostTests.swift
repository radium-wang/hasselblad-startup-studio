// Offline process fixture: no USB implementation is linked.
import Foundation
@main struct Gen2HostTests {
    static func main() throws {
        let temporary=FileManager.default.temporaryDirectory.appendingPathComponent("hss2-host-"+UUID().uuidString)
        defer {try? FileManager.default.removeItem(at:temporary)}
        let runtime=temporary.appendingPathComponent("gen2/runtime/bin")
        try FileManager.default.createDirectory(at:runtime,withIntermediateDirectories:true)
        let executable=runtime.appendingPathComponent("python3.13"),main=temporary.appendingPathComponent("gen2/main.py")
        try Data().write(to:main)
        func stub(_ text:String) throws {
            try ("#!/bin/sh\n"+text).write(to:executable,atomically:true,encoding:.utf8)
            try FileManager.default.setAttributes([.posixPermissions:0o700],ofItemAtPath:executable.path)
        }
        var logs=[String](),traces=[String]();var count=0
        let host=Gen2Host(resources:temporary,backups:temporary.appendingPathComponent("Backups"),log:{logs.append($0)},trace:{traces.append($0)})
        try stub("printf '%s\\n' '{\"type\":\"result\",\"data\":{\"gen2\":true}}'\n")
        try require(try host.probe(),"probe protocol failed");count+=1
        try stub("""
        test "$1" = -X && test "$2" = utf8 && test "$3" = -P && test "$4" = -s && test "$5" = -B || exit 8
        while [ "$#" -gt 0 ];do
          if [ "$1" = --input ];then shift;test -r "$1" || exit 9;fi
          shift
        done
        printf '%s\\n' '{"type":"progress","message":"正在核对二代画面"}'
        printf '%s\\n' '本地进程替身诊断' >&2
        printf '%s\\n' '{"type":"result","data":{"deviceTitle":"X2D II 100C","edition":"标准版","current":"原厂开机画面","canInstall":true,"canRestore":false}}'
        """)
        let status=try host.operate(1,image:GrayImage(width:1,height:1,pixels:Data([1,2,3,255]),channels:4))
        try require(status.deviceTitle=="X2D II 100C" && status.canInstall && logs==["正在核对二代画面"] && traces.contains(where:{$0.contains("本地进程替身诊断")}),"status, image forwarding or UTF8 lost");count+=1
        for source in ["printf '%s\\n' '{\"type\":\"error\",\"message\":\"测试错误：保留备份\"}';exit 2", "printf '%s\\n' 'not-json'", "exit 1"] {
            try stub(source);var failed=false
            do {_=try host.operate(0,image:nil)} catch {failed=true}
            try require(failed,"invalid helper output accepted");count+=1
        }
        print("PASS: \(count) Mac Gen2 process-protocol checks; no USB linked or accessed")
    }
}
