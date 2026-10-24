import AppKit

/// 不抢焦点的悬浮面板：盖在所有窗口上，所有桌面、全屏 App 上都能看到
final class FloatPanel: NSPanel {
    var allowKey = false
    var onCancel: (() -> Void)?
    override var canBecomeKey: Bool { allowKey }
    override var canBecomeMain: Bool { false }

    /// Esc
    override func cancelOperation(_ sender: Any?) {
        if let f = onCancel { f() } else { super.cancelOperation(sender) }
    }

    static func make(_ rect: NSRect, canKey: Bool) -> FloatPanel {
        let p = FloatPanel(contentRect: rect, styleMask: [.borderless, .nonactivatingPanel],
                           backing: .buffered, defer: false)
        p.allowKey = canKey
        p.isOpaque = false
        p.backgroundColor = .clear
        p.hasShadow = false
        p.level = .floating
        p.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        p.isFloatingPanel = true
        p.hidesOnDeactivate = false
        p.isMovable = false
        p.becomesKeyOnlyIfNeeded = false
        p.isReleasedWhenClosed = false
        return p
    }
}

/// 屏幕上的悬浮图标，以及它展开的扇形选项
@MainActor
final class BubbleController {
    static let shared = BubbleController()

    static let size: CGFloat = 96       // 窗口边长；图标在正中
    static let radius: CGFloat = 26     // 图标半径
    static let fanRadius: CGFloat = 98  // 选项离图标中心的距离
    static let spreadDeg: CGFloat = 52  // 相邻选项之间的角度

    let panel: FloatPanel
    let view: BubbleView
    let fan: FanController

    private var openTask: Task<Void, Never>?
    private var pollTask: Task<Void, Never>?
    private var lastInside = Date()
    private(set) var fanOpen = false
    private var fanDir: CGFloat = 0
    var barWasOpenAtDown = false

    private init() {
        let s = BubbleController.size
        panel = FloatPanel.make(NSRect(x: 0, y: 0, width: s, height: s), canKey: false)
        view = BubbleView(frame: NSRect(x: 0, y: 0, width: s, height: s))
        panel.contentView = view
        fan = FanController()
        view.controller = self
        fan.onChoose = { [weak self] m in self?.choose(m) }
    }

    func show() {
        let c = AppSettings.shared.bubbleCenter ?? defaultCenter()
        moveCenter(to: c)
        refreshMode()
        panel.orderFrontRegardless()
    }

    // ---------- 位置 ----------

    var center: CGPoint {
        CGPoint(x: panel.frame.midX, y: panel.frame.midY)
    }

    /// 图标在屏幕上的圆形区域（屏幕坐标）
    var bubbleRect: NSRect {
        let r = BubbleController.radius + 2
        return NSRect(x: center.x - r, y: center.y - r, width: r * 2, height: r * 2)
    }

    static func screen(containing p: CGPoint) -> NSScreen {
        NSScreen.screens.first { NSMouseInRect(p, $0.frame, false) } ?? NSScreen.main ?? NSScreen.screens[0]
    }

    private func defaultCenter() -> CGPoint {
        let vf = (NSScreen.main ?? NSScreen.screens[0]).visibleFrame
        return CGPoint(x: vf.maxX - 44, y: vf.minY + vf.height * 0.6)
    }

    /// 把图标中心放到 c，并保证完整地在所在屏幕的可用区域里（不压菜单栏、程序坞）
    func moveCenter(to c: CGPoint) {
        let vf = BubbleController.screen(containing: c).visibleFrame
        let m: CGFloat = 30
        let x = min(max(c.x, vf.minX + m), vf.maxX - m)
        let y = min(max(c.y, vf.minY + m), vf.maxY - m)
        let h = BubbleController.size / 2
        panel.setFrameOrigin(NSPoint(x: x - h, y: y - h))
    }

    // ---------- 鼠标事件（由 BubbleView 转过来） ----------

    func hoverBegan() {
        view.hoverOn()
        openTask?.cancel()
        // 稍停一下再展开，避免鼠标路过时乱弹
        openTask = Task { @MainActor [weak self] in
            try? await Task.sleep(nanoseconds: 220_000_000)
            guard let self, !Task.isCancelled, self.view.hovering, !self.view.pressed else { return }
            self.openFan()
        }
    }

    func hoverEnded() {
        view.hoverOff()
        if !fanOpen { openTask?.cancel() }
    }

    func mouseDown() {
        barWasOpenAtDown = BarController.shared.wasOpen()
    }

