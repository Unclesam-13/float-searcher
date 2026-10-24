import AppKit
import ServiceManagement

/// 点悬浮图标时打开哪一种
enum Mode: String, CaseIterable {
    case translate, chat, clipboard

    var title: String {
        switch self {
        case .translate: return "中 → 英"
        case .chat: return "AI 对话"
        case .clipboard: return "剪贴板"
        }
    }

    /// 悬浮图标上的字
    var glyph: String {
        switch self {
        case .translate: return "译"
        case .chat: return "AI"
        case .clipboard: return "贴"
        }
    }

    var hint: String {
        switch self {
        case .translate: return "输入中文，回车出英文"
        case .chat: return "和 AI 对话，回车发送"
        case .clipboard: return "复制后点图标，自动带上剪贴板内容"
        }
    }
}

enum ClipAction: String {
    case autoSend, askFirst
}

enum Provider: String, CaseIterable {
    case deepseek, anthropic, openai

    var title: String {
        switch self {
        case .deepseek: return "DeepSeek"
        case .anthropic: return "Claude"
        case .openai: return "其他兼容接口"
        }
    }

    var defaultBase: String {
        switch self {
        case .anthropic: return "https://api.anthropic.com"
        case .deepseek: return "https://api.deepseek.com"
        case .openai: return "https://api.openai.com/v1"
        }
    }

    var defaultModel: String {
        switch self {
        case .anthropic: return "claude-sonnet-5-5"
        case .deepseek: return "deepseek-flash"
        case .openai: return ""
        }
    }
}

/// 所有设置，保存在 UserDefaults（~/Library/Preferences/com.huaci.floatsearcher.plist）
final class AppSettings: ObservableObject {
    static let shared = AppSettings()
    private let d = UserDefaults.standard

    // ---- AI 接口 ----
    @Published var provider: Provider { didSet { d.set(provider.rawValue, forKey: "provider") } }
    @Published var baseURL: String { didSet { d.set(baseURL, forKey: "baseURL") } }
    @Published var apiKey: String { didSet { d.set(apiKey, forKey: "apiKey") } }
    @Published var model: String { didSet { d.set(model, forKey: "model") } }
    @Published var deepThink: Bool { didSet { d.set(deepThink, forKey: "deepThink") } }
    @Published var webSearch: Bool { didSet { d.set(webSearch, forKey: "webSearch") } }

    // ---- 当前模式（扇形里选的，下次还是它） ----
    @Published var mode: Mode { didSet { d.set(mode.rawValue, forKey: "mode") } }

    // ---- 中译英 ----
    @Published var trSynonyms: Bool { didSet { d.set(trSynonyms, forKey: "trSynonyms") } }
    @Published var trSynonymCount: Int { didSet { d.set(trSynonymCount, forKey: "trSynonymCount") } }
    @Published var trVariants: Bool { didSet { d.set(trVariants, forKey: "trVariants") } }
    @Published var trVariantList: String { didSet { d.set(trVariantList, forKey: "trVariantList") } }
    @Published var trExample: Bool { didSet { d.set(trExample, forKey: "trExample") } }
    @Published var trGrammar: Bool { didSet { d.set(trGrammar, forKey: "trGrammar") } }
    @Published var trExtra: String { didSet { d.set(trExtra, forKey: "trExtra") } }
    @Published var trAutoHide: Bool { didSet { d.set(trAutoHide, forKey: "trAutoHide") } }

    // ---- AI 对话 ----
    @Published var chatSystem: String { didSet { d.set(chatSystem, forKey: "chatSystem") } }

    // ---- 剪贴板 ----
    @Published var clipAction: ClipAction { didSet { d.set(clipAction.rawValue, forKey: "clipAction") } }
    @Published var clipPrompt: String { didSet { d.set(clipPrompt, forKey: "clipPrompt") } }
    @Published var clipPresets: String { didSet { d.set(clipPresets, forKey: "clipPresets") } }

    // ---- 悬浮图标 ----
    @Published var bubbleOpacity: Double { didSet { d.set(bubbleOpacity, forKey: "bubbleOpacity") } }

    static let defaultChatSystem =
        "你是电脑桌面上的悬浮助手。回答简洁清晰，先给结论再解释；需要时用短列表，少用标题和表格。默认用简体中文回答。"
    static let defaultClipPrompt = "请解释这段内容。如果是外文，先翻译成中文再解释。"
    static let defaultClipPresets = """
        解释 | 用通俗的话解释这段内容。
        翻译成中文 | 把这段内容翻译成自然流畅的中文，只给译文。
        翻译成英文 | 把这段内容翻译成地道的英文，只给译文。
        总结 | 用三句话以内总结要点。
        润色 | 在不改变原意的前提下润色这段文字，直接给出改好的版本。
        怎么回复 | 这是别人发给我的消息，帮我写两三个得体的回复。
        """

