import AppKit
import SwiftUI

/// 把 AI 的 Markdown 回答排成富文本（标题、粗体、代码、列表、引用），
/// 并把已经划线追问过的句子标成黄色高亮；点高亮打开那条追问。
enum MindText {
    static let bold: UInt8 = 1, code: UInt8 = 2, heading: UInt8 = 4, quote: UInt8 = 8, codeBlock: UInt8 = 16

    struct Seg {
        var text: String
        var flags: UInt8
    }

    struct Anchor {
        var text: String
        var childId: String
        var tip: String
    }

    private static let inlineRx = try! NSRegularExpression(pattern: "(\\*\\*[^*\\n]+?\\*\\*|__[^_\\n]+?__|`[^`\\n]+`)")
    private static let tableSep = try! NSRegularExpression(pattern: "^\\s*\\|?\\s*:?-{2,}:?\\s*(\\|\\s*:?-{2,}:?\\s*)*\\|?\\s*$")

    /// Markdown → 一行一行带格式的片段
    static func flatten(_ md: String) -> [[Seg]] {
        var lines: [[Seg]] = []
        var inCode = false
        for raw in md.replacingOccurrences(of: "\r", with: "").components(separatedBy: "\n") {
            let tSub = raw.drop(while: { $0 == " " || $0 == "\t" })
            var t = String(tSub)
            if t.hasPrefix("```") { inCode.toggle(); continue }
            if inCode { lines.append(raw.isEmpty ? [] : [Seg(text: raw, flags: codeBlock)]); continue }

            var lineFlag: UInt8 = 0
            var indent = String(raw.prefix(raw.count - t.count)).replacingOccurrences(of: "\t", with: "    ")
            if indent.count > 8 { indent = String(indent.prefix(8)) }
            var prefix = ""

            if t.hasPrefix("#") {
                t = String(t.drop(while: { $0 == "#" })).trimmingCharacters(in: .whitespaces)
                lineFlag = heading
                indent = ""
            } else if t == "---" || t == "***" || t == "___" {
                lines.append([])
                continue
            } else if t.hasPrefix(">") {
                t = String(t.drop(while: { $0 == ">" })).trimmingCharacters(in: .whitespaces)
                lineFlag = quote
                prefix = "│ "
            } else if t.hasPrefix("- ") || t.hasPrefix("* ") || t.hasPrefix("+ ") {
                t = String(t.dropFirst(2))
                if t.hasPrefix("[ ] ") { prefix = "☐ "; t = String(t.dropFirst(4)) }
                else if t.hasPrefix("[x] ") || t.hasPrefix("[X] ") { prefix = "☑ "; t = String(t.dropFirst(4)) }
                else { prefix = "• " }
            } else if tableSep.firstMatch(in: t, range: NSRange(location: 0, length: (t as NSString).length)) != nil {
                continue
            } else if t.hasPrefix("|") && t.trimmingCharacters(in: .whitespaces).hasSuffix("|") && t.count > 1 {
                let cells = t.trimmingCharacters(in: .whitespaces)
                    .trimmingCharacters(in: CharacterSet(charactersIn: "|"))
                    .components(separatedBy: "|")
                    .map { $0.trimmingCharacters(in: .whitespaces) }
                t = cells.joined(separator: "   ·   ")
            }

            var segs: [Seg] = []
            if !(indent + prefix).isEmpty { segs.append(Seg(text: indent + prefix, flags: lineFlag)) }
            let ns = t as NSString
            var pos = 0
            for m in inlineRx.matches(in: t, range: NSRange(location: 0, length: ns.length)) {
                if m.range.location > pos {
                    segs.append(Seg(text: ns.substring(with: NSRange(location: pos, length: m.range.location - pos)), flags: lineFlag))
                }
                let tok = ns.substring(with: m.range)
                if tok.hasPrefix("`") {
                    segs.append(Seg(text: String(tok.dropFirst().dropLast()), flags: lineFlag | code))
                } else {
                    segs.append(Seg(text: String(tok.dropFirst(2).dropLast(2)), flags: lineFlag | bold))
                }
                pos = m.range.location + m.range.length
            }
            if pos < ns.length { segs.append(Seg(text: ns.substring(from: pos), flags: lineFlag)) }
            lines.append(segs)
        }
        return lines
    }

