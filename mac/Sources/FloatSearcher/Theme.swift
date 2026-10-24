import AppKit
import SwiftUI

/// 和安卓版、Windows 版一致的玉石绿配色，跟随系统深浅色
enum Theme {
    static func dynamic(_ light: UInt32, _ dark: UInt32) -> NSColor {
        NSColor(name: nil) { appearance in
            let isDark = appearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua
            return NSColor(hex: isDark ? dark : light)
        }
    }

    static let bg = dynamic(0xF2F4F1, 0x0F1311)
    static let surface = dynamic(0xFFFFFF, 0x1A201D)
    static let surface2 = dynamic(0xE8EDE9, 0x252D29)
    static let quote = dynamic(0xEDF3EF, 0x1F2924)
    static let ink = dynamic(0x17201C, 0xE5ECE8)
    static let inkSoft = dynamic(0x5B6A63, 0x98A8A0)
    static let line = dynamic(0xD5DDD8, 0x2F3934)
    static let accent = dynamic(0x1E7A62, 0x5FCFAF)
    static let accentInk = dynamic(0xFFFFFF, 0x0A1914)
    static let hover = dynamic(0xE1E8E3, 0x2A332F)
    static let danger = dynamic(0xB23A2E, 0xF08A7E)
    static let accentSoft = NSColor(name: nil) { appearance in
        let isDark = appearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua
        return NSColor(hex: isDark ? 0x5FCFAF : 0x1E7A62).withAlphaComponent(0.18)
    }
}

extension NSColor {
    convenience init(hex: UInt32) {
        self.init(srgbRed: CGFloat((hex >> 16) & 0xFF) / 255,
                  green: CGFloat((hex >> 8) & 0xFF) / 255,
                  blue: CGFloat(hex & 0xFF) / 255, alpha: 1)
    }
}

extension Color {
    static let tBg = Color(nsColor: Theme.bg)
    static let tSurface = Color(nsColor: Theme.surface)
    static let tSurface2 = Color(nsColor: Theme.surface2)
    static let tQuote = Color(nsColor: Theme.quote)
    static let tInk = Color(nsColor: Theme.ink)
    static let tInkSoft = Color(nsColor: Theme.inkSoft)
    static let tLine = Color(nsColor: Theme.line)
    static let tAccent = Color(nsColor: Theme.accent)
    static let tAccentInk = Color(nsColor: Theme.accentInk)
    static let tAccentSoft = Color(nsColor: Theme.accentSoft)
    static let tHover = Color(nsColor: Theme.hover)
    static let tDanger = Color(nsColor: Theme.danger)
}
