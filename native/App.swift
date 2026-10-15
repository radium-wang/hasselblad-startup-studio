import SwiftUI
import AppKit
import ImageIO
import UniformTypeIdentifiers

func cgStartup(_ image: GrayImage) -> CGImage? {
    guard [1,4].contains(image.channels),image.pixels.count==image.width*image.height*image.channels else {return nil}
    if image.channels==1 {
        guard let provider=CGDataProvider(data:image.pixels as CFData) else {return nil}
        return CGImage(width:image.width,height:image.height,bitsPerComponent:8,bitsPerPixel:8,bytesPerRow:image.width,space:CGColorSpaceCreateDeviceGray(),bitmapInfo:CGBitmapInfo(rawValue:0),provider:provider,decode:nil,shouldInterpolate:false,intent:.defaultIntent)
    }
    var rgba=Data(count:image.pixels.count)
    for i in 0..<image.width*image.height {
        let at=i*4;rgba[at]=image.pixels[at+2];rgba[at+1]=image.pixels[at+1];rgba[at+2]=image.pixels[at];rgba[at+3]=255
    }
    guard let provider=CGDataProvider(data:rgba as CFData),let space=CGColorSpace(name:CGColorSpace.sRGB) else {return nil}
    return CGImage(width:image.width,height:image.height,bitsPerComponent:8,bitsPerPixel:32,bytesPerRow:image.width*4,space:space,bitmapInfo:CGBitmapInfo(rawValue:CGImageAlphaInfo.premultipliedLast.rawValue|CGBitmapInfo.byteOrder32Big.rawValue),provider:provider,decode:nil,shouldInterpolate:false,intent:.defaultIntent)
}
func importStartup(_ url: URL) throws -> GrayImage {
    guard let src=CGImageSourceCreateWithURL(url as CFURL,nil),CGImageSourceGetCount(src)==1,
          let props=CGImageSourceCopyPropertiesAtIndex(src,0,nil) as? [CFString:Any],
          let w=props[kCGImagePropertyPixelWidth] as? Int,let h=props[kCGImagePropertyPixelHeight] as? Int else { throw StudioError("请选择一张 PNG 或 JPEG 图片。") }
    let orientation=props[kCGImagePropertyOrientation] as? Int ?? 1
    let ow=orientation>=5 ? h:w,oh=orientation>=5 ? w:h
    try require(w>0 && h>0 && ow<=1024 && oh<=768,"导入图片宽度不得超过 1024、高度不得超过 768 像素，请先调整大小。")
    let options:[CFString:Any]=[kCGImageSourceCreateThumbnailFromImageAlways:true,kCGImageSourceCreateThumbnailWithTransform:true,kCGImageSourceThumbnailMaxPixelSize:max(w,h)]
    guard let cg=CGImageSourceCreateThumbnailAtIndex(src,0,options as CFDictionary),cg.width==ow,cg.height==oh else { throw StudioError("图片解码或方向处理失败。") }
    var rgba=[UInt8](repeating:0,count:ow*oh*4)
    let ok=rgba.withUnsafeMutableBytes { b -> Bool in
        guard let c=CGContext(data:b.baseAddress,width:ow,height:oh,bitsPerComponent:8,bytesPerRow:ow*4,space:CGColorSpace(name:CGColorSpace.sRGB)!,bitmapInfo:CGImageAlphaInfo.premultipliedLast.rawValue|CGBitmapInfo.byteOrder32Big.rawValue) else { return false }
        c.setFillColor(CGColor(gray:0,alpha:1));c.fill(CGRect(x:0,y:0,width:ow,height:oh));c.draw(cg,in:CGRect(x:0,y:0,width:ow,height:oh));return true
    }
    try require(ok,"无法转换图片。")
    var pixels=Data(capacity:ow*oh*4)
    for at in stride(from:0,to:rgba.count,by:4) {
        pixels.append(rgba[at+2]);pixels.append(rgba[at+1]);pixels.append(rgba[at]);pixels.append(0)
    }
    return GrayImage(width:ow,height:oh,pixels:pixels,channels:4)
}
func cropped(_ source: GrayImage,rect: CGRect) -> GrayImage {
    let x=max(0,min(source.width-1,Int(rect.minX.rounded()))),y=max(0,min(source.height-1,Int(rect.minY.rounded())))
    let w=max(1,min(source.width-x,Int(rect.width.rounded()))),h=max(1,min(source.height-y,Int(rect.height.rounded())))
    var data=Data(capacity:w*h*source.channels)
    for row in y..<y+h { data.append(source.pixels[(row*source.width+x)*source.channels..<(row*source.width+x+w)*source.channels]) }
    return GrayImage(width:w,height:h,pixels:data,channels:source.channels)
}
final class CropEditorView: NSView {
    var image: GrayImage?;var rect=CGRect.zero;var changed:((CGRect)->Void)?;var active=false
    private var dragStart=CGPoint.zero,startRect=CGRect.zero,kind=0
    override var isFlipped: Bool { true }
    var imageFrame: CGRect {
        guard let image else { return .zero };let k=min((bounds.width-24)/CGFloat(image.width),(bounds.height-24)/CGFloat(image.height))
        return CGRect(x:(bounds.width-CGFloat(image.width)*k)/2,y:(bounds.height-CGFloat(image.height)*k)/2,width:CGFloat(image.width)*k,height:CGFloat(image.height)*k)
    }
    func toView(_ r:CGRect) -> CGRect {
        guard let image else { return .zero };let f=imageFrame,k=f.width/CGFloat(image.width)
        return CGRect(x:f.minX+r.minX*k,y:f.minY+r.minY*k,width:r.width*k,height:r.height*k)
    }
    func point(_ event:NSEvent) -> CGPoint {
        guard let image else { return .zero };let p=convert(event.locationInWindow,from:nil),f=imageFrame,k=f.width/CGFloat(image.width)
        return CGPoint(x:max(0,min(CGFloat(image.width),(p.x-f.minX)/k)),y:max(0,min(CGFloat(image.height),(p.y-f.minY)/k)))
    }
    override func draw(_ dirtyRect:NSRect) {
        NSColor(calibratedWhite:0.06,alpha:1).setFill();bounds.fill()
        guard let image,let cg=cgStartup(image) else { return }
        NSImage(cgImage:cg,size:NSSize(width:image.width,height:image.height)).draw(in:imageFrame)
        if active {
            let frame=toView(rect)
            let shade=NSBezierPath(rect:imageFrame);shade.append(NSBezierPath(rect:frame));shade.windingRule = .evenOdd
            NSColor.black.withAlphaComponent(0.5).setFill();shade.fill()
            NSColor.systemOrange.setStroke();let border=NSBezierPath(rect:frame);border.lineWidth=1.5;border.stroke()
            for p in [CGPoint(x:frame.minX,y:frame.minY),CGPoint(x:frame.maxX,y:frame.minY),CGPoint(x:frame.minX,y:frame.maxY),CGPoint(x:frame.maxX,y:frame.maxY)] {
                NSColor.white.setFill();NSBezierPath(roundedRect:CGRect(x:p.x-4,y:p.y-4,width:8,height:8),xRadius:2,yRadius:2).fill()
            }
        }
    }
    override func mouseDown(with event:NSEvent) {
        guard active,let image,imageFrame.contains(convert(event.locationInWindow,from:nil)) else { return }
        dragStart=point(event);startRect=rect;kind=0;let frame=toView(rect),p=convert(event.locationInWindow,from:nil)
        let corners=[CGPoint(x:frame.minX,y:frame.minY),CGPoint(x:frame.maxX,y:frame.minY),CGPoint(x:frame.minX,y:frame.maxY),CGPoint(x:frame.maxX,y:frame.maxY)]
        for (i,c) in corners.enumerated() where hypot(p.x-c.x,p.y-c.y)<12 { kind=i+2;break }
        let full=rect.minX<=0 && rect.minY<=0 && rect.width>=CGFloat(image.width) && rect.height>=CGFloat(image.height)
        if kind==0 && rect.contains(dragStart) && !full {kind=1}
    }
    override func mouseDragged(with event:NSEvent) {
        guard active,let image else { return };let p=point(event)
        if kind==1 {
            rect.origin=CGPoint(x:max(0,min(CGFloat(image.width)-rect.width,startRect.minX+p.x-dragStart.x)),y:max(0,min(CGFloat(image.height)-rect.height,startRect.minY+p.y-dragStart.y)))
        } else {
            let anchor:CGPoint
            switch kind {
            case 2:anchor=CGPoint(x:startRect.maxX,y:startRect.maxY)
            case 3:anchor=CGPoint(x:startRect.minX,y:startRect.maxY)
            case 4:anchor=CGPoint(x:startRect.maxX,y:startRect.minY)
            case 5:anchor=startRect.origin
            default:anchor=dragStart
            }
            rect=CGRect(x:min(anchor.x,p.x),y:min(anchor.y,p.y),width:max(1,abs(p.x-anchor.x)),height:max(1,abs(p.y-anchor.y)))
        }
        changed?(rect);needsDisplay=true
    }
    override func mouseUp(with event:NSEvent) { if active { changed?(rect.integral);needsDisplay=true } }
}
struct CropEditor:NSViewRepresentable {
    let image:GrayImage?;@Binding var rect:CGRect;let active:Bool
    func makeNSView(context:Context)->CropEditorView { CropEditorView() }
    func updateNSView(_ v:CropEditorView,context:Context) { v.image=image;v.rect=rect;v.active=active;v.changed={rect=$0};v.needsDisplay=true }
}
struct DiagnosticEntry:Identifiable {
    let id=UUID();let time=Date();let message:String
    var line:String {let formatter=DateFormatter();formatter.dateFormat="HH:mm:ss.SSS";return formatter.string(from:time)+"  "+message}
}
func readableFailure(_ message:String) -> String {
    if message.contains("USB 原状态恢复未确认") {return "USB 连接状态恢复尚未确认，请重启相机后重新检测。"}
    if message.contains("LIBUSB_ERROR_NO_DEVICE") || message.contains("未检测到支持的相机") {return "相机连接已断开，请唤醒相机并检查数据线后重新检测。"}
    if message.contains("LIBUSB_ERROR_BUSY") {return "USB 接口正被其他程序占用，请关闭其他相机工具后重试。"}
    if message.contains("LIBUSB_ERROR_TIMEOUT") {return "相机未完成响应，请保持开机后重新检测。"}
    if message.contains("LIBUSB_ERROR_IO") {return "USB 传输中断，请检查连接后重新检测。"}
    return message
}
final class StudioModel:ObservableObject {
    @Published var source:GrayImage?;@Published var crop=CGRect.zero;@Published var shouldCrop=false;@Published var useColor=true;@Published var filename="未导入图片"
    @Published var snapshot:CameraSnapshot?;@Published var canRestore=false;@Published var canInstall=false;@Published var current="等待连接相机"
    @Published var busy=false;@Published var progress=0.0;@Published var status="导入图片预览，或连接相机检查"
    @Published var logs=[DiagnosticEntry(message:"已准备好，请连接相机")]
    func appendLog(_ message:String) {
        DispatchQueue.main.async {self.logs.append(DiagnosticEntry(message:message));if self.logs.count>500 {self.logs.removeFirst(self.logs.count-500)}}
    }
    @Published var error:String?;@Published var ready=false
    var output:GrayImage? { guard let s=source else {return nil};let prepared=shouldCrop ? cropped(s,rect:crop):s;return useColor ? prepared:prepared.grayscale() }
    var backupRoot:URL { FileManager.default.urls(for:.applicationSupportDirectory,in:.userDomainMask)[0].appendingPathComponent("X2DStartupStudio/Backups") }
    var resources:URL { Bundle.main.resourceURL! }
    func importImage() {
        let panel=NSOpenPanel();panel.allowedContentTypes=[.png,.jpeg];panel.allowsMultipleSelection=false
        if panel.runModal() == .OK,let url=panel.url {
            do { let s=try importStartup(url);source=s;crop=CGRect(x:0,y:0,width:s.width,height:s.height);filename=url.lastPathComponent;status="图片已导入，按原尺寸居中预览" }
            catch { self.error=error.localizedDescription }
        }
    }
    func exportImage() {
        guard let image=output,let cg=cgStartup(image) else {return}
        let panel=NSSavePanel();panel.allowedContentTypes=[.png];panel.nameFieldStringValue="startup.png"
        if panel.runModal() == .OK,let url=panel.url {
            guard let dst=CGImageDestinationCreateWithURL(url as CFURL,UTType.png.identifier as CFString,1,nil) else { error="无法保存图片。";return }
            CGImageDestinationAddImage(dst,cg,nil);if !CGImageDestinationFinalize(dst) {error="图片保存失败。"}
        }
    }
    func perform(_ operation:Int) {
        guard !busy else {return};let chosen=output;busy=true;progress=0;error=nil
        status=operation==0 ? "正在连接并检查相机":operation==1 ? "正在准备自定义画面":"正在准备恢复原厂画面"
        appendLog("开始："+status)
        DispatchQueue.global(qos:.userInitiated).async { [self] in
            var activeLink:ManagedCameraLink?
            let diagnostics=DiagnosticFile()
            do {
                let link=try ManagedCameraLink(resources:resources,log:{self.appendLog($0)},trace:{diagnostics.append($0)})
                activeLink=link
                let controller=try CameraController(link:link,resources:resources,backupRoot:backupRoot)
                var lastPhase=""
                controller.progress={ text,value in
                    if text != lastPhase {self.appendLog(text);lastPhase=text}
                    DispatchQueue.main.async {self.status=text;self.progress=value}
                }
                if operation==1 { guard let chosen else {throw StudioError("请先导入图片。")};try controller.install(chosen) }
                if operation==2 {try controller.restore()}
                let (s,r)=try controller.inspect()
                let isCustom=s.libraryHash != Adapter.stockHash && r != nil
                var current=s.edition+"原厂画面"
                if isCustom {
                    let mode=try? controller.read(CameraPaths.mode)
                    let image=(try? GrayImage.decode(controller.read(CameraPaths.image)))
                    if mode==Data("3".utf8),let image {
                        current=r?.adapterHash != controller.adapter.adapterSHA256 ? "旧版灰度画面 · 请先恢复原厂":image.channels==4 ? "自定义 RGB 彩色画面":"自定义灰度画面"
                    } else {current="自定义设置异常 · 原厂回退"}
                } else if r != nil {current="原厂画面 · 待清理安装记录"}
                try link.finish();activeLink=nil
                DispatchQueue.main.async {
                    self.snapshot=s;self.canRestore=r != nil;self.canInstall=r == nil || r?.adapterHash==controller.adapter.adapterSHA256;self.current=current;self.ready=true;self.busy=false;self.progress=1
                    self.status=operation==0 ? "相机检查完成":operation==1 ? "安装完成，请关机再开机确认画面":"已恢复"+s.edition+"原厂画面，请重启后重新检测"
                    self.appendLog("完成："+self.status+"；"+s.edition+"，"+current)
                }
            } catch {
                var message=error.localizedDescription
                if let link=activeLink {do {try link.finish()} catch {message+="\n"+error.localizedDescription}}
                diagnostics.append(message)
                let visible=readableFailure(message)
                self.appendLog("未完成："+visible)
                DispatchQueue.main.async { self.busy=false;self.ready=false;self.canInstall=false;self.error=visible;self.status="操作未完成，请查看日志中的原因" }
            }
        }
    }
}
struct StudioView:View {
    @StateObject var model=StudioModel()
    private let accent=Color(red:0.89,green:0.43,blue:0.14)
    private var panel:Color {Color(nsColor:.controlBackgroundColor)}
    private func card<Content:View>(@ViewBuilder _ content:()->Content)->some View {
        content().padding(18).frame(maxWidth:.infinity,maxHeight:.infinity,alignment:.topLeading)
            .background(panel).clipShape(RoundedRectangle(cornerRadius:14))
            .overlay(RoundedRectangle(cornerRadius:14).stroke(Color.primary.opacity(0.07),lineWidth:1))
    }
    var body:some View {
        VStack(alignment:.leading,spacing:18) {
            HStack(spacing:12) {
                Image(systemName:"camera.viewfinder").font(.system(size:25,weight:.medium)).foregroundColor(accent)
                    .frame(width:48,height:48).background(accent.opacity(0.09)).cornerRadius(12)
                VStack(alignment:.leading,spacing:4) {
                    Text("开机画面").font(.system(size:23,weight:.semibold))
                    Text("Hasselblad X2D / 907X · CFV 100C").font(.system(size:12)).foregroundColor(.secondary)
                }
                Spacer()
                Text("测试版 0.3.0-rgb-preview.5").font(.system(size:11,weight:.medium)).foregroundColor(.secondary)
                    .padding(.horizontal,10).padding(.vertical,6).background(Color.primary.opacity(0.04)).cornerRadius(6)
                Button(model.snapshot == nil ? "连接相机":"重新检测") {model.perform(0)}.controlSize(.large).disabled(model.busy)
            }
            HStack(spacing:22) {
                HStack(spacing:10) {
                    Circle().fill(model.ready ? Color.green:Color.gray.opacity(0.5)).frame(width:7,height:7)
                    VStack(alignment:.leading,spacing:4) {
                        Text("相机版本").font(.system(size:11)).foregroundColor(.secondary)
                        Text(model.snapshot.map {$0.cameraModel.title+" · "+$0.edition} ?? "等待连接").font(.system(size:13,weight:.medium))
                    }
                }.frame(maxWidth:.infinity,alignment:.leading)
                Rectangle().fill(Color.primary.opacity(0.09)).frame(width:1,height:30)
                VStack(alignment:.leading,spacing:4) {
                    Text("当前开机画面").font(.system(size:11)).foregroundColor(.secondary)
                    Text(model.current).font(.system(size:13,weight:.medium))
                }.frame(maxWidth:.infinity,alignment:.leading)
            }.padding(.horizontal,18).padding(.vertical,13).background(panel).cornerRadius(12)
            HStack(alignment:.top,spacing:18) {
                card {
                    VStack(alignment:.leading,spacing:12) {
                        Text("准备图片").font(.system(size:14,weight:.semibold))
                        Button {model.importImage()} label: {
                            Label("导入图片…",systemImage:"photo.badge.plus").frame(maxWidth:.infinity)
                        }.controlSize(.large).disabled(model.busy)
                        HStack {
                            Text(model.filename).font(.system(size:11)).foregroundColor(.secondary).lineLimit(1).truncationMode(.middle)
                            Spacer(minLength:4)
                            if let image=model.source {Text("\(image.width) × \(image.height)").font(.system(size:10,design:.monospaced)).foregroundColor(.secondary)}
                        }.frame(height:15)
                        Picker("颜色",selection:$model.useColor) {
                            Text("RGB 彩色").tag(true);Text("黑白灰度").tag(false)
                        }.pickerStyle(.segmented).labelsHidden().disabled(model.busy)
                        Picker("显示方式",selection:$model.shouldCrop) {
                            Text("保留黑边").tag(false);Text("裁剪图片").tag(true)
                        }.pickerStyle(.segmented).labelsHidden().disabled(model.busy)
                            .onChange(of:model.shouldCrop) {value in
                                if model.source != nil {model.status=value ? "已进入裁剪：在左侧图片上拖动画框或四角。":"保留原尺寸，图片居中显示。"}
                            }
                        ZStack {
                            CropEditor(image:model.source,rect:$model.crop,active:model.shouldCrop && !model.busy)
                            if model.source == nil {Text("导入后编辑图片").font(.system(size:12)).foregroundColor(.gray).allowsHitTesting(false)}
                        }.frame(height:180).cornerRadius(8)
                        Text(model.shouldCrop ? "拖动画框或四角调整范围，框外拖拽可重新框选。":"保留原尺寸，图片四周由相机填充黑边。")
                            .font(.system(size:11)).foregroundColor(.secondary).frame(height:30,alignment:.topLeading)
                        HStack(spacing:10) {
                            Button("重置裁剪") {if let image=model.source {model.crop=CGRect(x:0,y:0,width:image.width,height:image.height)}}
                                .disabled(model.source==nil || !model.shouldCrop || model.busy)
                        }
                        Spacer(minLength:0)
                        Divider()
                        Text("PNG／JPEG · 宽 ≤ 1024、高 ≤ 768 像素。\n按原尺寸居中，不缩放；透明区域为黑色。")
                            .font(.system(size:10)).foregroundColor(.secondary).fixedSize(horizontal:false,vertical:true).lineSpacing(3)
                    }
                }.frame(width:280)
                card {
                    VStack(alignment:.leading,spacing:14) {
                        HStack {
                            Text("屏幕预览").font(.system(size:14,weight:.semibold))
                            Spacer()
                            Text("1024 × 768").font(.system(size:11,design:.monospaced)).foregroundColor(.secondary)
                        }
                        GeometryReader {g in
                            let width=min(g.size.width,max(0,g.size.height)*4/3)
                            ZStack {
                                Color.black
                                if let image=model.output,let cg=cgStartup(image) {
                                    Image(decorative:cg,scale:1).interpolation(.none).resizable()
                                        .frame(width:width*CGFloat(image.width)/1024,height:width*3/4*CGFloat(image.height)/768)
                                } else {
                                    VStack(spacing:10) {
                                        Image(systemName:"photo").font(.system(size:27,weight:.ultraLight))
                                        Text("导入图片后预览").font(.system(size:12))
                                    }.foregroundColor(Color(white:0.42))
                                }
                            }.frame(width:width,height:width*3/4)
                                .overlay(Rectangle().stroke(Color.white.opacity(0.07),lineWidth:1))
                                .position(x:g.size.width/2,y:g.size.height/2)
                        }
                        HStack {
                            if let image=model.output {Text("\(image.width) × \(image.height) · \(image.channels==4 ? "RGB 彩色":"8 位灰度")")}
                            else {Text("预览与输出使用同一份像素")}
                            Spacer()
                            Button("导出成品 PNG…") {model.exportImage()}.disabled(model.output==nil || model.busy)
                            Text("原尺寸居中").foregroundColor(.secondary)
                        }.font(.system(size:11)).foregroundColor(.secondary)
                    }
                }
                card {
                    VStack(alignment:.leading,spacing:12) {
                        Text("操作日志").font(.system(size:14,weight:.semibold))
                        ScrollViewReader {proxy in
                            ScrollView {
                                LazyVStack(alignment:.leading,spacing:9) {
                                    ForEach(model.logs) {entry in
                                        Text(entry.line).font(.system(size:10,design:.monospaced))
                                            .foregroundColor(entry.message.hasPrefix("错误：") ? .red:.secondary)
                                            .textSelection(.enabled).fixedSize(horizontal:false,vertical:true).id(entry.id)
                                    }
                                }.frame(maxWidth:.infinity,alignment:.leading)
                            }.onChange(of:model.logs.last?.id) {id in if let id {proxy.scrollTo(id,anchor:.bottom)}}
                        }
                    }
                }.frame(width:272)
            }.frame(maxHeight:.infinity)
            HStack(alignment:.center,spacing:18) {
                VStack(alignment:.leading,spacing:6) {
                    Text(model.status).font(.system(size:12,weight:.medium)).lineLimit(2)
                    if model.busy {ProgressView(value:model.progress).frame(maxWidth:320)}
                    else {Text(model.snapshot.map {"恢复将复原为这台相机的"+$0.edition+"原厂画面。"} ?? "连接后检测原厂版本，首次恢复备份保留在这台 Mac。")
                        .font(.system(size:11)).foregroundColor(.secondary).lineLimit(2)}
                }
                Spacer(minLength:10)
                Button("恢复原厂") {model.perform(2)}.controlSize(.large).disabled(model.busy || !model.ready || !model.canRestore)
                Button("安装自定义画面") {model.perform(1)}.buttonStyle(.borderedProminent).controlSize(.large).tint(accent)
                    .disabled(model.busy || !model.ready || !model.canInstall || model.output==nil)
            }.padding(.horizontal,18).padding(.vertical,17).background(panel).cornerRadius(12)
        }.padding(24).frame(minWidth:1120,minHeight:760).background(Color(nsColor:.windowBackgroundColor))
        .alert("操作提示",isPresented:Binding(get:{model.error != nil},set:{if !$0 {model.error=nil}})) {
            Button("知道了",role:.cancel) {model.error=nil}
        } message: {Text(model.error ?? "")}
    }
}
@main struct StartupStudioApp:App {
    init() {
        if CommandLine.arguments.contains("--self-test") {
            do { try runSelfTests(); exit(0) } catch { fputs("SELF TEST FAILED: \(error)\n",stderr);exit(1) }
        }
    }
    var body:some Scene { WindowGroup {StudioView()}.defaultSize(width:1280,height:820).windowResizability(.contentMinSize) }
}
