import AppKit
import Combine
import SwiftUI
import UniformTypeIdentifiers

/// 点悬浮图标后横向出现的输入窗。只隐藏不销毁。
/// 第一次点就能直接点中按钮、输入框（不用先点一下让窗口获得焦点）
final class FirstMouseHostingView<Content: View>: NSHostingView<Content> {
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
}

@MainActor
final class BarController: NSObject, NSWindowDelegate {
    static let shared = BarController()

    let model: BarModel
    let panel: FloatPanel
    private let host: FirstMouseHostingView<AnyView>
    private var anchorTop: CGFloat = 0
    private var autoHiddenAt = Date.distantPast
    private var bag = Set<AnyCancellable>()

    private override init() {
        model = BarModel()
        panel = FloatPanel.make(NSRect(x: 0, y: 0, width: 560, height: 80), canKey: true)
        panel.hasShadow = true
        let root = BarView(m: model, settings: AppSettings.shared)
            .frame(maxWidth: .infinity, minHeight: 0, maxHeight: .infinity, alignment: .top)
        host = FirstMouseHostingView(rootView: AnyView(root))
        host.sizingOptions = []
        panel.contentView = host
        super.init()
        panel.delegate = self
        // Esc：先关追问小窗 / 取消追问，再收起
        panel.onCancel = { [weak self] in
            guard let self else { return }
            if !self.model.escape() { self.hide() }
        }
        // ⌘V：剪贴板里是图片就当附件；其他编辑快捷键直接交给当前焦点（程序没激活时菜单不一定接得到）
        panel.onKeyEquivalent = { [weak self] e in
            guard let self else { return false }
            let flags = e.modifierFlags.intersection(.deviceIndependentFlagsMask)
            let key = e.charactersIgnoringModifiers?.lowercased() ?? ""
            if flags == .command && key == "v" && self.model.pasteImage() { return true }
            var sel: Selector?
            if flags == .command {
                switch key {
                case "c": sel = #selector(NSText.copy(_:))
                case "x": sel = #selector(NSText.cut(_:))
                case "v": sel = #selector(NSText.paste(_:))
                case "a": sel = #selector(NSText.selectAll(_:))
                case "z": sel = Selector(("undo:"))
                default: break
                }
            } else if flags == [.command, .shift] && key == "z" {
                sel = Selector(("redo:"))
            }
            if let sel { return NSApp.sendAction(sel, to: nil, from: self.panel) }
            return false
        }
        // 弹系统对话框：先激活程序、把输入窗层级降下来，结束后再把输入窗放回前台
        model.onModal = { [weak self] body in
            guard let self else { body(); return }
            if #available(macOS 14.0, *) { NSApp.activate() } else { NSApp.activate(ignoringOtherApps: true) }
            self.panel.level = .normal
            body()
            self.panel.level = .floating
            self.panel.makeKeyAndOrderFront(nil)
        }
        model.onResignAnswer = { [weak self] in
            guard let self else { return }
            if self.panel.firstResponder is MindTextView { self.panel.makeFirstResponder(nil) }
        }
        // 用户自己滚动对话：不再自动跟到最下面
        NSEvent.addLocalMonitorForEvents(matching: .scrollWheel) { [weak self] e in
            if let self, e.window === self.panel { self.model.stopFollow() }
            return e
        }
        model.onHide = { [weak self] in self?.hide() }
        // 换模式、开关侧栏时宽度会变：重新摆位置
        model.onLayout = { [weak self] in
            guard let self, self.panel.isVisible else { return }
            DispatchQueue.main.async { self.reposition() }
        }
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

    func shutdown() { model.shutdown() }

    func windowDidResignKey(_ notification: Notification) {
        // 中译英是「查完就走」：点到别处自动收起（设置里可关）
        if panel.isVisible && model.isLookup && AppSettings.shared.trAutoHide {
            autoHiddenAt = Date()
            hide()
        }
    }

    /// 贴着悬浮图标横向展开：图标在右半屏就往左开，否则往右开；输入行和图标对齐
    func reposition() {
        let b = BubbleController.shared
        let c = b.center
        let vf = BubbleController.screen(containing: c).visibleFrame
        let w = model.width
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
        panel.setFrame(NSRect(x: x, y: top - h, width: model.width, height: h), display: false)
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

    static let chatMax: CGFloat = 480
    static let answerFont: CGFloat = 14.5

    init(m: BarModel, settings: AppSettings) {
        _m = ObservedObject(wrappedValue: m)
        _settings = ObservedObject(wrappedValue: settings)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            topRow
            if m.mode == .chat {
                if !m.pending.isEmpty { tray }
                if let a = m.replyAnchor { replyCard(a) }
                if let c = m.crumb { crumbRow(c) }
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
        .frame(width: m.width, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: 14).fill(Color.tSurface))
        .overlay(RoundedRectangle(cornerRadius: 14).stroke(Color.tLine, lineWidth: 1))
        .fixedSize(horizontal: false, vertical: true)
        .background(GeometryReader { g in Color.clear.preference(key: HeightKey.self, value: g.size.height) })
        .onPreferenceChange(HeightKey.self) { h in m.height = h }
        .onChange(of: m.focusTick) { _ in
            // 回答文本框可能还拿着焦点：先让它交出来，再给输入框
            m.onResignAnswer?()
            focused = false
            DispatchQueue.main.async { focused = true }
        }
        .onAppear { DispatchQueue.main.async { focused = true } }
        .onExitCommand { if !m.escape() { m.hide() } }
        .onDrop(of: [.fileURL, .image], isTargeted: nil) { providers in m.drop(providers) }
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
                if m.mode == .chat {
                    flatButton("📎", color: .tInkSoft, help: "添加图片（也可以 ⌘V 粘贴截图，或把图片拖进来）") { m.pickImages() }
                    if settings.webAvailable {
                        flatButton(settings.webSearch ? "联网 ✓" : "联网",
                                   color: settings.webSearch ? .tAccent : .tInkSoft,
                                   help: "DeepSeek 联网搜索：需要最新信息时自动搜索，回答附来源") { m.toggleWeb() }
                    }
                    tabButton("问答树", on: m.sideOpen && m.sideTab == .tree, help: "这段对话的所有追问，点哪跳哪") { m.toggleSide(.tree) }
                    tabButton("历史", on: m.sideOpen && m.sideTab == .hist, help: "以前的对话") { m.toggleSide(.hist) }
                    flatButton("新对话", color: .tInkSoft, help: "开始一段新对话（这段会留在历史里）") { m.newConversation() }
                }
                Button(action: { m.sendButton() }) {
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

    private func tabButton(_ title: String, on: Bool, help: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Text(title)
                .font(.system(size: 13, weight: on ? .semibold : .regular))
                .foregroundColor(on ? .tAccent : .tInkSoft)
                .padding(.horizontal, 8)
                .frame(height: 30)
                .background(RoundedRectangle(cornerRadius: 8).fill(on ? Color.tAccentSoft : Color.clear))
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help(help)
    }

    // ---------- 待发送的图片 / 追问的原文 / 来路 ----------

    private var tray: some View {
        HStack(spacing: 8) {
            ForEach(m.pending) { p in
                Image(nsImage: p.thumb)
                    .resizable()
                    .interpolation(.high)
                    .aspectRatio(contentMode: .fit)
                    .frame(maxWidth: 140, maxHeight: 60)
                    .padding(3)
                    .background(RoundedRectangle(cornerRadius: 8).fill(Color.tSurface2))
                    .overlay(alignment: .topTrailing) {
                        Button(action: { m.removePending(p.id) }) {
                            Text("✕")
                                .font(.system(size: 10, weight: .bold))
                                .foregroundColor(.tInk)
                                .frame(width: 18, height: 18)
                                .background(Circle().fill(Color.tSurface))
                        }
                        .buttonStyle(.plain)
                        .padding(2)
                        .help("去掉这张")
                    }
            }
        }
    }

    private func replyCard(_ a: MindAnchor) -> some View {
        HStack(alignment: .top) {
            VStack(alignment: .leading, spacing: 3) {
                Text("追问划线的内容")
                    .font(.system(size: 11.5, weight: .semibold))
                    .foregroundColor(.tAccent)
                Text(a.text)
                    .font(.system(size: 13))
                    .foregroundColor(.tInkSoft)
                    .lineLimit(3)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            Button(action: { m.clearReply(); m.focusTick += 1 }) {
                Text("✕").font(.system(size: 12)).foregroundColor(.tInkSoft).frame(width: 22, height: 22)
            }
            .buttonStyle(.plain)
            .help("不追问了")
        }
        .padding(.leading, 12)
        .padding(.trailing, 6)
        .padding(.vertical, 7)
        .background(RoundedRectangle(cornerRadius: 10).fill(Color.tQuote))
    }

    private func crumbRow(_ c: String) -> some View {
        HStack(spacing: 6) {
            Button(action: { m.goUp() }) {
                Text("‹ 上一层")
                    .font(.system(size: 12, weight: .semibold))
                    .foregroundColor(.tAccent)
                    .padding(.horizontal, 7)
                    .frame(height: 24)
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .help("回到分出这条追问的地方")
            Text(c)
                .font(.system(size: 12))
                .foregroundColor(.tInkSoft)
                .lineLimit(1)
                .truncationMode(.tail)
                .help(c)
        }
    }

    // ---------- 输出 ----------

    @ViewBuilder
    private var output: some View {
        if m.isLookup {
            ScrollView(.vertical) {
                translation
                    .background(GeometryReader { g in Color.clear.preference(key: ContentHeightKey.self, value: g.size.height) })
            }
            .frame(height: min(max(outputHeight, 20), 320))
            .onPreferenceChange(ContentHeightKey.self) { h in outputHeight = h }
        } else {
            HStack(alignment: .top, spacing: 10) {
                if m.sideOpen {
                    side.frame(width: 250, height: BarView.chatMax)
                }
                chatScroll
                    .frame(height: m.sideOpen ? BarView.chatMax : min(max(outputHeight, 20), BarView.chatMax))
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

    private var chatScroll: some View {
        ScrollViewReader { proxy in
            ScrollView(.vertical) {
                VStack(alignment: .leading, spacing: 10) {
                    if let n = m.node, !n.messages.isEmpty {
                        if let a = n.anchor, !a.text.isEmpty { anchorQuote(a) }
                        ForEach(n.messages) { msg in
                            Group {
                                if msg.role == "user" { userBubble(msg) } else { answer(n, msg) }
                            }
                            .id(msg.id)
                        }
                    } else {
                        Text("问点什么，开始一段对话。\n\n在 AI 的回答里选中一段看不懂的文字，就能就这段追问：追问在单独的分支里展开，主回答不会被淹没；所有追问会排进左边的问答树。\n\n⌘V 可以粘贴截图。")
                            .font(.system(size: 13))
                            .foregroundColor(.tInkSoft)
                            .padding(6)
                    }
                    Color.clear.frame(height: 1).id("bottom")
                }
                .padding(.trailing, 6)
                .background(GeometryReader { g in Color.clear.preference(key: ContentHeightKey.self, value: g.size.height) })
            }
            .onPreferenceChange(ContentHeightKey.self) { h in
                outputHeight = h
                if m.follow && m.currentRunning { proxy.scrollTo("bottom", anchor: .bottom) }
            }
            .onChange(of: m.scrollReq.seq) { _ in
                let id = m.scrollReq.id
                DispatchQueue.main.async {
                    proxy.scrollTo(id, anchor: id == "bottom" ? .bottom : .top)
                }
            }
            .onAppear {
                // 从中译英切回来、第一次打开时：滚到该看的位置
                let id = m.scrollReq.id
                DispatchQueue.main.async {
                    proxy.scrollTo(id, anchor: id == "bottom" ? .bottom : .top)
                }
            }
        }
    }

    /// 分支最上面：划线的原文，点它回到原处
    private func anchorQuote(_ a: MindAnchor) -> some View {
        VStack(alignment: .leading, spacing: 3) {
            Text("划线原文 · 点击回到原处")
                .font(.system(size: 11.5, weight: .semibold))
                .foregroundColor(.tAccent)
            Text(a.text)
                .font(.system(size: 13))
                .foregroundColor(.tInkSoft)
                .lineLimit(4)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 7)
        .background(RoundedRectangle(cornerRadius: 10).fill(Color.tQuote))
        .overlay(alignment: .leading) {
            RoundedRectangle(cornerRadius: 2).fill(Color.tMarkStrong).frame(width: 3)
        }
        .contentShape(Rectangle())
        .onTapGesture { m.goUp() }
    }

    private func userBubble(_ msg: MindMsg) -> some View {
        HStack {
            Spacer(minLength: 60)
            VStack(alignment: .trailing, spacing: 5) {
                let imgs = msg.images
                if !imgs.isEmpty {
                    HStack(spacing: 6) {
                        ForEach(imgs, id: \.id) { a in thumbView(a) }
                    }
                }
                let files = (msg.attachments?.count ?? 0) - imgs.count
                if files > 0 {
                    Text("📎 \(files) 个文件").font(.system(size: 12)).foregroundColor(.tInkSoft)
                }
                Text(msg.content)
                    .font(.system(size: 13.5))
                    .foregroundColor(.tInk)
                    .textSelection(.enabled)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 7)
            .background(RoundedRectangle(cornerRadius: 12).fill(Color.tSurface2))
        }
    }

    @ViewBuilder
    private func thumbView(_ a: MindAttachment) -> some View {
        let t = m.thumb(a)
        if let img = t.0 {
            Image(nsImage: img)
                .resizable()
                .interpolation(.high)
                .aspectRatio(contentMode: .fit)
                .frame(maxWidth: 160, maxHeight: 80)
                .clipShape(RoundedRectangle(cornerRadius: 6))
                .onTapGesture { if let u = t.1 { NSWorkspace.shared.open(u) } }
                .help("点击打开原图")
        } else {
            Text("🖼 图片不见了").font(.system(size: 12)).foregroundColor(.tInkSoft)
        }
    }

    private func answer(_ n: MindNode, _ msg: MindMsg) -> some View {
        let running = m.isRunning(msg)
        return VStack(alignment: .leading, spacing: 4) {
            if running && msg.content.isEmpty {
                Text(m.runStatus(msg) ?? "思考中…")
                    .font(.system(size: 13.5))
                    .foregroundColor(.tInkSoft)
            } else {
                AnswerText(content: msg.content,
                           anchors: running ? [] : m.anchors(n, msg),
                           error: msg.content.hasPrefix("❌"),
                           fontSize: BarView.answerFont,
                           onSelect: { text, point in m.selected(text, in: n, msg: msg, at: point) },
                           onPress: { m.stopFollow() },
                           onAnchor: { id in Task { @MainActor in m.openNode(id) } })
            }
            if !running {
                if let src = msg.sources, !src.isEmpty { sourcesView(src) }
                HStack(spacing: 2) {
                    tinyButton("复制", help: "复制这条回答（Markdown）") { m.copy(msg.content) }
                    tinyButton("重新回答", help: "用前面的对话重新生成这条回答") { m.regenerate(n, msg) }
                    Text(meta(n, msg))
                        .font(.system(size: 11.5))
                        .foregroundColor(.tInkSoft)
                        .opacity(0.85)
                        .lineLimit(1)
                        .padding(.leading, 4)
                }
            }
        }
        .padding(.horizontal, 4)
    }

    private func meta(_ n: MindNode, _ msg: MindMsg) -> String {
        var parts = [Mind.fmtTime(msg.doneAt ?? msg.createdAt)]
        if let model = msg.model, !model.isEmpty { parts.append(model) }
        parts.append("\(MindText.plain(msg.content).count) 字")
        let kids = m.anchors(n, msg).count
        if kids > 0 { parts.append("\(kids) 条追问") }
        return parts.joined(separator: " · ")
    }

    private func tinyButton(_ title: String, help: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Text(title)
                .font(.system(size: 12))
                .foregroundColor(.tInkSoft)
                .padding(.horizontal, 6)
                .frame(height: 22)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help(help)
    }

    private func sourcesView(_ hits: [MindSource]) -> some View {
        VStack(alignment: .leading, spacing: 3) {
            Text("来源").font(.system(size: 11.5, weight: .semibold)).foregroundColor(.tAccent)
            ForEach(Array(hits.enumerated()), id: \.offset) { _, h in
                Button(action: { if let u = URL(string: h.url) { NSWorkspace.shared.open(u) } }) {
                    HStack(alignment: .firstTextBaseline, spacing: 4) {
                        Text("[\(h.index)]").foregroundColor(.tInkSoft)
                        Text(h.title.isEmpty ? h.url : h.title).foregroundColor(.tAccent).lineLimit(1)
                        let metaText = [h.site, String(h.date.prefix(10))].filter { !$0.isEmpty }.joined(separator: " · ")
                        if !metaText.isEmpty { Text(metaText).foregroundColor(.tInkSoft).lineLimit(1) }
                    }
                    .font(.system(size: 12.5))
                }
                .buttonStyle(.plain)
                .help(h.url)
            }
        }
    }

    // ---------- 侧栏：问答树 / 历史 ----------

    private var side: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 2) {
                tabButton("问答树", on: m.sideTab == .tree, help: "这段对话的所有追问") { m.sideTab = .tree }
                tabButton("历史", on: m.sideTab == .hist, help: "以前的对话") { m.sideTab = .hist }
                Spacer()
                tinyButton("✕", help: "收起侧栏") { m.toggleSide(nil) }
            }
            .padding([.horizontal, .top], 6)
            if m.sideTab == .hist {
                TextField("搜索历史…", text: $m.histQuery)
                    .textFieldStyle(.roundedBorder)
                    .font(.system(size: 12.5))
                    .padding(.horizontal, 8)
                historyList
            } else {
                treeList
            }
        }
        .background(RoundedRectangle(cornerRadius: 10).fill(Color.tBg))
    }

    private func sideHint(_ t: String) -> some View {
        Text(t)
            .font(.system(size: 11))
            .foregroundColor(.tInkSoft)
            .fixedSize(horizontal: false, vertical: true)
            .padding(.horizontal, 10)
            .padding(.bottom, 8)
    }

    private func sideEmpty(_ t: String) -> some View {
        Text(t)
            .font(.system(size: 12.5))
            .foregroundColor(.tInkSoft)
            .fixedSize(horizontal: false, vertical: true)
            .padding(8)
    }

    private var treeList: some View {
        let items = m.treeItems
        let path = m.pathIds
        return VStack(alignment: .leading, spacing: 0) {
            ScrollView(.vertical) {
                LazyVStack(alignment: .leading, spacing: 1) {
                    if items.isEmpty || (m.session?.root?.messages.isEmpty ?? true) {
                        sideEmpty("还没有对话。问第一个问题后，这里会列出每一问，以及每一次划线追问分出来的分支。")
                    } else {
                        ForEach(items) { it in treeRow(it, current: it.node.id == m.nodeId, onPath: path.contains(it.node.id)) }
                    }
                }
                .padding(.horizontal, 4)
            }
            sideHint("在回答里选中一段文字就能追问 · 黄色高亮 = 已追问过的句子 · 右键分支可删除")
        }
    }

    private func treeRow(_ it: MindTreeItem, current: Bool, onPath: Bool) -> some View {
        let q = it.user?.content ?? it.node.title ?? "未命名问题"
        var text = AttributedString()
        if it.firstOfNode, let a = it.node.anchor, !a.text.isEmpty {
            var mark = AttributedString(Mind.truncate(a.text, 12))
            mark.swiftUI.backgroundColor = Color.tMark
            text += mark
            text += AttributedString(" ")
        }
        var qa = AttributedString(Mind.truncate(q, 60))
        if current { qa.swiftUI.font = .system(size: 12.5, weight: .semibold) }
        text += qa
        let isLastRunning = m.nodeRunning(it.node) && it.user?.id == it.node.messages.last(where: { $0.role == "user" })?.id
        if isLastRunning {
            var dots = AttributedString("  …")
            dots.swiftUI.foregroundColor = Color.tAccent
            text += dots
        }
        let anchorTip = it.firstOfNode && it.node.anchor != nil ? "划线「\(Mind.truncate(it.node.anchor?.text, 80))」\n" : ""
        return SideRow(current: current, indent: CGFloat(it.depth) * 14, help: anchorTip + Mind.truncate(q, 200)) {
            HStack(alignment: .firstTextBaseline, spacing: 6) {
                Text(it.label)
                    .font(.system(size: 11.5, weight: .semibold))
                    .foregroundColor(current || onPath ? .tAccent : .tInkSoft)
                    .frame(minWidth: 18, alignment: .leading)
                Text(text)
                    .font(.system(size: 12.5))
                    .foregroundColor(.tInk)
                    .lineLimit(2)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
        } onTap: {
            m.openNode(it.node.id, focus: it.user?.id)
        }
        .contextMenu {
            if it.firstOfNode && it.node.parentId != nil {
                Button("删除这条追问（连同下面的分支）") { m.deleteNode(it.node) }
            }
        }
    }

    private var historyList: some View {
        let h = m.history()
        return VStack(alignment: .leading, spacing: 0) {
            ScrollView(.vertical) {
                LazyVStack(alignment: .leading, spacing: 1) {
                    if h.shown.isEmpty {
                        sideEmpty(h.all == 0 ? "还没有历史对话。" : "没有找到包含「\(m.histQuery)」的对话。")
                    } else {
                        ForEach(h.shown) { ss in historyRow(ss) }
                    }
                }
                .padding(.horizontal, 4)
            }
            sideHint("共 \(h.all) 段对话 · 右键可删除 · 保存位置在设置里改")
        }
    }

    private func historyRow(_ ss: MindSession) -> some View {
        let current = ss === m.session
        var metaText = Mind.fmtTime(ss.updatedAt) + " · \(Mind.questionCount(ss)) 问"
        if ss.nodes.count > 1 { metaText += " · \(ss.nodes.count - 1) 条追问" }
        if m.sessionRunning(ss) { metaText += " · 回答中…" }
        return SideRow(current: current, indent: 0, help: ss.title) {
            VStack(alignment: .leading, spacing: 1) {
                Text(ss.title.isEmpty ? "未命名对话" : ss.title)
                    .font(.system(size: 12.5, weight: current ? .semibold : .regular))
                    .foregroundColor(.tInk)
                    .lineLimit(1)
                Text(metaText)
                    .font(.system(size: 11))
                    .foregroundColor(.tInkSoft)
                    .lineLimit(1)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        } onTap: {
            m.openSession(ss)
        }
        .contextMenu {
            Button("删除这段对话") { m.deleteSession(ss) }
        }
    }
}

/// 侧栏里的一行：当前项高亮，鼠标移上去变色
private struct SideRow<Content: View>: View {
    let current: Bool
    let indent: CGFloat
    let help: String
    let content: () -> Content
    let onTap: () -> Void
    @State private var hover = false

    init(current: Bool, indent: CGFloat, help: String,
         @ViewBuilder content: @escaping () -> Content, onTap: @escaping () -> Void) {
        self.current = current
        self.indent = indent
        self.help = help
        self.content = content
        self.onTap = onTap
    }

    var body: some View {
        content()
            .padding(.leading, 6 + indent)
            .padding(.trailing, 6)
            .padding(.vertical, 5)
            .background(RoundedRectangle(cornerRadius: 7)
                .fill(current ? Color.tAccentSoft : (hover ? Color.tHover : Color.clear)))
            .contentShape(Rectangle())
            .onHover { hover = $0 }
            .onTapGesture(perform: onTap)
            .help(help)
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
