import SwiftUI
import NativeCore
import UniformTypeIdentifiers

extension UTType {
    static let vemoLibraryItem = UTType(exportedAs: "io.github.sangdnog.vemo.library-item", conformingTo: .data)
}

/// Whole-row surfaces keep the tree's outer edges aligned; only its contents indent.
struct VemoFolderRow: View {
    let name: String
    let icon: String
    let count: Int?
    let depth: Int
    let selected: Bool
    var targeted = false
    var expanded: Bool? = nil
    let select: () -> Void
    var toggle: (() -> Void)? = nil
    var drag: (() -> NSItemProvider)? = nil
    @Environment(\.accessibilityReduceMotion) private var reducedMotion
    @Environment(\.colorSchemeContrast) private var contrast
    @ViewState private var hovered = false

    var body: some View {
        HStack(spacing: 0) {
            if let expanded, let toggle {
                Button(action: toggle) {
                    Image(systemName: "chevron.right")
                        .font(.system(size: 9, weight: .semibold))
                        .rotationEffect(.degrees(expanded ? 90 : 0))
                        .foregroundStyle(.secondary)
                        .frame(width: 25, height: 34).contentShape(Rectangle())
                }.buttonStyle(.plain)
                    .accessibilityLabel(name + (expanded ? " 하위 폴더 접기" : " 하위 폴더 펼치기"))
            } else {
                Color.clear.frame(width: 25, height: 34).accessibilityHidden(true)
            }
            selectionSurface

        }
        .padding(.leading, CGFloat(min(depth, 6)) * 13)
        .background(background, in: RoundedRectangle(cornerRadius: 10, style: .continuous))
        .overlay {
            RoundedRectangle(cornerRadius: 10, style: .continuous)
                .strokeBorder(targeted ? VemoTheme.accent.opacity(0.55) : (contrast == .increased && selected ? VemoTheme.ink : .clear), lineWidth: 1)
        }
        .contentShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
        .onHover { hovered = $0 }
        .animation(VemoTheme.motion(reducedMotion), value: hovered)
        .animation(VemoTheme.motion(reducedMotion), value: targeted)
        .animation(VemoTheme.motion(reducedMotion), value: selected)
    }
    private var selectionSurface: some View {
        rowLabel
            .onTapGesture(perform: select)
            .modifier(VemoDragSource(provider: drag))
            .focusable()
            .onKeyPress(.space) { select(); return .handled }
            .onKeyPress(.return) { select(); return .handled }
            .accessibilityElement(children: .ignore)
            .accessibilityAddTraits(.isButton)
            .accessibilityAction { select() }
            .accessibilityLabel(name + (count.map { ", \($0)개 녹음" } ?? ""))
            .accessibilityAddTraits(selected ? .isSelected : [])
    }
    private var rowLabel: some View {
        HStack(spacing: 9) {
            Image(systemName: icon)
                .font(.system(size: 13, weight: .medium))
                .foregroundStyle(selected || targeted ? VemoTheme.ink : Color.secondary).frame(width: 17)
            Text(name).lineLimit(1)
            Spacer(minLength: 4)
            if let count {
                Text("\(count)").font(.system(size: 10, weight: .medium).monospacedDigit())
                    .foregroundStyle(selected ? VemoTheme.ink : Color.secondary)
                    .padding(.horizontal, 6).padding(.vertical, 2)
                    .background(Color.primary.opacity(0.035), in: Capsule())
            }
        }
        .font(.system(size: 12, weight: selected ? .semibold : .regular))
        .foregroundStyle(selected ? VemoTheme.ink : Color.primary)
        .padding(.trailing, 10).frame(maxWidth: .infinity, minHeight: 34)
        .contentShape(Rectangle())
    }
    private var background: Color {
        if targeted { return VemoTheme.accent.opacity(0.15) }
        if selected { return VemoTheme.accent.opacity(0.10) }
        return hovered ? Color.primary.opacity(0.045) : .clear
    }
}

