import AppKit
import UniformTypeIdentifiers

/// 从剪贴板读图片，压缩成发给 AI 的 JPEG
enum ClipImage {
    /// 截图、网页里「拷贝图像」、或者在访达里拷贝的图片文件
    static func read(_ pb: NSPasteboard) -> NSImage? {
        // 访达里拷贝的图片文件
        let fileOpts: [NSPasteboard.ReadingOptionKey: Any] = [
            .urlReadingFileURLsOnly: true,
            .urlReadingContentsConformToTypes: [UTType.image.identifier],
        ]
        if let urls = pb.readObjects(forClasses: [NSURL.self], options: fileOpts) as? [URL],
           let u = urls.first, let img = NSImage(contentsOf: u) {
            return img
        }
        // 剪贴板里直接放着的图片数据
        if let imgs = pb.readObjects(forClasses: [NSImage.self], options: nil) as? [NSImage],
           let img = imgs.first, img.isValid {
            return img
        }
        return nil
    }

    /// 最长边不超过 1568 像素，透明处垫白底，JPEG 质量 0.85
    static func jpegBase64(_ img: NSImage, maxSide: CGFloat = 1568) -> (base64: String, width: Int, height: Int)? {
        var rect = CGRect(origin: .zero, size: img.size)
        guard let cg = img.cgImage(forProposedRect: &rect, context: nil, hints: nil) else { return nil }
        let w = CGFloat(cg.width), h = CGFloat(cg.height)
        guard w >= 1, h >= 1 else { return nil }
        let scale = min(1, maxSide / max(w, h))
        let tw = max(1, Int((w * scale).rounded()))
        let th = max(1, Int((h * scale).rounded()))

        guard let ctx = CGContext(data: nil, width: tw, height: th, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpaceCreateDeviceRGB(),
                                  bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue) else { return nil }
        ctx.setFillColor(CGColor(red: 1, green: 1, blue: 1, alpha: 1))
        ctx.fill(CGRect(x: 0, y: 0, width: tw, height: th))
        ctx.interpolationQuality = .high
        ctx.draw(cg, in: CGRect(x: 0, y: 0, width: tw, height: th))
        guard let out = ctx.makeImage() else { return nil }

        let rep = NSBitmapImageRep(cgImage: out)
        guard let data = rep.representation(using: .jpeg, properties: [.compressionFactor: 0.85]) else { return nil }
        return (data.base64EncodedString(), cg.width, cg.height)
    }
}
