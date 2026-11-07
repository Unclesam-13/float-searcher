import AppKit
import ServiceManagement

/// 点悬浮图标时打开哪一种
enum Mode: String, CaseIterable {
    case translate, chat

    var title: String {
        switch self {
        case .translate: return "中 → 英"
        case .chat: return "AI 对话"
        }
    }

    /// 悬浮图标上的字
    var glyph: String {
        switch self {
        case .translate: return "译"
        case .chat: return "AI"
        }
    }

    var hint: String {
        switch self {
        case .translate: return "输入中文，回车出英文"
        case .chat: return "和 AI 对话：选中回答里的一段可以追问，有历史和问答树；⌘V 粘贴截图"
        }
    }
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

    // ---- 划线追问 / 历史 ----
    /// 划线追问的预设问题，每行一个
    @Published var mindPresets: String { didSet { d.set(mindPresets, forKey: "mindPresets") } }
    /// 划线后直接回车追问时用的问题
    @Published var mindDefaultFollowUp: String { didSet { d.set(mindDefaultFollowUp, forKey: "mindDefaultFollowUp") } }
    /// 对话历史保存位置；空 = ~/Library/Application Support/FloatSearcher/对话数据
    @Published var mindFolder: String { didSet { d.set(mindFolder, forKey: "mindFolder") } }

    // ---- 悬浮图标 ----
    @Published var bubbleOpacity: Double { didSet { d.set(bubbleOpacity, forKey: "bubbleOpacity") } }

    static let defaultChatSystem =
        "你是电脑桌面上的悬浮助手。回答简洁清晰，先给结论再解释；需要时用短列表，少用标题和表格。默认用简体中文回答。"
    static let defaultMindPresets = """
        请详细解释这段内容。
        能举一个具体的例子吗？
        用通俗易懂的话再讲一遍。
        这和前面的内容有什么联系？
        """
    static let defaultMindFollowUp = "请详细解释这段内容。"

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
        // 旧版本的「剪贴板」模式已经去掉了：改成 AI 对话
        let savedMode = ud.string(forKey: "mode") ?? ""
        mode = Mode(rawValue: savedMode) ?? (savedMode == "clipboard" ? .chat : .translate)

        trSynonyms = bool("trSynonyms", true)
        trSynonymCount = ud.object(forKey: "trSynonymCount") == nil ? 3 : ud.integer(forKey: "trSynonymCount")
        trVariants = bool("trVariants", true)
        trVariantList = str("trVariantList", "日常口语\n正式书面\n简洁版")
        trExample = bool("trExample", false)
        trGrammar = bool("trGrammar", false)
        trExtra = str("trExtra", "")
        trAutoHide = bool("trAutoHide", true)

        chatSystem = str("chatSystem", AppSettings.defaultChatSystem)
        mindPresets = str("mindPresets", AppSettings.defaultMindPresets)
        mindDefaultFollowUp = str("mindDefaultFollowUp", AppSettings.defaultMindFollowUp)
        mindFolder = str("mindFolder", "")

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

    var mindPresetList: [String] {
        mindPresets.split(separator: "\n").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
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