    func dragBegan() {
        openTask?.cancel()
        closeFan()
    }

    func dragEnded() {
        moveCenter(to: center)
        AppSettings.shared.bubbleCenter = center
        BarController.shared.reposition()
    }

    func clicked() {
        openTask?.cancel()
        closeFan()
        BarController.shared.toggle(wasOpen: barWasOpenAtDown)
    }

    // ---------- 模式 ----------

    func choose(_ m: Mode) {
        AppSettings.shared.mode = m
        refreshMode()
        closeFan()
        view.bounce()
        BarController.shared.onModeChosen(m)
        (NSApp.delegate as? AppDelegate)?.refreshStatusItem()
    }

    func refreshMode() {
        view.setGlyph(AppSettings.shared.mode.glyph)
        view.applyIdleOpacity()
        view.toolTip = "当前：\(AppSettings.shared.mode.title)（单击打开，悬停切换，右键菜单）"
        fan.refresh()
    }

    // ---------- 扇形 ----------

    /// 朝屏幕中间展开（靠右边时往左，靠左边时往右），偏向水平方向
    private func fanDirection() -> CGFloat {
        let c = center
        let vf = BubbleController.screen(containing: c).visibleFrame
        let dx = vf.midX - c.x
        let dy = vf.midY - c.y
        return atan2(dy * 0.6, dx)
    }

    func openFan() {
        guard !fanOpen else { return }
        fanOpen = true
        fanDir = fanDirection()
        fan.open(center: center, direction: fanDir)
        panel.orderFrontRegardless() // 图标在扇形上面
        lastInside = Date()
        pollTask?.cancel()
        pollTask = Task { @MainActor [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: 60_000_000)
                guard let self, !Task.isCancelled else { return }
                self.poll()
            }
        }
    }

    func closeFan() {
        guard fanOpen else { return }
        fanOpen = false
        pollTask?.cancel()
        pollTask = nil
        fan.close()
    }

    /// 鼠标还在「图标 + 扇形 + 选项」范围内就保持展开；离开一会儿后收起
    private func poll() {
        let m = NSEvent.mouseLocation
        let c = center
        let dx = m.x - c.x, dy = m.y - c.y
        let dist = sqrt(dx * dx + dy * dy)
        var inside = dist <= BubbleController.radius + 10
        if !inside && dist <= BubbleController.fanRadius + 52 {
            var diff = atan2(dy, dx) - fanDir
            while diff > .pi { diff -= 2 * .pi }
            while diff < -.pi { diff += 2 * .pi }
            inside = abs(diff) <= (BubbleController.spreadDeg + 34) * .pi / 180
        }
        if !inside { inside = fan.pillScreenRects().contains { $0.insetBy(dx: -6, dy: -6).contains(m) } }
        if inside { lastInside = Date() }
        else if Date().timeIntervalSince(lastInside) > 0.38 { closeFan() }
    }

    // ---------- 右键菜单 ----------

    func showMenu(_ event: NSEvent) {
        openTask?.cancel()
        closeFan()
        guard let d = NSApp.delegate as? AppDelegate else { return }
        NSMenu.popUpContextMenu(d.buildMenu(), with: event, for: view)
    }
}

// ================= 图标视图 =================

final class BubbleView: NSView {
    weak var controller: BubbleController?
    private(set) var hovering = false
    private(set) var pressed = false
    private var dragging = false
    private var downMouse = NSPoint.zero
    private var downOrigin = NSPoint.zero

    private let group = CALayer()        // 圆 + 字，一起缩放
    private let circle = CALayer()
    private let ring = CAShapeLayer()    // 悬停时向外扩散的光圈
    private let text = CATextLayer()

    override init(frame: NSRect) {
        super.init(frame: frame)
        wantsLayer = true
        let root = layer!
        let c = CGPoint(x: bounds.midX, y: bounds.midY)
        let d = BubbleController.radius * 2

        ring.bounds = CGRect(x: 0, y: 0, width: d, height: d)
        ring.position = c
        ring.path = CGPath(ellipseIn: ring.bounds, transform: nil)
        ring.fillColor = nil
        ring.lineWidth = 3
        ring.opacity = 0
        root.addSublayer(ring)

        group.bounds = CGRect(x: 0, y: 0, width: d, height: d)
        group.position = c
        root.addSublayer(group)

        circle.frame = group.bounds
        circle.cornerRadius = d / 2
        circle.borderWidth = 2
        circle.shadowOpacity = 0.35
        circle.shadowRadius = 5
        circle.shadowOffset = CGSize(width: 0, height: -2)
        group.addSublayer(circle)

        text.alignmentMode = .center
        text.font = NSFont.systemFont(ofSize: 18, weight: .bold)
        text.fontSize = 18
        let th: CGFloat = 24
        text.frame = CGRect(x: 0, y: (d - th) / 2 - 1, width: d, height: th)
        group.addSublayer(text)

        applyColors()
    }

