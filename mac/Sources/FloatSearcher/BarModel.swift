import AppKit
import SwiftUI

/// 输入窗里的一条对话气泡
struct UIMsg: Identifiable {
    let id = UUID()
    var user: Bool
    var text: String
    var error = false
    var waiting = false
    var sources: [SearchHit] = []
}

/// 在异步任务里累积文字用的小盒子（任务闭包里不能直接改外面的 var）
final class Box<T> {
    var value: T
    init(_ v: T) { value = v }
}

/// 输入窗的全部状态和逻辑：中译英、AI 对话、剪贴板
@MainActor
final class BarModel: ObservableObject {
    @Published var mode: Mode = .translate
    @Published var input = ""
    @Published var trLines: [Prompts.Line] = []
    @Published var chat: [UIMsg] = []
    @Published var clip: [UIMsg] = []
    @Published var clipText: String?
    /// 剪贴板里的图片（没有文字时才用）：缩略图 + 发给 AI 的 JPEG
    @Published var clipImage: NSImage?
    @Published var clipImageInfo = ""
    private var clipImageB64: String?
    private var clipImageChange = -1   // 读到这张图时剪贴板的 changeCount
    var hasClip: Bool { clipText != nil || clipImage != nil }
    @Published var clipExpanded = false
    @Published var status: String?
    @Published var statusError = false
    /// 每种模式各自的请求：查单词不会打断正在输出的 AI 回答
    @Published private(set) var busyModes: Set<Mode> = []
    @Published var height: CGFloat = 0
    @Published var focusTick = 0

    var onHide: (() -> Void)?

    private var chatHistory: [ChatMsg] = []
    private var clipHistory: [ChatMsg] = []
    private var ownCopy: String?
    private var gens: [Mode: Int] = [:]          // 每次提问 +1；旧请求的收尾不会改动新请求的界面
    private var tasks: [Mode: Task<Void, Never>] = [:]

    /// 当前模式是否在等回答
    var busy: Bool { busyModes.contains(mode) }

    private func gen(_ m: Mode) -> Int { gens[m, default: 0] }

    private func bump(_ m: Mode) -> Int {
        let g = gen(m) + 1
        gens[m] = g
        return g
    }

    /// 这次请求的界面更新还算不算数（没被新请求取代，且用户正看着这个模式）
    private func live(_ m: Mode, _ g: Int) -> Bool { gen(m) == g }
    private func shown(_ m: Mode, _ g: Int) -> Bool { gen(m) == g && mode == m }
    private var trCache: [String: String] = [:]
    private var pendingAutoSend = false

    var s: AppSettings { AppSettings.shared }

    // ================= 打开 / 收起 =================

    /// 打开前准备：换模式、清空中译英、读剪贴板
    func prepare(for m: Mode, wasVisible: Bool) {
        let changed = m != mode || !wasVisible
        mode = m
        setStatus(nil)
        if m == .translate && changed {
            input = ""
            trLines = []
        }
        if m == .clipboard { prepareClipboard() }
    }

    /// 显示出来之后：剪贴板「自动发送」就立刻问
    func afterShow() {
        if mode == .clipboard && pendingAutoSend {
            pendingAutoSend = false
            let p = clipImage != nil ? s.clipImagePrompt : s.clipPrompt
            sendClip(display: p, prompt: p, includeText: true)
        }
    }

    /// 快捷提示词按钮；剪贴板是图片时最前面多一个「提取文字」
    var chipList: [(name: String, prompt: String)] {
        var list = s.presetList
        if clipImage != nil {
            list.insert(("提取文字", "把图片里的文字原样提取出来，保持原有的分段和顺序，不要解释。"), at: 0)
        }
        return list
    }

    func willHide() {
        if busyModes.contains(.translate) { stop(.translate) }
    }

    func hide() { onHide?() }

    var placeholder: String {
        switch mode {
        case .translate: return "输入中文，回车翻译成英文"
        case .chat: return "问点什么，回车发送（⌥+回车换行）"
        case .clipboard:
            if !hasClip { return "先复制一段文字或一张图片，再点悬浮图标" }
            if !clip.isEmpty { return "继续追问，回车发送" }
            return clipImage != nil ? "对这张图片说点什么，回车发送" : "对这段内容说点什么，回车发送"
        }
    }

    var hasOutput: Bool {
        switch mode {
        case .translate: return !trLines.isEmpty
        case .chat: return !chat.isEmpty
        case .clipboard: return !clip.isEmpty
        }
    }

    func setStatus(_ t: String?, error: Bool = false) {
        status = t
        statusError = error
    }

    // ================= 提交 =================

    func submit() {
        let text = input.trimmingCharacters(in: .whitespacesAndNewlines)
        switch mode {
        case .translate:
            if !text.isEmpty { translate(text) }
        case .chat:
            guard !text.isEmpty else { return }
            input = ""
            converse(isClip: false, display: text, content: text)
        case .clipboard:
            guard hasClip else {
                setStatus("剪贴板里没有文字或图片。先复制，再点悬浮图标。", error: true)
                return
            }
            guard !text.isEmpty else { return }
            input = ""
            // 图片已经在第一问里了，追问时不用再带（对话记录里保留着）
            sendClip(display: text, prompt: text, includeText: clipHistory.isEmpty)
        }
    }

