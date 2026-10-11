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
    let colorSpace = CGColorSpaceCreateDeviceRGB()
    guard let context = CGContext(data: nil, width: size, height: size, bitsPerComponent: 8, bytesPerRow: size * 4, space: colorSpace, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { fatalError("icon context") }
    context.clear(CGRect(x:0,y:0,width:size,height:size))
    let scale = CGFloat(size) / 1024
    context.scaleBy(x:scale,y:scale); context.translateBy(x:0,y:1024); context.scaleBy(x:1,y:-1)
    let tile = CGPath(roundedRect:CGRect(x:80,y:80,width:864,height:864), cornerWidth:190, cornerHeight:190, transform:nil)
    context.saveGState();context.addPath(tile);context.clip()
    let colors: [CGColor] = [CGColor(red:21/255,green:24/255,blue:63/255,alpha:1),CGColor(red:46/255,green:40/255,blue:117/255,alpha:1),CGColor(red:98/255,green:88/255,blue:238/255,alpha:1)]
    let gradient = CGGradient(colorsSpace:colorSpace,colors:colors as CFArray,locations:[0,0.5,1])!
    context.drawLinearGradient(gradient,start:CGPoint(x:80,y:944),end:CGPoint(x:944,y:80),options:[.drawsBeforeStartLocation,.drawsAfterEndLocation]);context.restoreGState()
    let wave = CGMutablePath(); wave.move(to:CGPoint(x:204,y:512));wave.addLine(to:CGPoint(x:246,y:512));wave.addQuadCurve(to:CGPoint(x:275,y:488),control:CGPoint(x:263,y:512))
    for p in [CGPoint(x:293,y:454),CGPoint(x:326,y:569),CGPoint(x:366,y:419),CGPoint(x:408,y:608),CGPoint(x:446,y:346),CGPoint(x:532,y:681),CGPoint(x:672,y:346),CGPoint(x:720,y:512),CGPoint(x:751,y:458),CGPoint(x:784,y:565),CGPoint(x:816,y:512),CGPoint(x:850,y:512)] { wave.addLine(to:p) }
    context.addPath(wave);context.setLineWidth(54);context.setLineCap(.round);context.setLineJoin(.round);context.setStrokeColor(CGColor(gray:1,alpha:1));context.strokePath()
    context.setFillColor(CGColor(red:82/255,green:231/255,blue:237/255,alpha:1));context.fillEllipse(in:CGRect(x:835,y:496,width:32,height:32))
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