    /// 纯文字版（算字数用）
    static func plain(_ md: String) -> String {
        flatten(md).map { $0.map(\.text).joined() }.joined(separator: "\n")
    }

    /// 比较时忽略空白和 Markdown 符号，划线原文从这边、Windows 或 Obsidian 来都能对上
    private static let skipSet: Set<UInt16> = Set("•*`#>│$☐☑".utf16)

    private static func skip(_ u: UInt16) -> Bool {
        if skipSet.contains(u) { return true }
        if let sc = Unicode.Scalar(u), CharacterSet.whitespacesAndNewlines.contains(sc) { return true }
        return false
    }

    static func condense(_ s: String) -> [UInt16] { s.utf16.filter { !skip($0) } }

    /// 选中的文字整理成划线原文：去掉列表圆点等
    static func cleanSelection(_ s: String) -> String {
        var out: [String] = []
        for l in s.replacingOccurrences(of: "\r", with: "").components(separatedBy: "\n") {
            var t = l.trimmingCharacters(in: .whitespaces)
            for p in ["• ", "│ ", "☐ ", "☑ "] where t.hasPrefix(p) { t = String(t.dropFirst(2)) }
            if !t.isEmpty { out.append(t) }
        }
        return out.joined(separator: "\n").trimmingCharacters(in: .whitespacesAndNewlines)
    }

    static func build(_ md: String, anchors: [Anchor], fontSize fs: CGFloat, error: Bool) -> NSAttributedString {
        let out = NSMutableAttributedString()
        let ink: NSColor = error ? Theme.danger : Theme.ink
        var first = true
        var gap = false
        for line in flatten(md) {
            let lineText = line.map(\.text).joined()
            if lineText.trimmingCharacters(in: .whitespaces).isEmpty { gap = true; continue }
            let lf = line.first?.flags ?? 0
            let ps = NSMutableParagraphStyle()
            ps.lineSpacing = 3
            ps.paragraphSpacingBefore = first ? 0 : (gap ? 8 : 2)
            if lf & heading != 0 && !first { ps.paragraphSpacingBefore = 10 }
            if !first { out.append(NSAttributedString(string: "\n", attributes: [.font: NSFont.systemFont(ofSize: fs), .paragraphStyle: ps])) }
            for seg in line {
                var font = NSFont.systemFont(ofSize: fs)
                var color = ink
                var attrs: [NSAttributedString.Key: Any] = [:]
                if seg.flags & heading != 0 { font = NSFont.systemFont(ofSize: fs + 1.5, weight: .bold) }
                if seg.flags & bold != 0 { font = NSFont.systemFont(ofSize: font.pointSize, weight: seg.flags & heading != 0 ? .bold : .semibold) }
                if seg.flags & (code | codeBlock) != 0 {
                    font = NSFont.monospacedSystemFont(ofSize: fs - 1.5, weight: .regular)
                    attrs[.backgroundColor] = Theme.surface2
                }
                if seg.flags & quote != 0 && !error { color = Theme.inkSoft }
                attrs[.font] = font
                attrs[.foregroundColor] = color
                attrs[.paragraphStyle] = ps
                out.append(NSAttributedString(string: seg.text, attributes: attrs))
            }
            first = false
            gap = false
        }

        // 高亮：在显示出来的文字里找划线原文（忽略空白和符号）
        if !anchors.isEmpty {
            let units = Array(out.string.utf16)
            var map: [Int] = []
            var cond: [UInt16] = []
            for (i, u) in units.enumerated() where !skip(u) { cond.append(u); map.append(i) }
            var marked = [Bool](repeating: false, count: units.count)
            for a in anchors {
                let key = condense(a.text)
                guard !key.isEmpty, key.count <= cond.count else { continue }
                guard let at = find(cond, key) else { continue }
                let start = map[at], end = map[at + key.count - 1] + 1
                if marked[start..<end].contains(true) { continue }
                for k in start..<end { marked[k] = true }
                let r = NSRange(location: start, length: end - start)
                out.addAttributes([
                    .link: "mindflow-node:" + a.childId,
                    .backgroundColor: Theme.mark,
                    .toolTip: a.tip,
                ], range: r)
            }
        }
        return out
    }

