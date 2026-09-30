#!/usr/bin/env swift
// Renders the SaveIt app icon and builds Resources/AppIcon.icns.
//
//   swift scripts/make_icon.swift            (run from the repo root)
//
// Design: deep near-black squircle, a floating black "island" pill with a soft violet glow,
// and a bold white down-arrow dropping out of it.
import AppKit
import CoreGraphics

let root = URL(fileURLWithPath: FileManager.default.currentDirectoryPath)
let outPNG = root.appendingPathComponent("docs/icon.png")
let iconset = URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent("AppIcon.iconset")
let icns = root.appendingPathComponent("Resources/AppIcon.icns")

func render(size: Int) -> CGImage {
    let S = CGFloat(size)
    let cs = CGColorSpace(name: CGColorSpace.displayP3)!
    let ctx = CGContext(data: nil, width: size, height: size, bitsPerComponent: 8, bytesPerRow: 0,
                        space: cs, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
    ctx.scaleBy(x: S / 1024, y: S / 1024)   // draw in a 1024 canvas
    ctx.setShouldAntialias(true)
    ctx.interpolationQuality = .high

    func rgb(_ r: CGFloat, _ g: CGFloat, _ b: CGFloat, _ a: CGFloat = 1) -> CGColor {
        CGColor(colorSpace: cs, components: [r, g, b, a])!
    }

    // macOS icon grid: 824x824 body centred on the 1024 canvas, continuous-corner radius ~185.
    let body = CGRect(x: 100, y: 100, width: 824, height: 824)
    let squircle = CGPath(roundedRect: body, cornerWidth: 185, cornerHeight: 185, transform: nil)

    // Drop shadow under the tile.
    ctx.saveGState()
    ctx.setShadow(offset: CGSize(width: 0, height: -12), blur: 28, color: rgb(0, 0, 0, 0.45))
    ctx.addPath(squircle); ctx.setFillColor(rgb(0.03, 0.03, 0.05)); ctx.fillPath()
    ctx.restoreGState()

    ctx.saveGState()
    ctx.addPath(squircle); ctx.clip()

    // Base: vertical near-black gradient.
    let base = CGGradient(colorsSpace: cs, colors: [rgb(0.075, 0.07, 0.11), rgb(0.02, 0.02, 0.035)] as CFArray,
                          locations: [0, 1])!
    ctx.drawLinearGradient(base, start: CGPoint(x: 512, y: 924), end: CGPoint(x: 512, y: 100), options: [])

    // Violet glow blooming from under the island.
    let glow = CGGradient(colorsSpace: cs, colors: [rgb(0.55, 0.36, 1.0, 0.42), rgb(0.42, 0.25, 0.95, 0.10),
                                                   rgb(0.30, 0.15, 0.80, 0.0)] as CFArray,
                          locations: [0, 0.5, 1])!
    ctx.drawRadialGradient(glow, startCenter: CGPoint(x: 512, y: 770), startRadius: 0,
                           endCenter: CGPoint(x: 512, y: 740), endRadius: 380, options: [])

    // Faint secondary glow at the bottom for depth.
    let low = CGGradient(colorsSpace: cs, colors: [rgb(0.45, 0.30, 1.0, 0.08), rgb(0.45, 0.30, 1.0, 0)] as CFArray,
                         locations: [0, 1])!
    ctx.drawRadialGradient(low, startCenter: CGPoint(x: 512, y: 180), startRadius: 0,
                           endCenter: CGPoint(x: 512, y: 180), endRadius: 420, options: [])

    // Top sheen on the tile (drawn before the island so the island stays pure black).
    let sheen = CGGradient(colorsSpace: cs, colors: [rgb(1, 1, 1, 0.06), rgb(1, 1, 1, 0)] as CFArray, locations: [0, 1])!
    ctx.drawLinearGradient(sheen, start: CGPoint(x: 512, y: 924), end: CGPoint(x: 512, y: 640), options: [])

    // The island: a floating black capsule near the top, lit from behind by the violet glow.
    let island = CGPath(roundedRect: CGRect(x: 512 - 215, y: 700, width: 430, height: 128),
                        cornerWidth: 64, cornerHeight: 64, transform: nil)
    ctx.saveGState()
    ctx.setShadow(offset: CGSize(width: 0, height: -6), blur: 70, color: rgb(0.58, 0.40, 1.0, 0.9))
    ctx.addPath(island); ctx.setFillColor(rgb(0, 0, 0)); ctx.fillPath()
    ctx.restoreGState()
    ctx.addPath(island); ctx.setFillColor(rgb(0, 0, 0)); ctx.fillPath()
    // Hairline rim + tiny "camera" dot so it reads as an island.
    ctx.addPath(island); ctx.setStrokeColor(rgb(0.75, 0.65, 1.0, 0.28)); ctx.setLineWidth(3); ctx.strokePath()
    ctx.addEllipse(in: CGRect(x: 512 + 215 - 64 - 22, y: 764 - 22, width: 44, height: 44))
    ctx.setFillColor(rgb(0.10, 0.09, 0.16)); ctx.fillPath()
    ctx.addEllipse(in: CGRect(x: 512 + 215 - 64 - 9, y: 764 - 9, width: 18, height: 18))
    ctx.setFillColor(rgb(0.30, 0.26, 0.55, 0.9)); ctx.fillPath()

    // White down-arrow below the island.
    let shaftW: CGFloat = 84, headW: CGFloat = 270, headH: CGFloat = 150
    let top: CGFloat = 640, tip: CGFloat = 318
    let arrow = CGMutablePath()
    arrow.move(to: CGPoint(x: 512 - shaftW / 2, y: top))
    arrow.addLine(to: CGPoint(x: 512 + shaftW / 2, y: top))
    arrow.addLine(to: CGPoint(x: 512 + shaftW / 2, y: tip + headH))
    arrow.addLine(to: CGPoint(x: 512 + headW / 2, y: tip + headH))
    arrow.addLine(to: CGPoint(x: 512, y: tip))
    arrow.addLine(to: CGPoint(x: 512 - headW / 2, y: tip + headH))
    arrow.addLine(to: CGPoint(x: 512 - shaftW / 2, y: tip + headH))
    arrow.closeSubpath()
    // Round the joins by stroking with a round line join in the fill colour.
    ctx.saveGState()
    ctx.setShadow(offset: CGSize(width: 0, height: -8), blur: 36, color: rgb(0.55, 0.38, 1.0, 0.55))
    ctx.addPath(arrow); ctx.setFillColor(rgb(1, 1, 1)); ctx.fillPath()
    ctx.restoreGState()
    ctx.addPath(arrow); ctx.setStrokeColor(rgb(1, 1, 1)); ctx.setLineWidth(34); ctx.setLineJoin(.round)
    ctx.strokePath()

    // Base line ("tray") under the arrow.
    let tray = CGPath(roundedRect: CGRect(x: 512 - 150, y: 232, width: 300, height: 34),
                      cornerWidth: 18, cornerHeight: 18, transform: nil)
    ctx.addPath(tray); ctx.setFillColor(rgb(1, 1, 1, 0.92)); ctx.fillPath()

    ctx.restoreGState()
    // Inner rim on the tile.
    ctx.addPath(CGPath(roundedRect: body.insetBy(dx: 1.5, dy: 1.5), cornerWidth: 184, cornerHeight: 184, transform: nil))
    ctx.setStrokeColor(rgb(1, 1, 1, 0.08)); ctx.setLineWidth(3); ctx.strokePath()

    return ctx.makeImage()!
}

func writePNG(_ img: CGImage, to url: URL) {
    let rep = NSBitmapImageRep(cgImage: img)
    try! rep.representation(using: .png, properties: [:])!.write(to: url)
}

let fm = FileManager.default
try? fm.createDirectory(at: outPNG.deletingLastPathComponent(), withIntermediateDirectories: true)
try? fm.removeItem(at: iconset)
try! fm.createDirectory(at: iconset, withIntermediateDirectories: true)

writePNG(render(size: 1024), to: outPNG)
for base in [16, 32, 128, 256, 512] {
    writePNG(render(size: base), to: iconset.appendingPathComponent("icon_\(base)x\(base).png"))
    writePNG(render(size: base * 2), to: iconset.appendingPathComponent("icon_\(base)x\(base)@2x.png"))
}

let p = Process()
p.executableURL = URL(fileURLWithPath: "/usr/bin/iconutil")
p.arguments = ["-c", "icns", iconset.path, "-o", icns.path]
try! p.run(); p.waitUntilExit()
try? fm.removeItem(at: iconset)
print(p.terminationStatus == 0 ? "Wrote \(icns.path) and \(outPNG.path)" : "iconutil failed")
exit(p.terminationStatus)
