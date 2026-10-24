import AppKit
import SwiftUI

@MainActor
final class SettingsController: NSObject, NSWindowDelegate {
    static let shared = SettingsController()
    private var window: NSWindow?

    func show() {
        if window == nil {
            let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 600, height: 760),
                             styleMask: [.titled, .closable, .miniaturizable, .resizable],
                             backing: .buffered, defer: false)
            w.title = "划词助手 · 设置"
            w.isReleasedWhenClosed = false
            w.contentView = NSHostingView(rootView: SettingsView(s: AppSettings.shared))
            w.minSize = NSSize(width: 480, height: 420)
            w.center()
            w.delegate = self
            window = w
        }
        if #available(macOS 14.0, *) { NSApp.activate() } else { NSApp.activate(ignoringOtherApps: true) }
        window?.makeKeyAndOrderFront(nil)
    }

    func windowWillClose(_ notification: Notification) {
        BubbleController.shared.refreshMode()
    }
}

struct SettingsView: View {
    @ObservedObject var s: AppSettings
    @State private var testText = ""
    @State private var testOK = false
    @State private var testing = false

    init(s: AppSettings) {
        _s = ObservedObject(wrappedValue: s)
    }

    var body: some View {
        Form {
            Section {
                Text("鼠标移到悬浮图标上会展开三个选项：中→英、AI 对话、剪贴板。选好后单击图标，旁边会横向弹出输入窗。右键图标或点菜单栏里的图标，可以打开这里或退出。")
                    .font(.callout)
                    .foregroundColor(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }

            Section("AI 接口") {
                Picker("接口", selection: $s.provider) {
                    ForEach(Provider.allCases, id: \.self) { p in Text(p.title).tag(p) }
                }
                .pickerStyle(.segmented)
                Text(providerHint).font(.caption).foregroundColor(.secondary)
                    .fixedSize(horizontal: false, vertical: true)

                TextField("接口地址", text: $s.baseURL, prompt: Text(s.provider.defaultBase))
                SecureField("API Key", text: $s.apiKey, prompt: Text("粘贴你的 Key"))
                TextField("模型", text: $s.model, prompt: Text(s.provider == .openai ? "填服务商给的模型名" : s.provider.defaultModel))

                if s.provider == .deepseek {
                    HStack {
                        Button("deepseek-flash · 快又省") { s.model = "deepseek-flash" }
                        Button("deepseek-v4-pro · 更强") { s.model = "deepseek-v4-pro" }
                    }
                    Toggle("联网搜索（AI 对话、剪贴板）：需要最新信息时自动搜索，回答附来源", isOn: $s.webSearch)
                    Text("AI 自己判断要不要搜、搜什么，最多搜 3 次。输入窗里的「联网」按钮也能随时开关。联网时不使用深度思考。每次搜索按 DeepSeek 的价格计费（约 0.75 美元 / 1000 次）。")
                        .font(.caption).foregroundColor(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                    Toggle("AI 对话时开启深度思考（更准，但要多等一会儿；中译英和联网时总是关闭）", isOn: $s.deepThink)
                }

                HStack {
                    Button(testing ? "连接中…" : "测试连接") { test() }.disabled(testing)
                    Text(testText)
                        .font(.caption)
                        .foregroundColor(testOK ? .tAccent : .tDanger)
                        .lineLimit(3)
                }
            }

            Section("中 → 英") {
                Text("输入中文按回车出英文；输入英文会反过来翻成中文。第一行总是最常用的译法，下面这些可以按需加上。")
                    .font(.caption).foregroundColor(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                Toggle("词语：给出同义词", isOn: $s.trSynonyms)
                if s.trSynonyms {
                    Stepper("最多 \(s.trSynonymCount) 个", value: $s.trSynonymCount, in: 1...6)
                }
                Toggle("句子：给出不同的表达方式（句式 / 语气 / 语法）", isOn: $s.trVariants)
                if s.trVariants {
                    VStack(alignment: .leading, spacing: 4) {
                        Text("每行一种，想要几种写几种。比如：日常口语、正式书面、简洁版、美式说法、英式说法、被动语态、强调句、倒装句、邮件用语。")
                            .font(.caption).foregroundColor(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                        TextEditor(text: $s.trVariantList)
                            .font(.body)
                            .frame(minHeight: 80)
                    }
                }
                Toggle("词语：附一个例句", isOn: $s.trExample)
                Toggle("附一句语法 / 用法说明", isOn: $s.trGrammar)
                VStack(alignment: .leading, spacing: 4) {
                    Text("其他要求（可留空），例如：偏商务场合；尽量用简单词汇；专有名词保留原文。")
                        .font(.caption).foregroundColor(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                    TextEditor(text: $s.trExtra)
                        .font(.body)
                        .frame(minHeight: 44)
                }
                Toggle("点到别处时自动收起输入窗", isOn: $s.trAutoHide)
            }

            Section("AI 对话") {
                VStack(alignment: .leading, spacing: 4) {
                    Text("给 AI 的固定说明（每次对话都会带上）").font(.caption).foregroundColor(.secondary)
                    TextEditor(text: $s.chatSystem)
                        .font(.body)
                        .frame(minHeight: 60)
                }
                Text("回车发送，⌥+回车换行。对话会一直保留，点输入窗里的「新对话」或菜单里的「开始新的 AI 对话」清空。")
                    .font(.caption).foregroundColor(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }

            Section("剪贴板") {
                Text("复制一段文字后单击悬浮图标，内容会自动放进对话。粘贴之后：")
                    .font(.caption).foregroundColor(.secondary)
                Picker("", selection: $s.clipAction) {
                    Text("自动用下面的提示词发送（最快）").tag(ClipAction.autoSend)
                    Text("先不发送，等我输入提示词再按回车").tag(ClipAction.askFirst)
                }
                .pickerStyle(.radioGroup)
                .labelsHidden()
                TextEditor(text: $s.clipPrompt)
                    .font(.body)
                    .frame(minHeight: 40)
                    .disabled(s.clipAction != .autoSend)
                    .opacity(s.clipAction == .autoSend ? 1 : 0.5)
                VStack(alignment: .leading, spacing: 4) {
                    Text("快捷提示词（输入窗里显示成按钮，点一下就对剪贴板内容提问）。每行一个：名称 | 要 AI 做什么")
                        .font(.caption).foregroundColor(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                    TextEditor(text: $s.clipPresets)
                        .font(.body)
                        .frame(minHeight: 110)
                }
            }

            Section("通用") {
                Toggle("开机自动启动", isOn: Binding(get: { s.launchAtLogin }, set: { s.launchAtLogin = $0 }))
                HStack {
                    Text("悬浮图标平时的透明度")
                    Slider(value: $s.bubbleOpacity, in: 0.3...1)
                    Text("\(Int(s.bubbleOpacity * 100))%").monospacedDigit().frame(width: 44, alignment: .trailing)
                }
                Text("鼠标移上去时总是完全不透明。设置改动会立即保存。")
                    .font(.caption).foregroundColor(.secondary)
            }
        }
        .formStyle(.grouped)
        .frame(minWidth: 480, minHeight: 420)
        .onChange(of: s.provider) { newValue in
            // 模型还是某家的默认值时，换成这一家的
            let known = Provider.allCases.map { $0.defaultModel } + ["deepseek-v4-pro"]
            if s.model.isEmpty || known.contains(s.model) { s.model = newValue.defaultModel }
            if Provider.allCases.map({ $0.defaultBase }).contains(s.baseURL) { s.baseURL = "" }
            BubbleController.shared.refreshMode()
        }
        .onChange(of: s.bubbleOpacity) { _ in BubbleController.shared.refreshMode() }
    }

    private var providerHint: String {
        switch s.provider {
        case .deepseek: return "DeepSeek 官方接口。Key 在 platform.deepseek.com 的「API keys」里创建。"
        case .anthropic: return "Anthropic 官方 Claude 接口。Key 在 console.anthropic.com 获取。"
        case .openai: return "OpenAI 或兼容它的服务（通义千问、Kimi、智谱等）。地址填到 /v1 这一级，模型名照服务商给的填。"
        }
    }

    private func test() {
        testing = true
        testText = ""
        Task { @MainActor in
            do {
                let r = try await AI.stream(AIConfig.current(), system: "只回复四个字：连接成功",
                                            history: [ChatMsg(role: "user", content: "测试")],
                                            noThinking: true) { _ in }
                testOK = true
                testText = "✓ 可以用了：" + String(r.trimmingCharacters(in: .whitespacesAndNewlines).prefix(30))
            } catch {
                testOK = false
                testText = error.localizedDescription
            }
            testing = false
        }
    }
}