    private static func find(_ hay: [UInt16], _ needle: [UInt16]) -> Int? {
        let n = hay.count, m = needle.count
        if m == 0 || m > n { return nil }
        let f = needle[0]
        var i = 0
        while i <= n - m {
            if hay[i] == f {
                var j = 1
                while j < m && hay[i + j] == needle[j] { j += 1 }
                if j == m { return i }
            }
            i += 1
        }
        return nil
    }
}

/// 回答用的文本框：可以选中文字（松开鼠标时通知划线追问），高亮可以点
final class MindTextView: NSTextView {
    var onSelect: ((String, NSRect) -> Void)?

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func mouseDown(with event: NSEvent) {
        super.mouseDown(with: event) // NSTextView 在这里跟踪拖选，松开鼠标后才返回
        let r = selectedRange()
        guard r.length > 0, let lm = layoutManager, let tc = textContainer else { return }
        let text = (string as NSString).substring(with: r)
        let glyphs = lm.glyphRange(forCharacterRange: r, actualCharacterRange: nil)
        var rect = lm.boundingRect(forGlyphRange: glyphs, in: tc)
        rect.origin.x += textContainerOrigin.x
        rect.origin.y += textContainerOrigin.y
        onSelect?(text, rect)
    }
}

struct AnswerText: NSViewRepresentable {
    let content: String
    let anchors: [MindText.Anchor]
    let error: Bool
    let fontSize: CGFloat
    let onSelect: (String, NSView, NSRect) -> Void
    let onAnchor: (String) -> Void

    private var signature: String {
        content + "\u{1}" + anchors.map { $0.childId + ":" + $0.text }.joined(separator: "\u{2}") + (error ? "E" : "")
    }

    func makeCoordinator() -> Coordinator { Coordinator(self) }

    func makeNSView(context: Context) -> MindTextView {
        let tv = MindTextView(usingTextLayoutManager: false)
        tv.isEditable = false
        tv.isSelectable = true
        tv.isRichText = true
        tv.drawsBackground = false
        tv.textContainerInset = .zero
        tv.textContainer?.lineFragmentPadding = 0
        tv.textContainer?.widthTracksTextView = true
        tv.isVerticallyResizable = false
        tv.isHorizontallyResizable = false
        tv.linkTextAttributes = [.foregroundColor: Theme.ink, .cursor: NSCursor.pointingHand]
        tv.selectedTextAttributes = [.backgroundColor: Theme.accentSoft]
        tv.delegate = context.coordinator
        return tv
    }

    func updateNSView(_ tv: MindTextView, context: Context) {
        context.coordinator.parent = self
        let coord = context.coordinator
        tv.onSelect = { [weak tv, weak coord] s, r in
            guard let tv, let coord else { return }
            coord.parent.onSelect(s, tv, r)
        }
        let sig = signature
        if coord.sig != sig {
            coord.sig = sig
            tv.textStorage?.setAttributedString(MindText.build(content, anchors: anchors, fontSize: fontSize, error: error))
            tv.invalidateIntrinsicContentSize()
        }
    }

    func sizeThatFits(_ proposal: ProposedViewSize, nsView tv: MindTextView, context: Context) -> CGSize? {
        let w = max(proposal.width ?? 480, 40)
        guard let lm = tv.layoutManager, let tc = tv.textContainer else { return nil }
        tc.containerSize = NSSize(width: w, height: .greatestFiniteMagnitude)
        lm.ensureLayout(for: tc)
        let h = ceil(lm.usedRect(for: tc).height) + 2
        return CGSize(width: w, height: max(h, 18))
    }

    final class Coordinator: NSObject, NSTextViewDelegate {
        var parent: AnswerText
        var sig = ""

        init(_ p: AnswerText) { parent = p }

        func textView(_ textView: NSTextView, clickedOnLink link: Any, at charIndex: Int) -> Bool {
            let s = (link as? String) ?? (link as? URL)?.absoluteString ?? ""
            if s.hasPrefix("mindflow-node:") {
                parent.onAnchor(String(s.dropFirst("mindflow-node:".count)))
                return true
            }
            if let u = URL(string: s) { NSWorkspace.shared.open(u) }
            return true
        }
    }
}
