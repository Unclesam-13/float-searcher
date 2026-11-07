import AppKit
import Combine
import SwiftUI
import UniformTypeIdentifiers

/// 在异步任务里累积文字用的小盒子（任务闭包里不能直接改外面的 var）
final class Box<T> {
    var value: T
    init(_ v: T) { value = v }
}

/// 待发送的图片
struct PendingImage: Identifiable {
    let id = UUID()
    let thumb: NSImage
    let jpeg: Data
}

enum SideTab { case tree, hist }

/// 正在生成的一条回答
@MainActor
final class Running {
    var task: Task<Void, Never>?
    let s: MindSession
    let node: MindNode
    let msg: MindMsg
    var status: String?
    var lastSave = Date()

    init(s: MindSession, node: MindNode, msg: MindMsg) { self.s = s; self.node = node; self.msg = msg }
}

/// 输入窗的全部状态和逻辑。两种模式：
///  - 中译英：回车直接出译文
///  - AI 对话 = Canvas Mindflow 的追问方式：回答里划线追问（分支单独展开、原文高亮）、问答树、历史；
///    可以粘贴图片。对话按插件的 JSON 格式保存，可以和 Obsidian、Windows 版共用。
@MainActor
final class BarModel: ObservableObject {
    @Published var mode: Mode = .translate { didSet { if mode != oldValue { onLayout?() } } }
    @Published var input = ""
    @Published var trLines: [Prompts.Line] = []
    @Published var status: String?
    @Published var statusError = false
    @Published private(set) var trBusy = false
    @Published var height: CGFloat = 0
    @Published var focusTick = 0

    // ---- AI 对话（追问树） ----
    @Published private(set) var session: MindSession?
    @Published private(set) var nodeId: String?
    @Published private(set) var replyAnchor: MindAnchor?
    @Published var pending: [PendingImage] = []
    @Published var sideOpen = false { didSet { if sideOpen != oldValue { onLayout?() } } }
    @Published var sideTab: SideTab = .tree { didSet { if sideTab == .hist { refreshHistory() } } }
    @Published var histQuery = ""
    /// 流式输出时刷新界面用（攒一下再刷，不是每个字都重排）
    @Published private(set) var tick = 0
    /// 要滚到哪条消息（"bottom" = 最下面）；seq 变了才滚
    @Published private(set) var scrollReq: (id: String, seq: Int) = ("bottom", 0)
    /// 有回答在写时自动跟到最下面
    private(set) var follow = true

    var onHide: (() -> Void)?
    var onLayout: (() -> Void)?
    /// 弹出系统对话框（提示、选文件）前后：激活程序、暂时降低输入窗层级，结束后把输入窗变回前台
    var onModal: ((() -> Void) -> Void)?
    /// 让回答文本框交出焦点（输入框才能重新拿到焦点）
    var onResignAnswer: (() -> Void)?
    /// 历史列表（缓存，不在每次刷新界面时读磁盘）
    @Published private(set) var historyAll: [MindSession] = []

    private let store = MindStore.shared
    private var running: [String: Running] = [:]
    private var loaded = false
    private var refreshQueued = false
    private var popup: FloatPanel?
    private var popupMonitors: [Any] = []
    private var selAnchor: MindAnchor?
    private var imgCache: [String: String] = [:]
    private var thumbCache: [String: NSImage] = [:]
    private var bag = Set<AnyCancellable>()

    // ---- 中译英 ----
    private var trTask: Task<Void, Never>?
    private var trGen = 0
    private var trCache: [String: String] = [:]

    var s: AppSettings { AppSettings.shared }

    static let maxImages = 4

    init() {
        store.onError = { [weak self] msg in self?.setStatus(msg, error: true) }
        store.inUse = { [weak self] id in
            guard let self else { return false }
            return self.session?.id == id || self.running.values.contains { $0.s.id == id }
        }
        // 设置里换了保存位置：停止打字一会儿后，从新位置重新载入
        store.useFolderSetting(AppSettings.shared.mindFolder)
        AppSettings.shared.$mindFolder
            .dropFirst()
            .removeDuplicates()
            .debounce(for: .milliseconds(800), scheduler: DispatchQueue.main)
            .sink { [weak self] v in
                self?.store.useFolderSetting(v)
                self?.folderChanged()
            }
            .store(in: &bag)
    }