    private init() {
        // 初始化时不能用 self，先用局部变量读
        let ud = UserDefaults.standard
        func str(_ k: String, _ def: String) -> String { ud.string(forKey: k) ?? def }
        func bool(_ k: String, _ def: Bool) -> Bool { ud.object(forKey: k) == nil ? def : ud.bool(forKey: k) }

        provider = Provider(rawValue: ud.string(forKey: "provider") ?? "") ?? .deepseek
        baseURL = str("baseURL", "")
        apiKey = str("apiKey", "")
        model = str("model", "")
        deepThink = bool("deepThink", false)
        webSearch = bool("webSearch", true)
        mode = Mode(rawValue: ud.string(forKey: "mode") ?? "") ?? .translate

        trSynonyms = bool("trSynonyms", true)
        trSynonymCount = ud.object(forKey: "trSynonymCount") == nil ? 3 : ud.integer(forKey: "trSynonymCount")
        trVariants = bool("trVariants", true)
        trVariantList = str("trVariantList", "日常口语\n正式书面\n简洁版")
        trExample = bool("trExample", false)
        trGrammar = bool("trGrammar", false)
        trExtra = str("trExtra", "")
        trAutoHide = bool("trAutoHide", true)

        chatSystem = str("chatSystem", AppSettings.defaultChatSystem)
        clipAction = ClipAction(rawValue: ud.string(forKey: "clipAction") ?? "") ?? .autoSend
        clipPrompt = str("clipPrompt", AppSettings.defaultClipPrompt)
        clipPresets = str("clipPresets", AppSettings.defaultClipPresets)

        bubbleOpacity = ud.object(forKey: "bubbleOpacity") == nil ? 0.95 : ud.double(forKey: "bubbleOpacity")
    }

    // ---- 派生值 ----

    var effectiveBase: String {
        let b = baseURL.trimmingCharacters(in: .whitespaces)
        return b.isEmpty ? provider.defaultBase : b
    }

    var effectiveModel: String {
        let m = model.trimmingCharacters(in: .whitespaces)
        return m.isEmpty ? provider.defaultModel : m
    }

    var hasKey: Bool { !apiKey.trimmingCharacters(in: .whitespaces).isEmpty }

    /// 只有 DeepSeek 能联网
    var webAvailable: Bool { provider == .deepseek }

    var variantNames: [String] {
        trVariantList.split(separator: "\n").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
    }

    /// 快捷提示词：每行「名称 | 指令」
    var presetList: [(name: String, prompt: String)] {
        var out: [(String, String)] = []
        for raw in clipPresets.split(separator: "\n") {
            let line = raw.trimmingCharacters(in: .whitespaces)
            if line.isEmpty { continue }
            if let r = line.firstIndex(where: { $0 == "|" || $0 == "｜" }), r != line.startIndex {
                let name = line[..<r].trimmingCharacters(in: .whitespaces)
                let prompt = line[line.index(after: r)...].trimmingCharacters(in: .whitespaces)
                if !name.isEmpty && !prompt.isEmpty { out.append((name, prompt)) }
            } else {
                out.append((line.count > 6 ? String(line.prefix(6)) + "…" : line, line))
            }
        }
        return out
    }

    // ---- 悬浮图标位置（图标中心，屏幕坐标） ----

    var bubbleCenter: CGPoint? {
        get {
            guard d.object(forKey: "bubbleX") != nil else { return nil }
            return CGPoint(x: d.double(forKey: "bubbleX"), y: d.double(forKey: "bubbleY"))
        }
        set {
            guard let p = newValue else { return }
            d.set(Double(p.x), forKey: "bubbleX")
            d.set(Double(p.y), forKey: "bubbleY")
        }
    }

    static func alert(_ text: String) {
        Task { @MainActor in
            let a = NSAlert()
            a.messageText = "划词助手"
            a.informativeText = text
            a.runModal()
        }
    }

    // ---- 开机自启（macOS 13+ 的「登录项」） ----

    var launchAtLogin: Bool {
        get { SMAppService.mainApp.status == .enabled }
        set {
            if newValue && !Bundle.main.bundlePath.hasPrefix("/Applications") {
                AppSettings.alert("请先把「划词助手」拖进「应用程序」文件夹，再从那里打开，然后再开启开机自动启动。")
                objectWillChange.send()
                return
            }
            do {
                if newValue { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
                if newValue && SMAppService.mainApp.status == .requiresApproval {
                    SMAppService.openSystemSettingsLoginItems()
                }
            } catch {
                AppSettings.alert("开机自动启动没设置成功：\(error.localizedDescription)\n可以到「系统设置 → 通用 → 登录项」里手动添加。")
            }
            objectWillChange.send()
        }
    }
}