    required init?(coder: NSCoder) { fatalError() }

    override var isFlipped: Bool { false }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        let scale = window?.backingScaleFactor ?? 2
        text.contentsScale = scale
        ring.contentsScale = scale
    }

    override func viewDidChangeEffectiveAppearance() {
        super.viewDidChangeEffectiveAppearance()
        applyColors()
    }

    private func applyColors() {
        effectiveAppearance.performAsCurrentDrawingAppearance {
            circle.backgroundColor = Theme.accent.cgColor
            circle.borderColor = Theme.surface.cgColor
            ring.strokeColor = Theme.accent.cgColor
            text.foregroundColor = Theme.accentInk.cgColor
        }
    }

    func setGlyph(_ g: String) {
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        text.string = g
        let size: CGFloat = g.count > 1 ? 16 : 18
        text.fontSize = size
        text.font = NSFont.systemFont(ofSize: size, weight: .bold)
        CATransaction.commit()
    }

    func applyIdleOpacity() {
        if !hovering { alphaValue = CGFloat(AppSettings.shared.bubbleOpacity) }
    }

    /// 只有圆形区域能点；四角透明处让给下面的窗口
    override func hitTest(_ point: NSPoint) -> NSView? {
        let p = convert(point, from: superview)
        let dx = p.x - bounds.midX, dy = p.y - bounds.midY
        return dx * dx + dy * dy <= pow(BubbleController.radius + 4, 2) ? self : nil
    }

    private var tracking: NSTrackingArea?

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let t = tracking { removeTrackingArea(t) }
        let r = BubbleController.radius + 4
        let rect = NSRect(x: bounds.midX - r, y: bounds.midY - r, width: r * 2, height: r * 2)
        // .activeAlways：本程序不在前台时也要感应鼠标
        let t = NSTrackingArea(rect: rect, options: [.mouseEnteredAndExited, .activeAlways], owner: self, userInfo: nil)
        addTrackingArea(t)
        tracking = t
    }

    override func mouseEntered(with event: NSEvent) {
        hovering = true
        controller?.hoverBegan()
    }

    override func mouseExited(with event: NSEvent) {
        hovering = false
        controller?.hoverEnded()
    }

    override func mouseDown(with event: NSEvent) {
        pressed = true
        dragging = false
        downMouse = NSEvent.mouseLocation
        downOrigin = window?.frame.origin ?? .zero
        controller?.mouseDown()
    }

    override func mouseDragged(with event: NSEvent) {
        guard pressed else { return }
        let m = NSEvent.mouseLocation
        let dx = m.x - downMouse.x, dy = m.y - downMouse.y
        if !dragging && abs(dx) < 4 && abs(dy) < 4 { return }
        if !dragging {
            dragging = true
            controller?.dragBegan()
        }
        window?.setFrameOrigin(NSPoint(x: downOrigin.x + dx, y: downOrigin.y + dy))
    }

    override func mouseUp(with event: NSEvent) {
        guard pressed else { return }
        pressed = false
        if dragging { controller?.dragEnded() } else { controller?.clicked() }
        dragging = false
    }

    override func rightMouseDown(with event: NSEvent) {
        controller?.showMenu(event)
    }

    // ---------- 动画 ----------

    private func setScale(_ to: CGFloat, spring: Bool) {
        let from = (group.presentation()?.value(forKeyPath: "transform.scale") as? CGFloat) ?? 1
        let a: CABasicAnimation
        if spring {
            let s = CASpringAnimation(keyPath: "transform.scale")
            s.damping = 11
            s.stiffness = 220
            s.mass = 1
            s.duration = s.settlingDuration
            a = s
        } else {
            a = CABasicAnimation(keyPath: "transform.scale")
            a.duration = 0.16
            a.timingFunction = CAMediaTimingFunction(name: .easeOut)
        }
        a.fromValue = from
        a.toValue = to
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        group.setValue(to, forKeyPath: "transform.scale")
        CATransaction.commit()
        group.add(a, forKey: "scale")
    }

    func hoverOn() {
        alphaValue = 1
        setScale(1.14, spring: true)
        // 光圈：从图标边缘向外扩散并淡出，循环
        let grow = CABasicAnimation(keyPath: "transform.scale")
        grow.fromValue = 1.0
        grow.toValue = 1.55
        let fade = CABasicAnimation(keyPath: "opacity")
        fade.fromValue = 0.75
        fade.toValue = 0.0
        let g = CAAnimationGroup()
        g.animations = [grow, fade]
        g.duration = 1.1
        g.timingFunction = CAMediaTimingFunction(name: .easeOut)
        g.repeatCount = .infinity
        ring.add(g, forKey: "pulse")
    }

    func hoverOff() {
        setScale(1.0, spring: false)
        ring.removeAnimation(forKey: "pulse")
        alphaValue = CGFloat(AppSettings.shared.bubbleOpacity)
    }

    /// 切换模式后「跳」一下
    func bounce() {
        let a = CAKeyframeAnimation(keyPath: "transform.scale")
        let base = (group.presentation()?.value(forKeyPath: "transform.scale") as? CGFloat) ?? 1
        a.values = [base, 1.28, 0.96, 1.0]
        a.keyTimes = [0, 0.35, 0.7, 1]
        a.duration = 0.34
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        group.setValue(1.0, forKeyPath: "transform.scale")
        CATransaction.commit()
        group.add(a, forKey: "scale")
    }
}