    /// 输入窗宽度：中译英窄一些；AI 对话宽一些，打开侧栏再加宽
    var width: CGFloat { mode == .chat ? (sideOpen ? 900 : 640) : 560 }

    var node: MindNode? { nodeId.flatMap { session?.nodes[$0] } }

    var currentRunning: Bool {
        guard let n = node, let ss = session else { return false }
        return running.values.contains { $0.s === ss && $0.node === n }
    }

    /// 发送按钮显示成「停止」
    var busy: Bool { mode == .chat ? currentRunning : trBusy }

    func isRunning(_ m: MindMsg) -> Bool { running[m.id] != nil }
    func runStatus(_ m: MindMsg) -> String? { running[m.id]?.status }
    func nodeRunning(_ n: MindNode) -> Bool { running.values.contains { $0.node === n } }
    func sessionRunning(_ ss: MindSession) -> Bool { running.values.contains { $0.s === ss } }

    private func isCurrent(_ ss: MindSession, _ n: MindNode) -> Bool {
        mode == .chat && ss === session && n.id == nodeId
    }

    // ================= 打开 / 收起 =================

    func prepare(for m: Mode, wasVisible: Bool) {
        let changed = m != mode || !wasVisible
        if trBusy && m != mode { stopTranslate() }
        mode = m
        setStatus(nil)
        if m == .translate && changed {
            input = ""
            trLines = []
        }
        if m == .chat && !loaded {
            loaded = true
            if session == nil, let latest = store.list().first {
                session = latest
                nodeId = latest.rootId
                requestScroll("bottom")
            }
        }
        if m == .chat && sideOpen && sideTab == .hist { refreshHistory() }
        closePopover()
    }

    func afterShow() {}

    func willHide() {
        if trBusy { stopTranslate() }
        closePopover()
    }

    func hide() { onHide?() }

    /// Esc：先关追问小窗、取消追问，再收起窗口。返回 true 表示已经处理了
    func escape() -> Bool {
        if popup != nil { closePopover(); focusTick += 1; return true }
        if mode == .chat && replyAnchor != nil { clearReply(); return true }
        return false
    }

    var placeholder: String {
        if mode == .translate { return "输入中文，回车翻译成英文" }
        if replyAnchor != nil { return "就划线的内容追问，回车发送（直接回车 = \(Mind.truncate(s.mindDefaultFollowUp, 12))）" }
        if !pending.isEmpty { return "要问这张图什么？回车发送（直接回车 = 分析图片）" }
        guard let n = node else { return "问点什么，回车发送 · ⌘V 粘贴截图" }
        if n.anchor != nil { return "在这条追问里接着问，回车发送" }
        return "接着问，回车发送 · 选中回答里的文字可以追问"
    }

    var hasOutput: Bool {
        mode == .translate ? !trLines.isEmpty : (sideOpen || !(node?.messages.isEmpty ?? true))
    }

    func setStatus(_ t: String?, error: Bool = false) {
        if status == t && statusError == error { return } // 不重复发通知（流式输出时每个字都会调到这里）
        status = t
        statusError = error
    }

    // ================= 提交 =================

    func submit() {
        let text = input.trimmingCharacters(in: .whitespacesAndNewlines)
        if mode == .translate {
            if !text.isEmpty { translate(text) }
        } else {
            mindSubmit(text)
        }
    }

    func sendButton() {
        if busy { stopByUser() } else { submit() }
    }

    func stopByUser() {
        if mode == .chat { stopCurrent() } else { stopTranslate() }
        setStatus("已停止")
    }

    func toggleWeb() {
        s.webSearch.toggle()
        setStatus(s.webSearch ? "已开启联网：需要时会先搜索，回答附来源" : "已关闭联网")
    }

    /// 右键菜单「开始新的 AI 对话」
    func resetChat() { newConversation() }