    /// 停止某个模式的请求（默认当前模式）
    func stop(_ which: Mode? = nil) {
        let m = which ?? mode
        _ = bump(m)
        tasks[m]?.cancel()
        tasks[m] = nil
        busyModes.remove(m)
    }

    func stopByUser() {
        stop()
        setStatus("已停止")
    }

    func newConversation() {
        stop()
        if mode == .chat {
            chatHistory.removeAll()
            chat.removeAll()
        } else if mode == .clipboard {
            clipHistory.removeAll()
            clip.removeAll()
        }
        setStatus(nil)
        focusTick += 1
    }

    func resetChat() {
        if busyModes.contains(.chat) { stop(.chat) }
        chatHistory.removeAll()
        chat.removeAll()
    }

    func toggleWeb() {
        s.webSearch.toggle()
        setStatus(s.webSearch ? "已开启联网：需要时会先搜索，回答附来源" : "已关闭联网")
    }

    // ================= 复制 =================

    func copy(_ text: String) {
        guard !text.isEmpty else { return }
        let pb = NSPasteboard.general
        pb.clearContents()
        pb.setString(text, forType: .string)
        ownCopy = text
        let one = text.replacingOccurrences(of: "\n", with: " ")
        setStatus("已复制：" + (one.count > 40 ? String(one.prefix(40)) + "…" : one))
    }

    // ================= 中译英 =================

    func translate(_ text: String) {
        let prompt = Prompts.translate(s)
        let key = prompt + "\u{1}" + text
        stop(.translate)
        let myGen = bump(.translate)
        if let cached = trCache[key] {
            trLines = Prompts.split(cached)
            doneTranslating()
            return
        }
        busyModes.insert(.translate)
        setStatus("翻译中…")
        trLines = []
        let acc = Box("")
        tasks[.translate] = Task { [weak self] in
            guard let self else { return }
            do {
                let full = try await AI.stream(AIConfig.current(), system: prompt,
                                               history: [ChatMsg(role: "user", content: text)],
                                               noThinking: true) { piece in
                    guard self.live(.translate, myGen) else { return }
                    acc.value += piece
                    self.trLines = Prompts.split(acc.value)
                }
                guard self.live(.translate, myGen) else { return }
                if full.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                    if self.mode == .translate { self.setStatus("没有拿到译文，再按一次回车试试", error: true) }
                } else {
                    self.trCache[key] = full
                    self.trLines = Prompts.split(full)
                    if self.mode == .translate { self.doneTranslating() }
                }
            } catch is CancellationError {
                if self.shown(.translate, myGen) { self.setStatus(nil) }
            } catch {
                if self.shown(.translate, myGen) { self.setStatus(error.localizedDescription, error: true) }
            }
            if self.live(.translate, myGen) {
                self.busyModes.remove(.translate)
                self.tasks[.translate] = nil
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

    // ================= 对话（AI 对话、剪贴板共用） =================

    func converse(isClip: Bool, display: String, content: String, image: String? = nil) {
        let key: Mode = isClip ? .clipboard : .chat
        stop(key)
        let myGen = bump(key)

        let userMsg = ChatMsg(role: "user", content: content, image: image)
        appendHistory(isClip, userMsg)
        appendUI(isClip, UIMsg(user: true, text: display))
        let answer = UIMsg(user: false, text: "…", waiting: true)
        let answerID = answer.id
        appendUI(isClip, answer)

        busyModes.insert(key)
        setStatus("思考中…")
        let history = isClip ? clipHistory : chatHistory
        let system = s.chatSystem
        let useWeb = s.webAvailable && s.webSearch
        let acc = Box("")
        let sources = Box<[SearchHit]>([])

        let web: WebHooks? = useWeb ? WebHooks(
            onSearch: { [weak self] q in
                guard let self else { return }
                if acc.value.isEmpty { self.updateUI(isClip, answerID) { $0.text = "正在搜索：\(q) …"; $0.waiting = true } }
                if self.shown(key, myGen) { self.setStatus("正在联网搜索：\(q)") }
            },
            onReset: { acc.value = "" },
            onSources: { sources.value = $0 }
        ) : nil

        tasks[key] = Task { [weak self] in
            guard let self else { return }
            do {
                let full = try await AI.stream(AIConfig.current(), system: system, history: history, web: web,
                                               onThinking: { [weak self] n in
                                                   guard let self, self.shown(key, myGen), acc.value.isEmpty else { return }
                                                   self.setStatus("深度思考中…（已想 \(n) 字）")
                                               }) { [weak self] piece in
                    guard let self else { return }
                    acc.value += piece
                    let shown = acc.value
                    self.updateUI(isClip, answerID) { $0.text = shown; $0.waiting = false }
                    if self.shown(key, myGen) { self.setStatus("正在回答…") }
                }
                if full.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                    self.updateUI(isClip, answerID) { $0.text = "（没有收到回答）"; $0.waiting = false }
                    self.settle(isClip, userMsg, nil)
                } else {
                    let found = sources.value
                    self.updateUI(isClip, answerID) { $0.text = full; $0.waiting = false; $0.sources = found }
                    self.settle(isClip, userMsg, full)
                }
                if self.shown(key, myGen) { self.setStatus(nil) }
            } catch is CancellationError {
                let partial = acc.value
                if partial.isEmpty { self.updateUI(isClip, answerID) { $0.text = "（已停止）"; $0.waiting = false } }
                self.settle(isClip, userMsg, partial.isEmpty ? nil : partial)
            } catch {
                let msg = error.localizedDescription
                let partial = acc.value
                if partial.isEmpty {
                    self.updateUI(isClip, answerID) { $0.text = "⚠ " + msg; $0.error = true; $0.waiting = false }
                }
                self.settle(isClip, userMsg, partial.isEmpty ? nil : partial)
                if self.shown(key, myGen) { self.setStatus(msg, error: true) }
            }
            if self.live(key, myGen) {
                self.busyModes.remove(key)
                self.tasks[key] = nil
            }
        }
    }

    private func appendHistory(_ isClip: Bool, _ m: ChatMsg) {
        if isClip {
            clipHistory.append(m)
            trim(&clipHistory, keepHead: true)
        } else {
            chatHistory.append(m)
            trim(&chatHistory, keepHead: false)
        }
    }

    private func appendUI(_ isClip: Bool, _ m: UIMsg) {
        if isClip { clip.append(m) } else { chat.append(m) }
    }

    private func updateUI(_ isClip: Bool, _ id: UUID, _ f: (inout UIMsg) -> Void) {
        if isClip {
            if let i = clip.firstIndex(where: { $0.id == id }) { f(&clip[i]) }
        } else {
            if let i = chat.firstIndex(where: { $0.id == id }) { f(&chat[i]) }
        }
    }

    /// 有回答就紧跟在这条提问后面放进记录；没有回答就撤回这条提问（方便重试）。对话已清空时什么也不做。
    private func settle(_ isClip: Bool, _ userMsg: ChatMsg, _ answer: String?) {
        func apply(_ h: inout [ChatMsg]) {
            guard let i = h.firstIndex(where: { $0.id == userMsg.id }) else { return }
            if let a = answer, !a.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                h.insert(ChatMsg(role: "assistant", content: a), at: i + 1)
            } else {
                h.remove(at: i)
            }
        }
        if isClip { apply(&clipHistory) } else { apply(&chatHistory) }
    }

