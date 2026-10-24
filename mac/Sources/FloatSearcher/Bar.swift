import AppKit
import Combine
import SwiftUI

/// 点悬浮图标后横向出现的输入窗。只隐藏不销毁，AI 对话会一直保留。
/// 第一次点就能直接点中按钮、输入框（不用先点一下让窗口获得焦点）
final class FirstMouseHostingView<Content: View>: NSHostingView<Content> {
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
}

@MainActor
final class BarController: NSObject, NSWindowDelegate {
    static let shared = BarController()
    static let width: CGFloat = 560

    let model: BarModel
    let panel: FloatPanel
    private let host: FirstMouseHostingView<AnyView>
    private var anchorTop: CGFloat = 0
    private var autoHiddenAt = Date.distantPast
    private var bag = Set<AnyCancellable>()

    private override init() {
        model = BarModel()
        panel = FloatPanel.make(NSRect(x: 0, y: 0, width: BarController.width, height: 80), canKey: true)
        panel.hasShadow = true
        let root = BarView(m: model, settings: AppSettings.shared)
            .frame(maxWidth: .infinity, minHeight: 0, maxHeight: .infinity, alignment: .top)
        host = FirstMouseHostingView(rootView: AnyView(root))
        host.sizingOptions = []
        panel.contentView = host
        super.init()
        panel.delegate = self
        panel.onCancel = { [weak self] in self?.hide() }
        model.onHide = { [weak self] in self?.hide() }
        // 内容变高变矮时，窗口跟着变，顶边不动
        model.$height
            .removeDuplicates()
            .receive(on: DispatchQueue.main) // 等 SwiftUI 这一轮布局结束再改窗口大小
            .sink { [weak self] h in self?.applyHeight(h) }
            .store(in: &bag)
    }

    var isVisible: Bool { panel.isVisible }

    /// 按下悬浮图标那一刻，输入窗是不是开着（包括刚因为点到别处自动收起）
    func wasOpen() -> Bool {
        model.mode == AppSettings.shared.mode &&
            (panel.isVisible || Date().timeIntervalSince(autoHiddenAt) < 0.6)
    }

    func toggle(wasOpen: Bool) {
        if wasOpen {
            if panel.isVisible { hide() }
            return
        }
        show(AppSettings.shared.mode)
    }

    /// 扇形里换了模式：输入窗开着（或刚被收起）就跟着切换
    func onModeChosen(_ m: Mode) {
        if panel.isVisible || Date().timeIntervalSince(autoHiddenAt) < 0.6 { show(m) }
    }

    func show(_ m: Mode) {
        model.prepare(for: m, wasVisible: panel.isVisible)
        reposition()
        // 面板不激活本程序：你正在用的 App 保持在前台，但可以直接在这里打字
        panel.makeKeyAndOrderFront(nil)
        model.afterShow()
        // 等界面画出来再给输入框焦点（第一次打开时也能直接打字）
        DispatchQueue.main.async { [weak self] in self?.model.focusTick += 1 }
    }

    func hide() {
        guard panel.isVisible else { return }
        model.willHide()
        panel.orderOut(nil)
    }

    func resetChat() { model.resetChat() }

    func windowDidResignKey(_ notification: Notification) {
        // 中译英是「查完就走」：点到别处自动收起（设置里可关）
        if panel.isVisible && model.mode == .translate && AppSettings.shared.trAutoHide {
            autoHiddenAt = Date()
            hide()
        }
    }

    /// 贴着悬浮图标横向展开：图标在右半屏就往左开，否则往右开；输入行和图标对齐
    func reposition() {
        let b = BubbleController.shared
        let c = b.center
        let vf = BubbleController.screen(containing: c).visibleFrame
        let w = BarController.width
        var x = c.x > vf.midX ? c.x - BubbleController.radius - 10 - w : c.x + BubbleController.radius + 10
        x = min(max(x, vf.minX + 4), vf.maxX - w - 4)
        anchorTop = min(c.y + 28, vf.maxY - 4)
        let h = max(model.height, 56)
        place(x: x, height: h, in: vf)
    }

