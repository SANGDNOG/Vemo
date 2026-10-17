import Foundation
import CoreGraphics
import ImageIO
import UniformTypeIdentifiers

// Deterministic vector rendering of assets/vemo-icon.svg. No source raster is edited.
let destination = URL(fileURLWithPath: CommandLine.arguments.dropFirst().first ?? "assets")
try FileManager.default.createDirectory(at: destination, withIntermediateDirectories: true)
let iconset = destination.appendingPathComponent("Vemo.iconset")
try FileManager.default.createDirectory(at: iconset, withIntermediateDirectories: true)
func render(_ size: Int, to url: URL) throws {
    let colorSpace = CGColorSpace(name: CGColorSpace.sRGB)!
    guard let context = CGContext(data: nil, width: size, height: size, bitsPerComponent: 8, bytesPerRow: size * 4, space: colorSpace, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { fatalError("icon context") }
    context.clear(CGRect(x:0,y:0,width:size,height:size))
    let scale = CGFloat(size) / 100
    context.scaleBy(x:scale,y:scale); context.translateBy(x:0,y:100); context.scaleBy(x:1,y:-1)
    let tile = CGPath(roundedRect:CGRect(x:8,y:8,width:84,height:84), cornerWidth:18.5, cornerHeight:18.5, transform:nil)
    context.saveGState();context.addPath(tile);context.clip()
    let colors: [CGColor] = [CGColor(colorSpace:colorSpace,components:[167/255,151/255,237/255,1])!,CGColor(colorSpace:colorSpace,components:[130/255,112/255,216/255,1])!]
    let gradient = CGGradient(colorsSpace:colorSpace,colors:colors as CFArray,locations:[0,1])!
    context.drawLinearGradient(gradient,start:CGPoint(x:8,y:8),end:CGPoint(x:92,y:92),options:[.drawsBeforeStartLocation,.drawsAfterEndLocation]);context.restoreGState()
    let wave = CGMutablePath(); wave.move(to:CGPoint(x:18,y:50))
    for p in [CGPoint(x:22,y:50),CGPoint(x:26,y:40),CGPoint(x:31,y:60),CGPoint(x:35,y:50),CGPoint(x:40,y:25),CGPoint(x:54,y:76),CGPoint(x:68,y:25),CGPoint(x:74,y:50),CGPoint(x:78,y:41),CGPoint(x:82,y:50),CGPoint(x:86,y:50)] { wave.addLine(to:p) }
    context.addPath(wave);context.setLineWidth(5.4);context.setLineCap(.round);context.setLineJoin(.round);context.setStrokeColor(CGColor(gray:1,alpha:1));context.strokePath()
    guard let image=context.makeImage(),let output=CGImageDestinationCreateWithURL(url as CFURL,UTType.png.identifier as CFString,1,nil) else { fatalError("icon output") }
    CGImageDestinationAddImage(output,image,nil);guard CGImageDestinationFinalize(output) else { fatalError("icon write") }
}
try render(1024,to:destination.appendingPathComponent("vemo-icon.png"))
for size in [16,32,128,256,512] {
    try render(size,to:iconset.appendingPathComponent("icon_\(size)x\(size).png"))
    try render(size*2,to:iconset.appendingPathComponent("icon_\(size)x\(size)@2x.png"))
}
let process=Process();process.executableURL=URL(fileURLWithPath:"/usr/bin/iconutil");process.arguments=["-c","icns",iconset.path,"-o",destination.appendingPathComponent("Vemo.icns").path];try process.run();process.waitUntilExit();guard process.terminationStatus==0 else { fatalError("iconutil") }
print(destination.appendingPathComponent("Vemo.icns").path)
