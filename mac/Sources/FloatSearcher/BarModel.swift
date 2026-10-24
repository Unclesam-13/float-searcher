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
    @Published var clipExpanded = false
    @Published var status: String?
    @Published var statusError = false
    @Published var busy = false
    @Published var height: CGFloat = 0
    @Published var focusTick = 0

    var onHide: (() -> Void)?

    private var chatHistory: [ChatMsg] = []
    private var clipHistory: [ChatMsg] = []
    private var ownCopy: String?
    private var gen = 0                 // 每次提问 +1；旧请求的收尾不会改动新请求的界面
    private var task: Task<Void, Never>?
    private var trCache: [String: String] = [:]
    private var pendingAutoSend = false

    var s: AppSettings { AppSettings.shared }

    // ================= 打开 / 收起 =================

    /// 打开前准备：换模式、清空中译英、读剪贴板
    func prepare(for m: Mode, wasVisible: Bool) {
        let changed = m != mode || !wasVisible
        if busy && m != mode { stop() }
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
            sendClip(display: s.clipPrompt, prompt: s.clipPrompt, includeText: true)
        }
    }

    func willHide() {
        if mode == .translate && busy { stop() }
    }

    func hide() { onHide?() }

    var placeholder: String {
        switch mode {
        case .translate: return "输入中文，回车翻译成英文"
        case .chat: return "问点什么，回车发送（⌥+回车换行）"
        case .clipboard:
            if clipText == nil { return "先复制一段文字，再点悬浮图标" }
            return clip.isEmpty ? "对这段内容说点什么，回车发送" : "继续追问，回车发送"
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
            guard clipText != nil else {
                setStatus("剪贴板里没有文字。先复制，再点悬浮图标。", error: true)
                return
            }
            guard !text.isEmpty else { return }
            input = ""
            sendClip(display: text, prompt: text, includeText: clipHistory.isEmpty)
        }
    }

    func stop() {
        gen += 1
        task?.cancel()
        task = nil
        busy = false
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
        if busy && mode == .chat { stop() }
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
        stop()
        gen += 1
        let myGen = gen
        if let cached = trCache[key] {
            trLines = Prompts.split(cached)
            doneTranslating()
            return
        }
        busy = true
        setStatus("翻译中…")
        trLines = []
        let acc = Box("")
        task = Task { [weak self] in
            guard let self else { return }
            do {
                let full = try await AI.stream(AIConfig.current(), system: prompt,
                                               history: [ChatMsg(role: "user", content: text)],
                                               noThinking: true) { piece in
                    guard myGen == self.gen else { return }
                    acc.value += piece
                    self.trLines = Prompts.split(acc.value)
                }
                guard myGen == self.gen else { return }
                if full.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                    self.setStatus("没有拿到译文，再按一次回车试试", error: true)
                } else {
                    self.trCache[key] = full
                    self.trLines = Prompts.split(full)
                    self.doneTranslating()
                }
            } catch is CancellationError {
                if myGen == self.gen { self.setStatus(nil) }
            } catch {
                if myGen == self.gen { self.setStatus(error.localizedDescription, error: true) }
            }
            if myGen == self.gen { self.busy = false }
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

    func converse(isClip: Bool, display: String, content: String) {
        if busy { stop() }
        gen += 1
        let myGen = gen

        let userMsg = ChatMsg(role: "user", content: content)
        appendHistory(isClip, userMsg)
        appendUI(isClip, UIMsg(user: true, text: display))
        let answer = UIMsg(user: false, text: "…", waiting: true)
        let answerID = answer.id
        appendUI(isClip, answer)

        busy = true
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
                if myGen == self.gen { self.setStatus("正在联网搜索：\(q)") }
            },
            onReset: { acc.value = "" },
            onSources: { sources.value = $0 }
        ) : nil

        task = Task { [weak self] in
            guard let self else { return }
            do {
                let full = try await AI.stream(AIConfig.current(), system: system, history: history, web: web,
                                               onThinking: { [weak self] n in
                                                   guard let self, myGen == self.gen, acc.value.isEmpty else { return }
                                                   self.setStatus("深度思考中…（已想 \(n) 字）")
                                               }) { [weak self] piece in
                    guard let self else { return }
                    acc.value += piece
                    let shown = acc.value
                    self.updateUI(isClip, answerID) { $0.text = shown; $0.waiting = false }
                    if myGen == self.gen { self.setStatus("正在回答…") }
                }
                if full.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                    self.updateUI(isClip, answerID) { $0.text = "（没有收到回答）"; $0.waiting = false }
                    self.settle(isClip, userMsg, nil)
                } else {
                    let found = sources.value
                    self.updateUI(isClip, answerID) { $0.text = full; $0.waiting = false; $0.sources = found }
                    self.settle(isClip, userMsg, full)
                }
                if myGen == self.gen { self.setStatus(nil) }
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
                if myGen == self.gen { self.setStatus(msg, error: true) }
            }
            if myGen == self.gen { self.busy = false }
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

    /// 读剪贴板：内容变了就开始一段新对话
    func prepareClipboard() {
        let text = NSPasteboard.general.string(forType: .string)?.trimmingCharacters(in: .whitespacesAndNewlines)
        if let t = text, !t.isEmpty, t != clipText, t != ownCopy {
            if busy { stop() }
            clipText = t
            clipHistory.removeAll()
            clip.removeAll()
            clipExpanded = false
            // 「粘贴后自动发送」：显示出来后立刻用预设提示词发送
            pendingAutoSend = s.clipAction == .autoSend
        }
        if clipText == nil {
            setStatus("剪贴板里没有文字。先复制一段，再点悬浮图标。", error: true)
        }
    }

    func sendClip(display: String, prompt: String, includeText: Bool) {
        guard let t = clipText else { return }
        let content = includeText ? prompt + "\n\n内容：\n\"\"\"\n" + t + "\n\"\"\"" : prompt
        converse(isClip: true, display: display, content: content)
    }
}