// ================= 扇形选项 =================

@MainActor
final class FanController {
    let panel: FloatPanel
    let view: FanView
    var onChoose: ((Mode) -> Void)?
    private var hideTask: Task<Void, Never>?

    static let size: CGFloat = 340

    init() {
        let s = FanController.size
        panel = FloatPanel.make(NSRect(x: 0, y: 0, width: s, height: s), canKey: false)
        view = FanView(frame: NSRect(x: 0, y: 0, width: s, height: s))
        panel.contentView = view
        view.onChoose = { [weak self] m in self?.onChoose?(m) }
    }

    func refresh() { view.refreshStyles() }

    func open(center: CGPoint, direction: CGFloat) {
        hideTask?.cancel()
        let h = FanController.size / 2
        panel.setFrameOrigin(NSPoint(x: center.x - h, y: center.y - h))
        panel.orderFrontRegardless()
        view.open(direction: direction)
    }

    func close() {
        view.close()
        hideTask?.cancel()
        hideTask = Task { @MainActor [weak self] in
            try? await Task.sleep(nanoseconds: 160_000_000)
            guard let self, !Task.isCancelled else { return }
            self.panel.orderOut(nil)
        }
    }

    func pillScreenRects() -> [NSRect] {
        guard panel.isVisible else { return [] }
        let o = panel.frame.origin
        return view.pillFrames().map { $0.offsetBy(dx: o.x, dy: o.y) }
    }
}

final class FanView: NSView {
    var onChoose: ((Mode) -> Void)?

    private final class Pill {
        let mode: Mode
        let layer = CALayer()
        let text = CATextLayer()
        var frame = CGRect.zero
        init(_ m: Mode) { mode = m }
    }

    private var pills: [Pill] = []
    private var hovered: Mode?
    private static let pillW: CGFloat = 88
    private static let pillH: CGFloat = 34

    override init(frame: NSRect) {
        super.init(frame: frame)
        wantsLayer = true
        for m in Mode.allCases {
            let p = Pill(m)
            p.layer.bounds = CGRect(x: 0, y: 0, width: FanView.pillW, height: FanView.pillH)
            p.layer.cornerRadius = FanView.pillH / 2
            p.layer.borderWidth = 1
            p.layer.shadowOpacity = 0.22
            p.layer.shadowRadius = 4
            p.layer.shadowOffset = CGSize(width: 0, height: -1)
            p.layer.opacity = 0
            p.text.string = m.title
            p.text.alignmentMode = .center
            p.text.fontSize = 13
            p.text.font = NSFont.systemFont(ofSize: 13, weight: .semibold)
            p.text.frame = CGRect(x: 0, y: (FanView.pillH - 18) / 2 - 1, width: FanView.pillW, height: 18)
            p.layer.addSublayer(p.text)
            layer!.addSublayer(p.layer)
            pills.append(p)
        }
        refreshStyles()
    }

