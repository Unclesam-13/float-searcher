import AppKit

/// 程序入口：不在程序坞显示，只有屏幕上的悬浮图标和右上角菜单栏图标
@main
@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    static func main() {
        let app = NSApplication.shared
        let delegate = AppDelegate()
        app.delegate = delegate
        app.setActivationPolicy(.accessory)
        // 没有主菜单时 ⌘V / ⌘C 等快捷键不起作用：建一个看不见的「编辑」菜单
        app.mainMenu = AppDelegate.editMenu()
        withExtendedLifetime(delegate) { app.run() }
    }

    static func editMenu() -> NSMenu {
        let main = NSMenu()
        let appItem = NSMenuItem()
        let appMenu = NSMenu()
        appMenu.addItem(NSMenuItem(title: "退出划词助手", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q"))
        appItem.submenu = appMenu
        main.addItem(appItem)

        let editItem = NSMenuItem()
        let edit = NSMenu(title: "编辑")
        edit.addItem(NSMenuItem(title: "撤销", action: Selector(("undo:")), keyEquivalent: "z"))
        let redo = NSMenuItem(title: "重做", action: Selector(("redo:")), keyEquivalent: "z")
        redo.keyEquivalentModifierMask = [.command, .shift]
        edit.addItem(redo)
        edit.addItem(.separator())
        edit.addItem(NSMenuItem(title: "剪切", action: #selector(NSText.cut(_:)), keyEquivalent: "x"))
        edit.addItem(NSMenuItem(title: "复制", action: #selector(NSText.copy(_:)), keyEquivalent: "c"))
        edit.addItem(NSMenuItem(title: "粘贴", action: #selector(NSText.paste(_:)), keyEquivalent: "v"))
        edit.addItem(NSMenuItem(title: "全选", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a"))
        editItem.submenu = edit
        main.addItem(editItem)
        return main
    }

    private var statusItem: NSStatusItem?

    func applicationDidFinishLaunching(_ notification: Notification) {
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        let menu = NSMenu()
        menu.delegate = self
        fill(menu)
        item.menu = menu
        statusItem = item
        refreshStatusItem()

        BubbleController.shared.show()
        // 接上 / 拔掉显示器后，把图标挪回屏幕里
        NotificationCenter.default.addObserver(forName: NSApplication.didChangeScreenParametersNotification,
                                               object: nil, queue: .main) { _ in
            Task { @MainActor in
                let b = BubbleController.shared
                b.moveCenter(to: b.center)
                BarController.shared.reposition()
            }
        }
        // 第一次用：先填接口
        if !AppSettings.shared.hasKey { SettingsController.shared.show() }
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

    func refreshStatusItem() {
        guard let button = statusItem?.button else { return }
        let img = NSImage(systemSymbolName: "character.bubble", accessibilityDescription: "划词助手")
        img?.isTemplate = true
        button.image = img
        button.imagePosition = .imageLeading
        button.title = " " + AppSettings.shared.mode.glyph
        button.toolTip = "划词助手 · 当前：\(AppSettings.shared.mode.title)"
    }

    // ---------- 菜单（菜单栏图标和右键悬浮图标共用） ----------

    func menuNeedsUpdate(_ menu: NSMenu) { fill(menu) }

    func buildMenu() -> NSMenu {
        let m = NSMenu()
        fill(m)
        return m
    }

    private func fill(_ menu: NSMenu) {
        menu.removeAllItems()
        let head = NSMenuItem(title: "悬浮图标单击时打开：", action: nil, keyEquivalent: "")
        head.isEnabled = false
        menu.addItem(head)
        for m in Mode.allCases {
            let it = NSMenuItem(title: "\(m.title)　\(m.hint)", action: #selector(pickMode(_:)), keyEquivalent: "")
            it.target = self
            it.representedObject = m.rawValue
            it.state = AppSettings.shared.mode == m ? .on : .off
            menu.addItem(it)
        }
        menu.addItem(.separator())

        let open = NSMenuItem(title: "打开输入窗", action: #selector(openBar), keyEquivalent: "")
        open.target = self
        menu.addItem(open)

        let newChat = NSMenuItem(title: "开始新的 AI 对话", action: #selector(newChat), keyEquivalent: "")
        newChat.target = self
        menu.addItem(newChat)

        let settings = NSMenuItem(title: "设置…", action: #selector(openSettings), keyEquivalent: ",")
        settings.target = self
        menu.addItem(settings)

        menu.addItem(.separator())
        let login = NSMenuItem(title: "开机自动启动", action: #selector(toggleLogin), keyEquivalent: "")
        login.target = self
        login.state = AppSettings.shared.launchAtLogin ? .on : .off
        menu.addItem(login)

        let quit = NSMenuItem(title: "退出划词助手", action: #selector(quit), keyEquivalent: "q")
        quit.target = self
        menu.addItem(quit)
    }

    @objc private func pickMode(_ sender: NSMenuItem) {
        guard let raw = sender.representedObject as? String, let m = Mode(rawValue: raw) else { return }
        BubbleController.shared.choose(m)
    }

    @objc private func openBar() {
        BarController.shared.show(AppSettings.shared.mode)
    }

    @objc private func newChat() {
        BarController.shared.resetChat()
    }

    @objc private func openSettings() {
        SettingsController.shared.show()
    }

    @objc private func toggleLogin() {
        AppSettings.shared.launchAtLogin.toggle()
    }

    @objc private func quit() {
        NSApp.terminate(nil)
    }
}