    func copy(_ text: String) {
        guard !text.isEmpty else { return }
        let pb = NSPasteboard.general
        pb.clearContents()
        pb.setString(text, forType: .string)
        let one = text.replacingOccurrences(of: "\n", with: " ")
        setStatus("已复制：" + (one.count > 40 ? String(one.prefix(40)) + "…" : one))
    }

    // ================= 中译英 =================

    private func stopTranslate() {
        trGen += 1
        trTask?.cancel()
        trTask = nil
        trBusy = false
    }

    func translate(_ text: String) {
        let prompt = Prompts.translate(s)
        let key = prompt + "\u{1}" + text
        stopTranslate()
        let myGen = trGen
        if let cached = trCache[key] {
            trLines = Prompts.split(cached)
            doneTranslating()
            return
        }
        trBusy = true
        setStatus("翻译中…")
        trLines = []
        let acc = Box("")
        trTask = Task { [weak self] in
            guard let self else { return }
            do {
                let full = try await AI.stream(AIConfig.current(), system: prompt,
                                               history: [ChatMsg(role: "user", content: text)],
                                               noThinking: true) { piece in
                    guard self.trGen == myGen else { return }
                    acc.value += piece
                    self.trLines = Prompts.split(acc.value)
                }
                guard self.trGen == myGen else { return }
                if full.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                    if self.mode == .translate { self.setStatus("没有拿到译文，再按一次回车试试", error: true) }
                } else {
                    self.trCache[key] = full
                    self.trLines = Prompts.split(full)
                    if self.mode == .translate { self.doneTranslating() }
                }
            } catch is CancellationError {
                if self.trGen == myGen && self.mode == .translate { self.setStatus(nil) }
            } catch {
                if self.trGen == myGen && self.mode == .translate { self.setStatus(error.localizedDescription, error: true) }
            }
            if self.trGen == myGen {
                self.trBusy = false
                self.trTask = nil
            }
        }
    }

    private func doneTranslating() {
        setStatus(trLines.count > 1 ? "点任意一行复制 · 接着打字查下一个" : "点译文复制 · 接着打字查下一个")
        // 选中原文：接着打字就会替换，查下一个
        Task { @MainActor in
            _ = NSApp.sendAction(#selector(NSText.selectAll(_:)), to: nil, from: nil)
        }
    }

    // ================= AI 对话：提问 =================

    /// 划线后直接回车时问的问题（设置清空了就用默认的）
    private var defaultFollowUp: String {
        let t = s.mindDefaultFollowUp.trimmingCharacters(in: .whitespacesAndNewlines)
        return t.isEmpty ? AppSettings.defaultMindFollowUp : t
    }

    /// 用户自己滚动或在回答里拖选：不再自动跟到最下面
    func stopFollow() { follow = false }

    func refreshHistory() { historyAll = store.list() }

    private func refreshSoon() {
        if refreshQueued { return }
        refreshQueued = true
        Task { @MainActor [weak self] in
            try? await Task.sleep(nanoseconds: 90_000_000)
            guard let self else { return }
            self.refreshQueued = false
            self.tick += 1
        }
    }

    private func refreshNow() { tick += 1 }

    private func requestScroll(_ id: String) {
        scrollReq = (id, scrollReq.seq + 1)
    }

    private func mindSubmit(_ text: String) {
        if replyAnchor == nil && text.isEmpty && pending.isEmpty { return }
        if replyAnchor == nil && currentRunning {
            setStatus("上一个回答还在写，写完再接着问（点 ■ 可以停止）。想追问上面的内容，选中那段文字就行。")
            return
        }
        input = ""
        let images = takePending()

        if let a = replyAnchor, session != nil, node != nil {
            clearReply()
            branch(a, question: text, images: images)
            return
        }
        clearReply()
        if text.isEmpty && images == nil { return }

        if session == nil {
            let ns = Mind.newSession(text.isEmpty ? "图片提问" : text)
            store.add(ns)
            session = ns
            nodeId = ns.rootId
            refreshHistory()
        }
        guard let ss = session else { return }
        if node == nil { nodeId = ss.rootId }
        guard let n = node else { return }
        ask(ss, n, text, images)
    }

    /// 从选中的一段话分出一条追问，并切到它
    private func branch(_ anchor: MindAnchor, question: String, images: [MindAttachment]?) {
        guard let ss = session, let parent = node else { return }
        let q = question.trimmingCharacters(in: .whitespacesAndNewlines)
        let child = MindNode()
        child.parentId = parent.id
        child.anchor = anchor
        child.title = q.isEmpty ? "「\(Mind.truncate(anchor.text, 18))」是什么意思？" : Mind.truncate(q, 40)
        ss.nodes[child.id] = child
        nodeId = child.id
        ask(ss, child, q.isEmpty ? defaultFollowUp : q, images)
        focusTick += 1
    }

    private func ask(_ ss: MindSession, _ n: MindNode, _ question: String, _ images: [MindAttachment]?) {
        var q = question
        if q.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            q = images != nil ? "请分析这张图片。" : defaultFollowUp
        }
        let um = MindMsg(role: "user", content: q)
        um.attachments = images
        n.messages.append(um)
        if (n.title ?? "").isEmpty { n.title = Mind.truncate(q, 40) }
        if ss.title.isEmpty && n.id == ss.rootId { ss.title = n.title ?? "" }
        let am = MindMsg(role: "assistant", content: "")
        am.createdAt = um.createdAt + 1
        am.pending = true
        n.messages.append(am)
        store.save(ss)

        let msgs = Mind.buildThread(ss, n.id, loadImage: { [weak self] a in self?.loadImageB64(a) })
        if isCurrent(ss, n) {
            follow = true
            requestScroll("bottom")
        }
        run(ss, n, am, msgs)
    }

    /// 流式生成一条回答。可以同时有好几条在生成（比如主回答还在写，就去追问上面的一句）
    private func run(_ ss: MindSession, _ n: MindNode, _ am: MindMsg, _ msgs: [ChatMsg]) {
        let r = Running(s: ss, node: n, msg: am)
        running[am.id] = r
        am.pending = true
        refreshNow()
        if isCurrent(ss, n) { setStatus("思考中…") }

        let acc = Box("")
        let sources = Box<[SearchHit]>([])
        let web: WebHooks? = (s.webAvailable && s.webSearch) ? WebHooks(
            onSearch: { [weak self, weak r] q in
                guard let self else { return }
                r?.status = "正在搜索：\(q) …"
                self.refreshSoon()
                if self.isCurrent(ss, n) { self.setStatus("正在联网搜索：\(q)") }
            },
            onReset: { [weak self] in
                acc.value = ""
                am.content = ""
                self?.refreshSoon()
            },
            onSources: { sources.value = $0 }
        ) : nil
        var model = s.effectiveModel
        if msgs.contains(where: { !$0.images.isEmpty }) && s.provider == .deepseek { model = "deepseek-flash" }
        am.model = model
        let system = s.chatSystem

        r.task = Task { [weak self] in
            guard let self else { return }
            do {
                let full = try await AI.stream(AIConfig.current(), system: system, history: msgs, web: web,
                                               onThinking: { [weak self, weak r] count in
                                                   guard let self, acc.value.isEmpty else { return }
                                                   r?.status = "深度思考中…（已想 \(count) 字）"
                                                   self.refreshSoon()
                                               }) { [weak self, weak r] piece in
                    guard let self else { return }
                    acc.value += piece
                    am.content = acc.value
                    r?.status = nil
                    self.refreshSoon()
                    // 每隔几秒存一次写到一半的回答（程序意外退出也不会全丢）
                    if let r, Date().timeIntervalSince(r.lastSave) > 4 {
                        r.lastSave = Date()
                        self.store.save(ss)
                    }
                    if self.isCurrent(ss, n) { self.setStatus("正在回答…") }
                }
                am.content = full.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? "（AI 返回为空）" : full
                if !sources.value.isEmpty { am.sources = sources.value.map { MindSource($0) } }
                if self.isCurrent(ss, n) { self.setStatus(nil) }
            } catch is CancellationError {
                am.content = acc.value + (acc.value.isEmpty ? "" : "\n\n") + "*（已停止生成）*"
            } catch {
                let msg = error.localizedDescription
                am.content = (acc.value.isEmpty ? "" : acc.value + "\n\n") + "❌ 请求失败：" + msg
                if self.isCurrent(ss, n) { self.setStatus(msg, error: true) }
            }
            self.finish(ss, n, am)
        }
    }

    private func finish(_ ss: MindSession, _ n: MindNode, _ am: MindMsg) {
        running.removeValue(forKey: am.id)
        am.pending = false
        am.doneAt = Mind.now()
        if ss.nodes[n.id] != nil { store.save(ss, immediate: true) }
        if sideOpen && sideTab == .hist { refreshHistory() }
        refreshNow()
    }

    private func stopCurrent() {
        guard let n = node, let ss = session else { return }
        for r in running.values where r.s === ss && r.node === n { r.task?.cancel() }
    }

    /// 退出程序时：还在写的回答把已写出的部分存下来
    func shutdown() {
        for r in running.values {
            r.task?.cancel()
            r.msg.pending = false
            r.msg.content += (r.msg.content.isEmpty ? "" : "\n\n") + "*（已停止生成）*"
            r.msg.doneAt = Mind.now()
            store.save(r.s, immediate: true)
        }
        running.removeAll()
        store.flush()
    }

    /// 重新回答：用这条回答之前的对话重新生成
    func regenerate(_ n: MindNode, _ m: MindMsg) {
        guard let ss = session, running[m.id] == nil else { return }
        if !anchors(n, m).isEmpty &&
            !confirm("重新回答？", "这条回答下面有划线追问。重新回答后，原来划线的句子可能对不上，高亮会消失（追问本身还在问答树里）。", ok: "重新回答") {
            return
        }
        let msgs = Mind.buildThread(ss, n.id, loadImage: { [weak self] a in self?.loadImageB64(a) }, upto: m.id)
        m.content = ""
        m.sources = nil
        m.extra.removeValue(forKey: "editedAt")
        m.extra.removeValue(forKey: "originalContent")
        m.createdAt = Mind.now()
        m.pending = true
        follow = true
        run(ss, n, m, msgs)
    }

    // ================= 划线追问 =================

    /// 回答里选中了一段：弹出追问小窗
    /// 回答里选中了一段：在鼠标松开的地方弹出追问小窗（不抢焦点的小面板，第一次点就能点中）
    func selected(_ text: String, in n: MindNode, msg m: MindMsg, at screenPoint: NSPoint) {
        follow = false
        guard running[m.id] == nil, n.id == nodeId else { return }
        let t = MindText.cleanSelection(text)
        guard !t.isEmpty else { return }
        selAnchor = MindAnchor(text: t, msgId: m.id)
        closePopover()

        let host = FirstMouseHostingView(rootView: AnyView(SelPopView(m: self, presets: s.mindPresetList)))
        let size = host.fittingSize
        let p = FloatPanel.make(NSRect(origin: .zero, size: size), canKey: false)
        p.hasShadow = true
        p.level = NSWindow.Level(rawValue: NSWindow.Level.floating.rawValue + 1)
        p.contentView = host
        // 放在鼠标右下方，放不下就挪进屏幕
        let vf = (NSScreen.screens.first { $0.frame.contains(screenPoint) } ?? NSScreen.main)?.visibleFrame
            ?? NSRect(x: 0, y: 0, width: 1440, height: 900)
        var x = screenPoint.x - 12
        var y = screenPoint.y - 14 - size.height
        x = min(max(x, vf.minX + 4), vf.maxX - size.width - 4)
        if y < vf.minY + 4 { y = screenPoint.y + 14 }
        y = min(y, vf.maxY - size.height - 4)
        p.setFrameOrigin(NSPoint(x: x, y: y))
        p.orderFrontRegardless()
        popup = p

        // 点到别处（本程序的其他地方或别的 App）就关掉
        if let local = NSEvent.addLocalMonitorForEvents(matching: [.leftMouseDown, .rightMouseDown], handler: { [weak self] e in
            if let self, e.window !== self.popup { self.closePopover() }
            return e
        }) { popupMonitors.append(local) }
        if let global = NSEvent.addGlobalMonitorForEvents(matching: [.leftMouseDown, .rightMouseDown], handler: { [weak self] _ in
            Task { @MainActor in self?.closePopover() }
        }) { popupMonitors.append(global) }
    }

    func closePopover() {
        for mon in popupMonitors { NSEvent.removeMonitor(mon) }
        popupMonitors.removeAll()
        popup?.orderOut(nil)
        popup = nil
    }

    /// 「追问…」：划线原文挂在输入框上方，在输入框里写问题
    func startReply() {
        closePopover()
        guard let a = selAnchor else { return }
        replyAnchor = a
        focusTick += 1
    }

    /// 点预设问题：直接追问
    func askPreset(_ q: String) {
        closePopover()
        guard let a = selAnchor else { return }
        clearReply()
        branch(a, question: q, images: takePending())
    }

    func copySelection() {
        closePopover()
        if let a = selAnchor { copy(a.text) }
        focusTick += 1
    }

    func clearReply() { replyAnchor = nil }

    /// 这条回答下面挂着的追问（用来画高亮）
    func anchors(_ n: MindNode, _ m: MindMsg) -> [MindText.Anchor] {
        guard let ss = session else { return [] }
        return Mind.children(ss, n.id).compactMap { c in
            guard let a = c.anchor, a.msgId == m.id, !a.text.isEmpty else { return nil }
            return MindText.Anchor(text: a.text, childId: c.id, tip: "已追问：\(c.title ?? "")\n点击打开")
        }
    }

    // ================= 导航 =================

    func openNode(_ id: String, focus msgId: String? = nil) {
        guard let ss = session, ss.nodes[id] != nil else { return }
        nodeId = id
        clearReply()
        closePopover()
        follow = msgId == nil
        requestScroll(msgId ?? "bottom")
        setStatus(currentRunning ? "正在回答…" : nil)
        focusTick += 1
    }

    func openSession(_ ss: MindSession) {
        session = ss
        nodeId = ss.rootId
        clearReply()
        closePopover()
        follow = true
        requestScroll("bottom")
        setStatus(nil)
        focusTick += 1
    }

    /// 回到分出这条追问的地方，并滚到那条回答
    func goUp() {
        guard let n = node, let p = n.parentId else { return }
        openNode(p, focus: n.anchor?.msgId)
    }

    /// 开始一段新对话（旧的留在历史里）
    func newConversation() {
        session = nil
        nodeId = nil
        loaded = true
        clearReply()
        closePopover()
        setStatus(nil)
        focusTick += 1
    }

    private func folderChanged() {
        session = nil
        nodeId = nil
        loaded = false
        clearReply()
        if mode == .chat {
            loaded = true
            if let latest = store.list().first { session = latest; nodeId = latest.rootId }
        }
        refreshHistory()
        refreshNow()
    }

    /// 来路：主线 › 1.1 xxx › 1.1.1 xxx
    var crumb: String? {
        guard let n = node, n.parentId != nil, let ss = session else { return nil }
        let tree = Mind.tree(ss)
        var parts = ["主线"]
        for x in Mind.chain(ss, n.id).dropFirst() {
            let label = tree.first(where: { $0.node === x && $0.firstOfNode })?.label ?? ""
            parts.append("\(label) \(Mind.truncate(x.title, 16))")
        }
        return parts.joined(separator: "  ›  ")
    }

    // ================= 侧栏 =================

    func toggleSide(_ tab: SideTab?) {
        if let tab, !(sideOpen && sideTab == tab) {
            sideTab = tab
            sideOpen = true
            if tab == .hist { refreshHistory() }
        } else {
            sideOpen = false
        }
        focusTick += 1
    }

    var treeItems: [MindTreeItem] { Mind.tree(session) }

    var pathIds: Set<String> { Set(Mind.chain(session, nodeId).map(\.id)) }

    func history() -> (all: Int, shown: [MindSession]) {
        let all = historyAll
        let q = histQuery.trimmingCharacters(in: .whitespaces)
        if q.isEmpty { return (all.count, all) }
        let shown = all.filter { ss in
            if ss.title.localizedCaseInsensitiveContains(q) { return true }
            return ss.nodes.values.contains { $0.messages.contains { $0.content.localizedCaseInsensitiveContains(q) } }
        }
        return (all.count, shown)
    }

    private func confirm(_ title: String, _ text: String, ok: String) -> Bool {
        let a = NSAlert()
        a.messageText = title
        a.informativeText = text
        a.addButton(withTitle: ok)
        a.addButton(withTitle: "取消")
        var result = false
        modal { result = a.runModal() == .alertFirstButtonReturn }
        return result
    }

    private func modal(_ body: () -> Void) {
        if let f = onModal { f(body) } else { body() }
        focusTick += 1
    }

    func deleteNode(_ n: MindNode) {
        guard let ss = session, n.parentId != nil else { return }
        let ids = Mind.subtree(ss, n.id)
        let more = ids.count > 1 ? "和它下面的 \(ids.count - 1) 条分支" : ""
        guard confirm("删除这条追问？", "「\(Mind.truncate(n.title, 30))」\(more)会一起删除。", ok: "删除") else { return }
        for r in running.values where r.s === ss && ids.contains(r.node.id) { r.task?.cancel() }
        for id in ids { ss.nodes.removeValue(forKey: id) }
        if let cur = nodeId, ids.contains(cur) { nodeId = n.parentId }
        clearReply()
        closePopover()
        store.save(ss, immediate: true)
        refreshNow()
    }

    func deleteSession(_ ss: MindSession) {
        guard confirm("删除这段对话？", "「\(Mind.truncate(ss.title, 30))」删除后不能恢复。", ok: "删除") else { return }
        for r in running.values where r.s === ss { r.task?.cancel() }
        store.delete(ss.id)
        if ss === session { session = nil; nodeId = nil; clearReply() }
        refreshHistory()
        refreshNow()
    }

    // ================= 图片 =================

    /// ⌘V：剪贴板里是图片（并且没有文字）就当附件。返回 true 表示已经处理了
    func pasteImage() -> Bool {
        guard mode == .chat else { return false }
        let pb = NSPasteboard.general
        // 访达里拷贝的图片文件（同时带着文件名文字，要先看文件）
        let files = ClipImage.imageFiles(pb)
        if !files.isEmpty {
            for u in files.prefix(BarModel.maxImages) {
                if let img = NSImage(contentsOf: u) { addPending(img) }
            }
            return true
        }
        if ClipImage.hasFileURLs(pb) { return false }   // 拷贝的是别的文件：照常粘贴文件名
        if let t = pb.string(forType: .string), !t.isEmpty { return false }
        guard let img = ClipImage.read(pb) else { return false }
        addPending(img)
        return true
    }

    func addPending(_ img: NSImage) {
        if pending.count >= BarModel.maxImages { setStatus("一次最多 \(BarModel.maxImages) 张图片", error: true); return }
        guard let enc = ClipImage.jpeg(img) else { setStatus("图片读不出来，换一张试试", error: true); return }
        pending.append(PendingImage(thumb: img, jpeg: enc))
        setStatus("已添加图片，写上问题回车发送；直接回车 = 分析图片。看图需要能看图的模型。")
        focusTick += 1
    }

    func removePending(_ id: UUID) { pending.removeAll { $0.id == id } }

    func pickImages() {
        let p = NSOpenPanel()
        p.allowsMultipleSelection = true
        p.canChooseDirectories = false
        p.allowedContentTypes = [.image]
        p.message = "选择要发给 AI 的图片"
        var urls: [URL] = []
        modal { if p.runModal() == .OK { urls = p.urls } }
        for u in urls {
            if let img = NSImage(contentsOf: u) { addPending(img) }
            else { setStatus("这张图片打不开：\(u.lastPathComponent)", error: true) }
        }
    }

    /// 拖进来的图片文件 / 图片
    func drop(_ providers: [NSItemProvider]) -> Bool {
        guard mode == .chat else { return false }
        var handled = false
        for p in providers {
            if p.hasItemConformingToTypeIdentifier(UTType.fileURL.identifier) {
                handled = true
                _ = p.loadObject(ofClass: URL.self) { url, _ in
                    guard let url, url.isFileURL, let img = NSImage(contentsOf: url) else { return }
                    Task { @MainActor [weak self] in self?.addPending(img) }
                }
            } else if p.hasItemConformingToTypeIdentifier(UTType.image.identifier) {
                handled = true
                p.loadDataRepresentation(forTypeIdentifier: UTType.image.identifier) { data, _ in
                    guard let data, let img = NSImage(data: data) else { return }
                    Task { @MainActor [weak self] in self?.addPending(img) }
                }
            }
        }
        return handled
    }

    /// 把待发送的图片存进附件文件夹，变成附件记录
    private func takePending() -> [MindAttachment]? {
        guard !pending.isEmpty else { return nil }
        var list: [MindAttachment] = []
        for p in pending {
            do {
                let a = try store.saveImage(p.jpeg)
                if let u = store.resolve(a) { imgCache[u.path] = p.jpeg.base64EncodedString() }
                list.append(a)
            } catch {
                setStatus("图片保存失败：\(error.localizedDescription)", error: true)
            }
        }
        pending.removeAll()
        return list.isEmpty ? nil : list
    }

    /// 附件 → 发给 AI 的 JPEG base64（插件存的 png 等统一转成 JPEG）
    func loadImageB64(_ a: MindAttachment) -> String? {
        guard let u = store.resolve(a) else { return nil }
        if let hit = imgCache[u.path] { return hit }
        guard let data = try? Data(contentsOf: u) else { return nil }
        let ext = u.pathExtension.lowercased()
        var b64: String?
        let px = NSBitmapImageRep(data: data).map { max($0.pixelsWide, $0.pixelsHigh) } ?? Int.max
        if (ext == "jpg" || ext == "jpeg") && data.count < 3_500_000 && px <= 1568 {
            b64 = data.base64EncodedString()
        } else if let img = NSImage(data: data), let j = ClipImage.jpeg(img) {
            b64 = j.base64EncodedString()
        }
        if let b64 { imgCache[u.path] = b64 }
        return b64
    }

    /// 聊天里显示的缩略图
    func thumb(_ a: MindAttachment) -> (NSImage?, URL?) {
        guard let u = store.resolve(a) else { return (nil, nil) }
        if let t = thumbCache[u.path] { return (t, u) }
        guard let img = NSImage(contentsOf: u) else { return (nil, u) }
        thumbCache[u.path] = img
        return (img, u)
    }
}