    required init?(coder: NSCoder) { fatalError() }

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        let scale = window?.backingScaleFactor ?? 2
        for p in pills { p.text.contentsScale = scale }
    }

    override func viewDidChangeEffectiveAppearance() {
        super.viewDidChangeEffectiveAppearance()
        refreshStyles()
    }

    func refreshStyles() {
        let current = AppSettings.shared.mode
        effectiveAppearance.performAsCurrentDrawingAppearance {
            CATransaction.begin()
            CATransaction.setDisableActions(true)
            for p in pills {
                let on = p.mode == current
                let hover = !on && p.mode == hovered
                p.layer.backgroundColor = (on ? Theme.accent : (hover ? Theme.hover : Theme.surface)).cgColor
                p.layer.borderColor = (on ? Theme.accent : Theme.line).cgColor
                p.text.foregroundColor = (on ? Theme.accentInk : Theme.ink).cgColor
            }
            CATransaction.commit()
        }
    }

    func pillFrames() -> [CGRect] { pills.map { $0.frame } }

    func open(direction: CGFloat) {
        let c = CGPoint(x: bounds.midX, y: bounds.midY)
        hovered = nil
        refreshStyles()
        for (i, p) in pills.enumerated() {
            let ang = direction + CGFloat(i - 1) * BubbleController.spreadDeg * .pi / 180
            let target = CGPoint(x: c.x + BubbleController.fanRadius * cos(ang),
                                 y: c.y + BubbleController.fanRadius * sin(ang))
            p.frame = CGRect(x: target.x - FanView.pillW / 2, y: target.y - FanView.pillH / 2,
                             width: FanView.pillW, height: FanView.pillH)

            CATransaction.begin()
            CATransaction.setDisableActions(true)
            p.layer.removeAllAnimations()
            p.layer.position = target
            p.layer.opacity = 1
            p.layer.setValue(1.0, forKeyPath: "transform.scale")
            CATransaction.commit()

            // 从图标中心「飞」出去
            let begin = CACurrentMediaTime() + Double(i) * 0.045
            let move = CASpringAnimation(keyPath: "position")
            move.fromValue = NSValue(point: c)
            move.toValue = NSValue(point: target)
            move.damping = 14
            move.stiffness = 260
            move.duration = move.settlingDuration
            let scale = CABasicAnimation(keyPath: "transform.scale")
            scale.fromValue = 0.3
            scale.toValue = 1.0
            scale.duration = 0.22
            scale.timingFunction = CAMediaTimingFunction(controlPoints: 0.34, 1.4, 0.64, 1)
            let fade = CABasicAnimation(keyPath: "opacity")
            fade.fromValue = 0.0
            fade.toValue = 1.0
            fade.duration = 0.15
            for a in [move, scale, fade] as [CAAnimation] {
                a.beginTime = begin
                a.fillMode = .backwards
                p.layer.add(a, forKey: nil)
            }
        }
    }

    func close() {
        for p in pills {
            let fade = CABasicAnimation(keyPath: "opacity")
            fade.fromValue = p.layer.presentation()?.opacity ?? 1
            fade.toValue = 0.0
            fade.duration = 0.13
            let scale = CABasicAnimation(keyPath: "transform.scale")
            scale.fromValue = 1.0
            scale.toValue = 0.6
            scale.duration = 0.13
            CATransaction.begin()
            CATransaction.setDisableActions(true)
            p.layer.opacity = 0
            CATransaction.commit()
            p.layer.add(fade, forKey: "fadeOut")
            p.layer.add(scale, forKey: "scaleOut")
        }
    }

    private func pill(at point: NSPoint) -> Pill? {
        pills.first { $0.frame.insetBy(dx: -4, dy: -4).contains(point) }
    }

    /// 只有选项本身能点；其余透明处让给下面的窗口
    override func hitTest(_ point: NSPoint) -> NSView? {
        pill(at: convert(point, from: superview)) != nil ? self : nil
    }

    private var tracking: NSTrackingArea?

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let t = tracking { removeTrackingArea(t) }
        let t = NSTrackingArea(rect: bounds, options: [.mouseMoved, .mouseEnteredAndExited, .activeAlways, .inVisibleRect],
                               owner: self, userInfo: nil)
        addTrackingArea(t)
        tracking = t
    }

    override func mouseMoved(with event: NSEvent) {
        let m = pill(at: convert(event.locationInWindow, from: nil))?.mode
        if m != hovered {
            hovered = m
            refreshStyles()
            if m != nil { NSCursor.pointingHand.set() } else { NSCursor.arrow.set() }
        }
    }

    override func mouseExited(with event: NSEvent) {
        hovered = nil
        refreshStyles()
    }

    override func mouseDown(with event: NSEvent) {
        if let p = pill(at: convert(event.locationInWindow, from: nil)) { onChoose?(p.mode) }
    }
}