    private func applyHeight(_ h: CGFloat) {
        guard h > 0, panel.isVisible || anchorTop > 0 else { return }
        let vf = BubbleController.screen(containing: BubbleController.shared.center).visibleFrame
        place(x: panel.frame.minX, height: h, in: vf)
    }

    private func place(x: CGFloat, height h: CGFloat, in vf: NSRect) {
        var top = anchorTop
        // 放不下时整体往上挪
        if top - h < vf.minY + 4 { top = vf.minY + 4 + h }
        panel.setFrame(NSRect(x: x, y: top - h, width: BarController.width, height: h), display: false)
        DispatchQueue.main.async { [weak self] in self?.panel.invalidateShadow() }
    }
}

// ================= 界面 =================

private struct HeightKey: PreferenceKey {
    static var defaultValue: CGFloat = 0
    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) { value = max(value, nextValue()) }
}

private struct ContentHeightKey: PreferenceKey {
    static var defaultValue: CGFloat = 0
    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) { value = max(value, nextValue()) }
}

struct BarView: View {
    @ObservedObject var m: BarModel
    @ObservedObject var settings: AppSettings
    @FocusState private var focused: Bool
    @State private var outputHeight: CGFloat = 0

    init(m: BarModel, settings: AppSettings) {
        _m = ObservedObject(wrappedValue: m)
        _settings = ObservedObject(wrappedValue: settings)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            topRow
            if m.mode == .clipboard, let img = m.clipImage {
                imageCard(img)
                chips
            } else if m.mode == .clipboard, let t = m.clipText {
                clipCard(t)
                chips
            }
            if m.hasOutput { output }
            if let st = m.status {
                Text(st)
                    .font(.system(size: 11.5))
                    .foregroundColor(m.statusError ? .tDanger : .tInkSoft)
                    .lineLimit(2)
                    .padding(.horizontal, 4)
            }
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 8)
        .frame(width: BarController.width, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: 14).fill(Color.tSurface))
        .overlay(RoundedRectangle(cornerRadius: 14).stroke(Color.tLine, lineWidth: 1))
        .fixedSize(horizontal: false, vertical: true)
        .background(GeometryReader { g in Color.clear.preference(key: HeightKey.self, value: g.size.height) })
        .onPreferenceChange(HeightKey.self) { h in m.height = h }
        .onChange(of: m.focusTick) { _ in focused = true }
        .onAppear { DispatchQueue.main.async { focused = true } }
        .onExitCommand { m.hide() }
    }

    // ---------- 第一行：模式 | 输入框 | 按钮 ----------

    private var topRow: some View {
        HStack(alignment: .top, spacing: 8) {
            Text(m.mode.title)
                .font(.system(size: 12.5, weight: .semibold))
                .foregroundColor(.tAccent)
                .padding(.horizontal, 10)
                .frame(height: 30)
                .background(RoundedRectangle(cornerRadius: 10).fill(Color.tAccentSoft))

            TextField(m.placeholder, text: $m.input, axis: .vertical)
                .textFieldStyle(.plain)
                .font(.system(size: 15.5))
                .foregroundColor(.tInk)
                .lineLimit(1...5)
                .focused($focused)
                .onSubmit { m.submit() }
                .padding(.vertical, 6)

            HStack(spacing: 2) {
                if m.mode != .translate && settings.webAvailable {
                    flatButton(settings.webSearch ? "联网 ✓" : "联网",
                               color: settings.webSearch ? .tAccent : .tInkSoft,
                               help: "DeepSeek 联网搜索：需要最新信息时自动搜索，回答附来源") { m.toggleWeb() }
                }
                if m.mode != .translate {
                    flatButton("新对话", color: .tInkSoft, help: "清空这段对话，重新开始") { m.newConversation() }
                }
                Button(action: { if m.busy { m.stopByUser() } else { m.submit() } }) {
                    Text(m.busy ? "■" : "↵")
                        .font(.system(size: 14, weight: .bold))
                        .foregroundColor(.tAccentInk)
                        .frame(width: 40, height: 30)
                        .background(RoundedRectangle(cornerRadius: 10).fill(Color.tAccent))
                }
                .buttonStyle(.plain)
                .help(m.busy ? "停止" : "发送（回车）")
                flatButton("✕", color: .tInkSoft, help: "收起（Esc）") { m.hide() }
            }
        }
    }

    private func flatButton(_ title: String, color: Color, help: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Text(title)
                .font(.system(size: 13))
                .foregroundColor(color)
                .padding(.horizontal, 8)
                .frame(height: 30)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help(help)
    }

    // ---------- 剪贴板 ----------

    private func clipCard(_ t: String) -> some View {
        VStack(alignment: .leading, spacing: 3) {
            Text("剪贴板 · \(t.count) 字")
                .font(.system(size: 11.5, weight: .semibold))
                .foregroundColor(.tAccent)
            Text(t)
                .font(.system(size: 13))
                .foregroundColor(.tInkSoft)
                .lineLimit(m.clipExpanded ? 14 : 3)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
        .background(RoundedRectangle(cornerRadius: 10).fill(Color.tQuote))
        .contentShape(Rectangle())
        .onTapGesture { m.clipExpanded.toggle() }
        .help("点一下展开 / 收起")
    }

    /// 剪贴板里的图片：缩略图，点一下放大 / 缩小
    private func imageCard(_ img: NSImage) -> some View {
        VStack(alignment: .leading, spacing: 5) {
            Text("剪贴板 · \(m.clipImageInfo)")
                .font(.system(size: 11.5, weight: .semibold))
                .foregroundColor(.tAccent)
            Image(nsImage: img)
                .resizable()
                .interpolation(.high)
                .aspectRatio(contentMode: .fit)
                .frame(maxHeight: m.clipExpanded ? 320 : 96, alignment: .leading)
                .clipShape(RoundedRectangle(cornerRadius: 6))
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: 10).fill(Color.tQuote))
        .contentShape(Rectangle())
        .onTapGesture { m.clipExpanded.toggle() }
        .help("点一下放大 / 缩小")
    }

    private var chips: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 6) {
                ForEach(Array(m.chipList.enumerated()), id: \.offset) { _, p in
                    Button(action: { m.sendClip(display: p.name, prompt: p.prompt, includeText: true) }) {
                        Text(p.name)
                            .font(.system(size: 12.5))
                            .foregroundColor(.tInk)
                            .padding(.horizontal, 12)
                            .frame(height: 26)
                            .background(Capsule().fill(Color.tSurface2))
                    }
                    .buttonStyle(.plain)
                    .help(p.prompt)
                }
            }
        }
    }

    // ---------- 输出 ----------

    private var output: some View {
        let maxH: CGFloat = m.mode == .translate ? 320 : 440
        return ScrollViewReader { proxy in
            ScrollView(.vertical) {
                VStack(alignment: .leading, spacing: 0) {
                    switch m.mode {
                    case .translate: translation
                    case .chat: conversation(m.chat)
                    case .clipboard: conversation(m.clip)
                    }
                    Color.clear.frame(height: 1).id("bottom")
                }
                .background(GeometryReader { g in Color.clear.preference(key: ContentHeightKey.self, value: g.size.height) })
            }
            .frame(height: min(max(outputHeight, 20), maxH))
            .onPreferenceChange(ContentHeightKey.self) { h in
                outputHeight = h
                if m.mode != .translate { proxy.scrollTo("bottom", anchor: .bottom) }
            }
        }
    }

    /// 第一行大字是主译文，后面每行是同义词 / 各种句式；点哪一行就复制哪一行
    private var translation: some View {
        VStack(alignment: .leading, spacing: 2) {
            ForEach(Array(m.trLines.enumerated()), id: \.offset) { i, line in
                TrRow(line: line, main: i == 0) { m.copy(line.text) }
            }
        }
    }

    private func conversation(_ msgs: [UIMsg]) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            ForEach(msgs) { msg in
                if msg.user {
                    HStack {
                        Spacer(minLength: 60)
                        Text(msg.text)
                            .font(.system(size: 13.5))
                            .foregroundColor(.tInk)
                            .lineLimit(6)
                            .textSelection(.enabled)
                            .padding(.horizontal, 12)
                            .padding(.vertical, 7)
                            .background(RoundedRectangle(cornerRadius: 12).fill(Color.tSurface2))
                    }
                } else {
                    VStack(alignment: .leading, spacing: 6) {
                        Text(BarView.markdown(msg.text))
                            .font(.system(size: 14.5))
                            .foregroundColor(msg.error ? .tDanger : (msg.waiting ? .tInkSoft : .tInk))
                            .lineSpacing(3)
                            .textSelection(.enabled)
                            .frame(maxWidth: .infinity, alignment: .leading)
                        if !msg.sources.isEmpty { sourcesView(msg.sources) }
                    }
                    .padding(.horizontal, 4)
                }
            }
        }
    }

    private func sourcesView(_ hits: [SearchHit]) -> some View {
        VStack(alignment: .leading, spacing: 3) {
            Text("来源").font(.system(size: 11.5, weight: .semibold)).foregroundColor(.tAccent)
            ForEach(hits) { h in
                Button(action: { if let u = URL(string: h.url) { NSWorkspace.shared.open(u) } }) {
                    HStack(alignment: .firstTextBaseline, spacing: 4) {
                        Text("[\(h.index)]").foregroundColor(.tInkSoft)
                        Text(h.title.isEmpty ? h.url : h.title).foregroundColor(.tAccent).lineLimit(1)
                        let meta = [h.site, String(h.date.prefix(10))].filter { !$0.isEmpty }.joined(separator: " · ")
                        if !meta.isEmpty { Text(meta).foregroundColor(.tInkSoft).lineLimit(1) }
                    }
                    .font(.system(size: 12.5))
                }
                .buttonStyle(.plain)
                .help(h.url)
            }
        }
    }

    /// 支持粗体、斜体、行内代码、链接；标题和分隔线去掉
    static func markdown(_ s: String) -> AttributedString {
        let cleaned = Prompts.cleanMarkdown(s)
        let opts = AttributedString.MarkdownParsingOptions(interpretedSyntax: .inlineOnlyPreservingWhitespace)
        return (try? AttributedString(markdown: cleaned, options: opts)) ?? AttributedString(cleaned)
    }
}

private struct TrRow: View {
    let line: Prompts.Line
    let main: Bool
    let onCopy: () -> Void
    @State private var hover = false

    init(line: Prompts.Line, main: Bool, onCopy: @escaping () -> Void) {
        self.line = line
        self.main = main
        self.onCopy = onCopy
    }

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 8) {
            if !line.label.isEmpty {
                Text(line.label)
                    .font(.system(size: 11.5))
                    .foregroundColor(.tInkSoft)
                    .padding(.horizontal, 7)
                    .padding(.vertical, 1)
                    .background(RoundedRectangle(cornerRadius: 6).fill(Color.tSurface2))
            }
            Text(line.text)
                .font(.system(size: main ? 19 : 14, weight: main ? .semibold : .regular))
                .foregroundColor(.tInk)
                .frame(maxWidth: .infinity, alignment: .leading)
                .fixedSize(horizontal: false, vertical: true)
        }
        .padding(.horizontal, 8)
        .padding(.vertical, main ? 6 : 5)
        .background(RoundedRectangle(cornerRadius: 8).fill(hover ? Color.tHover : Color.clear))
        .contentShape(Rectangle())
        .onHover { hover = $0 }
        .onTapGesture(perform: onCopy)
        .help("点击复制")
    }
}