/// 选中回答里的文字后弹出的追问小窗
struct SelPopView: View {
    let m: BarModel
    let presets: [String]

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 6) {
                Button(action: { m.startReply() }) {
                    Text("✦ 追问…")
                        .font(.system(size: 13, weight: .semibold))
                        .foregroundColor(.tAccentInk)
                        .padding(.horizontal, 12)
                        .frame(height: 28)
                        .background(Capsule().fill(Color.tAccent))
                }
                .buttonStyle(.plain)
                .help("就这段话提问（在输入框里写问题）")
                Button(action: { m.copySelection() }) {
                    Text("复制")
                        .font(.system(size: 13))
                        .foregroundColor(.tInk)
                        .padding(.horizontal, 12)
                        .frame(height: 28)
                        .background(Capsule().fill(Color.tSurface2))
                }
                .buttonStyle(.plain)
            }
            ForEach(Array(presets.enumerated()), id: \.offset) { _, q in
                Button(action: { m.askPreset(q) }) {
                    Text(q)
                        .font(.system(size: 13))
                        .foregroundColor(.tInk)
                        .lineLimit(1)
                        .padding(.horizontal, 10)
                        .frame(maxWidth: .infinity, minHeight: 26, alignment: .leading)
                        .background(RoundedRectangle(cornerRadius: 8).fill(Color.tSurface2))
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .help(q)
            }
        }
        .padding(10)
        .frame(width: 280)
    }
}