/// Explicit parent choices expose hierarchy without a long, flat popup menu.
struct VemoParentFolderPicker: View {
    let folders: [LibraryFolder]
    let editingID: String?
    @Binding var selection: String

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("상위 폴더").font(.caption.weight(.medium)).foregroundStyle(.secondary)
            ScrollView {
                VStack(spacing: 3) {
                    choice("최상위", id: "", depth: 0, icon: "square.stack.3d.up")
                    ForEach(LibraryFolderHierarchy.parentCandidates(for: editingID, in: folders)) { row in
                        choice(row.folder.name, id: row.id, depth: row.depth + 1, icon: "folder")
                            .help(LibraryFolderHierarchy.path(for: row.id, in: folders))
                    }
                }.padding(6)
            }
            .frame(height: min(CGFloat(LibraryFolderHierarchy.parentCandidates(for: editingID, in: folders).count + 1) * 36 + 12, 210))
            .background(Color.primary.opacity(0.025), in: RoundedRectangle(cornerRadius: 12, style: .continuous))
            .overlay { RoundedRectangle(cornerRadius: 12, style: .continuous).strokeBorder(Color.primary.opacity(0.06), lineWidth: 1) }
            Text(selection.isEmpty ? "라이브러리의 최상위에 저장합니다." : LibraryFolderHierarchy.path(for: selection, in: folders))
                .font(.caption).foregroundStyle(.secondary).lineLimit(2)
        }
    }
    private func choice(_ name: String, id: String, depth: Int, icon: String) -> some View {
        Button { selection = id } label: {
            HStack(spacing: 9) {
                Image(systemName: icon).frame(width: 17).foregroundStyle(selection == id ? VemoTheme.ink : .secondary)
                Text(name).lineLimit(1)
                Spacer(minLength: 6)
                Image(systemName: selection == id ? "checkmark.circle.fill" : "circle")
                    .foregroundStyle(selection == id ? VemoTheme.ink : Color.secondary.opacity(0.25))
            }.font(.system(size: 12, weight: selection == id ? .medium : .regular))
                .padding(.leading, 10 + CGFloat(min(depth, 7)) * 13).padding(.trailing, 10)
                .frame(maxWidth: .infinity, minHeight: 33)
                .background(selection == id ? VemoTheme.wash : .clear, in: RoundedRectangle(cornerRadius: 8, style: .continuous))
                .contentShape(Rectangle())
        }.buttonStyle(.plain)
            .accessibilityLabel("상위 폴더: " + (id.isEmpty ? name : LibraryFolderHierarchy.path(for: id, in: folders)))
            .accessibilityAddTraits(selection == id ? .isSelected : [])
    }
}

/// A reference is shared with delegates before AppKit starts its drag tracking loop.
/// A per-drag token binds the local payload to its provider, including after a cancelled drag.
@MainActor final class VemoLibraryDragSession {
    private(set) var payload: LibraryDragPayload?
    private var token: String?

    func provider(for payload: LibraryDragPayload) -> NSItemProvider {
        self.payload = payload
        let token = "io.github.sangdnog.vemo.drag-token." + UUID().uuidString.lowercased()
        self.token = token
        let provider = NSItemProvider()
        provider.registerDataRepresentation(forTypeIdentifier: token, visibility: .ownProcess) { completion in
            completion(Data(), nil)
            return nil
        }
        provider.registerDataRepresentation(forTypeIdentifier: UTType.vemoLibraryItem.identifier, visibility: .ownProcess) { completion in
            completion(try? JSONEncoder().encode(payload), nil)
            return nil
        }
        return provider
    }
    func payload(for providers: [NSItemProvider]) -> LibraryDragPayload? {
        guard providers.count == 1, let token, providers[0].hasItemConformingToTypeIdentifier(token) else { return nil }
        return payload
    }
    func finish() { payload = nil; token = nil }
}

/// Only this app's current provider can move library metadata; fileURL import stays separate.
@MainActor struct VemoLibraryDropDelegate: DropDelegate {
    let model: AppModel
    let target: LibraryDropTarget
    let session: VemoLibraryDragSession
    @Binding var targeted: Bool
    var accepted: () -> Void = {}

    func validateDrop(info: DropInfo) -> Bool {
        let providers = info.itemProviders(for: [.vemoLibraryItem])
        let valid = session.payload(for: providers).map { model.canDropLibraryItem($0, on: target) } == true
        return valid
    }
    func dropEntered(info: DropInfo) { targeted = validateDrop(info: info) }
    func dropExited(info: DropInfo) { targeted = false }
    func dropUpdated(info: DropInfo) -> DropProposal? {
        let valid = validateDrop(info: info)
        targeted = valid
        return DropProposal(operation: valid ? .move : .cancel)
    }
    func performDrop(info: DropInfo) -> Bool {
        targeted = false
        guard let payload = session.payload(for: info.itemProviders(for: [.vemoLibraryItem])),
              model.dropLibraryItem(payload, on: target) else { return false }
        session.finish()
        accepted()
        // Acceptance means the existing serialized save was queued. Disk failures use model.problem.
        return true
    }
}

struct VemoDragSource: ViewModifier {
    let provider: (() -> NSItemProvider)?
    @ViewBuilder func body(content: Content) -> some View {
        if let provider { content.onDrag(provider) } else { content }
    }
}