    /// 只带最近 20 条；开头必须是用户的话。剪贴板对话保留第一问一答（里面有原文）。
    private func trim(_ h: inout [ChatMsg], keepHead: Bool) {
        let start = keepHead && h.count > 2 && h[0].role == "user" && h[1].role == "assistant" ? 2 : 0
        while h.count > 20 && h.count - start > 2 {
            h.remove(at: start)
            if h.count - start > 1 && h[start].role != "user" { h.remove(at: start) }
        }
        while h.count > 1 && h[0].role != "user" { h.remove(at: 0) }
    }

    // ================= 剪贴板 =================

    /// 读剪贴板：有文字用文字，没有文字再看有没有图片；内容变了就开始一段新对话
    func prepareClipboard() {
        let pb = NSPasteboard.general
        let text = pb.string(forType: .string)?.trimmingCharacters(in: .whitespacesAndNewlines)
        if let t = text, !t.isEmpty {
            if t != clipText && t != ownCopy {
                startClip()
                clipText = t
            }
        } else if pb.changeCount != clipImageChange, let img = ClipImage.read(pb) {
            clipImageChange = pb.changeCount
            if let enc = ClipImage.jpegBase64(img) {
                startClip()
                clipImage = img
                clipImageB64 = enc.base64
                clipImageInfo = "图片 \(enc.width)×\(enc.height)"
            } else {
                setStatus("这张图片读不出来，换一张试试", error: true)
            }
        }
        if !hasClip {
            setStatus("剪贴板里没有文字或图片。先复制一段文字或一张图片，再点悬浮图标。", error: true)
        }
    }

    /// 换了新的剪贴板内容：清掉旧对话
    private func startClip() {
        if busyModes.contains(.clipboard) { stop(.clipboard) }
        clipText = nil
        clipImage = nil
        clipImageB64 = nil
        clipImageInfo = ""
        clipHistory.removeAll()
        clip.removeAll()
        clipExpanded = false
        // 「粘贴后自动发送」：显示出来后立刻用预设提示词发送
        pendingAutoSend = s.clipAction == .autoSend
    }

    func sendClip(display: String, prompt: String, includeText: Bool) {
        if let img = clipImageB64 {
            // 图片：第一次提问（或点快捷按钮）时把图片带上
            converse(isClip: true, display: display, content: prompt, image: includeText ? img : nil)
            return
        }
        guard let t = clipText else { return }
        let content = includeText ? prompt + "\n\n内容：\n\"\"\"\n" + t + "\n\"\"\"" : prompt
        converse(isClip: true, display: display, content: content)
    }
}
