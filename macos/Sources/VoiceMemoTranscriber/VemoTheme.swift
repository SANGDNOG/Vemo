import SwiftUI

/// The icon's purple is reserved for actions and selection, with neutral surfaces.
enum VemoTheme {
    static let accent = Color(red: 98/255, green: 88/255, blue: 238/255)
    static let ink = Color(nsColor: NSColor(name: nil) { appearance in
        appearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua
            ? NSColor(red: 0.72, green: 0.68, blue: 1, alpha: 1)
            : NSColor(red: 0.34, green: 0.29, blue: 0.77, alpha: 1)
    })
    static let wash = accent.opacity(0.09)
    static func motion(_ reduced: Bool) -> Animation? { reduced ? nil : .easeInOut(duration: 0.18) }
}

struct VemoButtonStyle: ButtonStyle {
    enum Emphasis { case primary, secondary, quiet }
    var emphasis: Emphasis = .secondary
    @Environment(\.isEnabled) private var enabled
    @Environment(\.accessibilityReduceMotion) private var reducedMotion
    @Environment(\.colorSchemeContrast) private var contrast
    @ViewState private var hovered = false

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.system(size: 12, weight: .medium))
            .padding(.horizontal, emphasis == .quiet ? 8 : 12)
            .padding(.vertical, 7)
            .foregroundStyle(emphasis == .primary ? Color.white : (emphasis == .quiet ? Color.primary : VemoTheme.ink))
            .background(background(pressed: configuration.isPressed), in: RoundedRectangle(cornerRadius: 9, style: .continuous))
            .overlay { RoundedRectangle(cornerRadius: 9, style: .continuous).strokeBorder(emphasis == .primary ? .clear : Color.primary.opacity(contrast == .increased ? 0.3 : 0.07), lineWidth: 1) }
            .opacity(enabled ? 1 : 0.42)
            .scaleEffect(reducedMotion ? 1 : (configuration.isPressed ? 0.98 : 1))
            .animation(VemoTheme.motion(reducedMotion), value: configuration.isPressed)
            .animation(VemoTheme.motion(reducedMotion), value: hovered)
            .onHover { hovered = $0 }
    }
    private func background(pressed: Bool) -> Color {
        if emphasis == .primary { return VemoTheme.accent.opacity(pressed ? 0.82 : 1) }
        if emphasis == .quiet { return Color.primary.opacity(pressed ? 0.10 : (hovered ? 0.06 : 0)) }
        return VemoTheme.accent.opacity(pressed ? 0.18 : (hovered ? 0.13 : 0.08))
    }
}

extension View {
    func vemoMenuSurface() -> some View {
        self.menuStyle(.borderlessButton).fixedSize()
            .font(.system(size: 12, weight: .medium)).foregroundStyle(VemoTheme.ink)
            .padding(.horizontal, 11).padding(.vertical, 7)
            .background(VemoTheme.wash, in: RoundedRectangle(cornerRadius: 9))
    }
}
