import Foundation
import NativeCore

private func dragRejects(_ name: String, _ action: () async throws -> Void) async throws {
    do { try await action() } catch { print("PASS: \(name)"); return }
    throw NativeFailure.invalid("FAIL: \(name)")
}

private func dragUnchanged(_ name: String, root: URL, rejects: Bool = false, _ action: () async throws -> Void) async throws {
    let file = root.appendingPathComponent("native/library.json")
    let bytes = try Data(contentsOf: file)
    let before = try await LibraryFolderStore(root: root).load()
    let modified = try file.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate
    if rejects { try await dragRejects(name, action) } else { try await action() }
    try check(try Data(contentsOf: file) == bytes, "\(name): exact durable bytes unchanged")
    try check(try await LibraryFolderStore(root: root).load() == before, "\(name): state and revision unchanged after restart")
    try check(try file.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate == modified, "\(name): file modification time unchanged")
}

// Literal expectations catch swapped kinds, nil destinations, and accidental action/noop changes.
private func dragAction(_ actual: LibraryDropAction?, recording: String? = nil, folder: String? = nil, destination: String? = nil, _ name: String) throws {
    let matches: Bool
    switch actual {
    case .recording(let id, let folderID): matches = recording == id && folder == nil && folderID == destination
    case .folder(let id, let parentID): matches = folder == id && recording == nil && parentID == destination
    case nil: matches = recording == nil && folder == nil
    }
    try check(matches, name)
}

func libraryDragChecks() async throws {
    try await dragPayloadAndRulesChecks()
    try await dragPersistenceChecks()
    try await dragFreshStateChecks()
}

private func dragPayloadAndRulesChecks() async throws {
    let session = "drag-test-session"
    let state = LibraryOrganization(revision: 12, folders: [
        LibraryFolder(id: "a", name: "Alpha"),
        LibraryFolder(id: "b", name: "Beta"),
        LibraryFolder(id: "c", name: "Child", parentID: "a"),
        LibraryFolder(id: "g", name: "Grandchild", parentID: "c"),
        LibraryFolder(id: "collision", name: "CHILD", parentID: "b")
    ], assignments: ["filed-job": "a"])
    let known: Set<String> = ["filed-job", "unfiled-job"]
    func payload(_ kind: LibraryDragKind, _ id: String, sessionID: String = "drag-test-session", version: Int = 1) -> LibraryDragPayload {
        LibraryDragPayload(kind: kind, id: id, sessionID: sessionID, version: version)
    }
    func action(_ value: LibraryDragPayload, _ target: LibraryDropTarget, organization: LibraryOrganization? = nil, jobs: Set<String>? = nil) throws -> LibraryDropAction? {
        try LibraryDropRules.action(for: value, target: target, organization: organization ?? state, knownJobIDs: jobs ?? known, sessionID: session)
    }
    for (kind, id, target) in [(LibraryDragKind.recording, "unfiled-job", LibraryDropTarget.folder("b")), (.folder, "g", .root)] {
        let original = LibraryDragPayload(kind: kind, id: id, sessionID: session)
        let encoded = try JSONEncoder().encode(original)
        let decoded = try JSONDecoder().decode(LibraryDragPayload.self, from: encoded)
        try check(decoded.kind == kind && decoded.id == id && decoded.sessionID == session && decoded.version == 1, "drag payload roundtrip preserves kind, opaque ID, session, default version")
        let resolved = try action(decoded, target)
        if kind == .recording { try dragAction(resolved, recording: id, destination: "b", "decoded recording payload produces intended folder assignment") }
        else { try dragAction(resolved, folder: id, "decoded folder payload produces root move") }
    }
    for raw in [#"{"kind":"unknown","id":"c","sessionID":"drag-test-session","version":1}"#, #"{"kind":"folder","sessionID":"drag-test-session","version":1}"#, "{broken"] {
        try rejects("malformed or unknown-kind drag payload cannot decode") { _ = try JSONDecoder().decode(LibraryDragPayload.self, from: Data(raw.utf8)) }
    }
    for version in [0, 2, -1] {
        for (kind, id) in [(LibraryDragKind.recording, "filed-job"), (.folder, "g")] {
            let decoded = try JSONDecoder().decode(LibraryDragPayload.self, from: JSONEncoder().encode(payload(kind, id, version: version)))
            try rejects("unsupported drag version \(version) rejects at action boundary") { _ = try action(decoded, .folder("b")) }
        }
    }
    for (kind, id) in [(LibraryDragKind.recording, "filed-job"), (.folder, "g")] {
        try rejects("wrong-session \(kind) drag rejects") { _ = try action(payload(kind, id, sessionID: "another-session"), .folder("b")) }
        try rejects("empty-session \(kind) drag rejects") { _ = try action(payload(kind, id, sessionID: ""), .folder("b")) }
        try rejects("missing-destination \(kind) drag rejects") { _ = try action(payload(kind, id), .folder("deleted-folder")) }
        try rejects("empty folder destination is not root") { _ = try action(payload(kind, id), .folder("")) }
    }
    try dragAction(try action(payload(.recording, "filed-job"), .folder("b")), recording: "filed-job", destination: "b", "recording moves to another folder")
    try dragAction(try action(payload(.recording, "unfiled-job"), .folder("c")), recording: "unfiled-job", destination: "c", "unfiled recording moves into nested folder")
    try dragAction(try action(payload(.recording, "filed-job"), .unfiled), recording: "filed-job", "recording unfiled action has nil destination")
    try dragAction(try action(payload(.recording, "filed-job"), .folder("a")), "recording same-folder drop is noop")
    try dragAction(try action(payload(.recording, "unfiled-job"), .unfiled), "already unfiled recording drop is noop")
    try rejects("recording cannot target folder root") { _ = try action(payload(.recording, "filed-job"), .root) }
    try rejects("folder cannot target unfiled recordings") { _ = try action(payload(.folder, "g"), .unfiled) }
    try rejects("missing recording rejects even if stale assignment exists") { _ = try action(payload(.recording, "filed-job"), .folder("b"), jobs: ["unfiled-job"]) }
    try rejects("deleted recording rejects before unfiled noop") { _ = try action(payload(.recording, "unfiled-job"), .unfiled, jobs: []) }
    try rejects("wrong session rejects before same-folder recording noop") { _ = try action(payload(.recording, "filed-job", sessionID: "another-session"), .folder("a")) }
    try rejects("unsupported version rejects before root folder noop") { _ = try action(payload(.folder, "a", version: 2), .root) }
    try rejects("unknown recording ID rejects") { _ = try action(payload(.recording, "missing-job"), .unfiled) }
    try rejects("empty recording ID rejects") { _ = try action(payload(.recording, ""), .folder("b")) }
    try rejects("unknown folder source rejects before root noop") { _ = try action(payload(.folder, "missing-folder"), .root) }
    try rejects("empty folder ID rejects") { _ = try action(payload(.folder, ""), .root) }
    try dragAction(try action(payload(.folder, "g"), .folder("b")), folder: "g", destination: "b", "folder moves to unrelated parent")
    try dragAction(try action(payload(.folder, "g"), .root), folder: "g", "nested folder moves to root")
    try dragAction(try action(payload(.folder, "a"), .root), "root folder to root is noop")
    try dragAction(try action(payload(.folder, "c"), .folder("a")), "folder same-parent drop is noop")
    try rejects("folder self target rejects") { _ = try action(payload(.folder, "c"), .folder("c")) }
    try rejects("folder immediate child target rejects") { _ = try action(payload(.folder, "a"), .folder("c")) }
    try rejects("folder deep descendant target rejects") { _ = try action(payload(.folder, "a"), .folder("g")) }
    try rejects("folder sibling name collision rejects during drop validation") { _ = try action(payload(.folder, "c"), .folder("b")) }
    let unicode = LibraryOrganization(folders: [LibraryFolder(id: "p", name: "Parent"), LibraryFolder(id: "q", name: "Other"), LibraryFolder(id: "x", name: "Cafe\u{0301}", parentID: "p"), LibraryFolder(id: "y", name: "CAFÉ", parentID: "q")])
    try rejects("drop rejects normalized Unicode and case sibling collision") { _ = try action(payload(.folder, "x"), .folder("q"), organization: unicode) }
    let rootCollision = LibraryOrganization(folders: [LibraryFolder(id: "p", name: "Parent"), LibraryFolder(id: "x", name: "Café", parentID: "p"), LibraryFolder(id: "y", name: "CAFE\u{0301}")])
    try rejects("drop rejects normalized name collision at root") { _ = try action(payload(.folder, "x"), .root, organization: rootCollision) }
}

private func dragPersistenceChecks() async throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent("LibraryDragChecks-" + UUID().uuidString)
    defer { try? FileManager.default.removeItem(at: root) }
    let store = LibraryFolderStore(root: root)
    let a = try await store.create(name: "Alpha").folders.last!.id
    let b = try await store.create(name: "Beta").folders.last!.id
    let c = try await store.create(name: "Child", parentID: a).folders.last!.id
    let g = try await store.create(name: "Grandchild", parentID: c).folders.last!.id
    _ = try await store.assign(jobIDs: ["parent-job"], folderID: a)
    _ = try await store.assign(jobIDs: ["child-job"], folderID: c)
    _ = try await store.assign(jobIDs: ["grandchild-job"], folderID: g)
    _ = try await store.assign(jobIDs: ["other-job"], folderID: b)
    let before = try await store.load()
    let assignments = ["parent-job": a, "child-job": c, "grandchild-job": g, "other-job": b]
    let moved = try await store.moveFolder(id: c, parentID: b)
    try check(moved.revision == before.revision + 1 && moved.folders.first(where: { $0.id == c }) == LibraryFolder(id: c, name: "Child", parentID: b), "moveFolder persists original ID/name and one parent change in one revision")
    try check(moved.assignments == assignments && moved.folders.first(where: { $0.id == g })?.parentID == c, "moveFolder preserves subtree and all recording assignments")
    try check(try await LibraryFolderStore(root: root).load() == moved, "folder move survives fresh actor restart")
    try check(LibraryFolderHierarchy.path(for: g, in: moved.folders) == "Beta / Child / Grandchild", "persisted move changes full descendant path")
    try dragQueryChecks(moved, parent: a, destination: b, child: c, grandchild: g)
    try await dragUnchanged("same-parent store move is noop", root: root) {
        let noop = try await store.moveFolder(id: c, parentID: b)
        try check(noop == moved, "same-parent move returns unchanged organization")
    }
    let detached = try await store.moveFolder(id: c, parentID: nil)
    try check(detached.revision == moved.revision + 1 && detached.folders.first(where: { $0.id == c })?.parentID == nil && detached.assignments == assignments && detached.folders.first(where: { $0.id == g })?.parentID == c, "root move persists once and retains subtree assignments")
    try check(try await LibraryFolderStore(root: root).load() == detached, "root move survives restart")
    try await dragUnchanged("root-to-root store move is noop", root: root) { _ = try await store.moveFolder(id: c, parentID: nil) }
    for (id, destination, name) in [(c, c, "self"), (c, g, "descendant"), (a, "missing", "deleted destination"), ("missing", a, "deleted source"), (c, "", "empty destination"), ("", a, "empty source")] {
        try await dragUnchanged("store rejects \(name)", root: root, rejects: true) { _ = try await store.moveFolder(id: id, parentID: destination) }
    }
    _ = try await store.create(name: "CHILD", parentID: a)
    try await dragUnchanged("case sibling collision rejected atomically", root: root, rejects: true) { _ = try await store.moveFolder(id: c, parentID: a) }
    let cafe = try await store.create(name: "Cafe\u{0301}", parentID: b).folders.last!.id
    _ = try await store.create(name: "CAFÉ", parentID: a)
    _ = try await store.create(name: "Café")
    try await dragUnchanged("normalized sibling collision rejected atomically", root: root, rejects: true) { _ = try await store.moveFolder(id: cafe, parentID: a) }
    try await dragUnchanged("normalized root collision rejected atomically", root: root, rejects: true) { _ = try await store.moveFolder(id: cafe, parentID: nil) }

    let known: Set<String> = ["parent-job", "child-job", "grandchild-job", "other-job", "unfiled-job"]
    let recording = LibraryDragPayload(kind: .recording, id: "child-job", sessionID: "persist-session")
    let start = try await store.load()
    let action = try LibraryDropRules.action(for: recording, target: .folder(b), organization: start, knownJobIDs: known, sessionID: "persist-session")
    try dragAction(action, recording: "child-job", destination: b, "persisted recording drop resolves intended assignment")
    if case .recording(let id, let folderID) = action { _ = try await store.assign(jobIDs: [id], folderID: folderID) }
    let filed = try await LibraryFolderStore(root: root).load()
    try check(filed.assignments == ["parent-job": a, "child-job": b, "grandchild-job": g, "other-job": b] && filed.folders == start.folders && filed.revision == start.revision + 1, "recording drop persists exactly one assignment without moving folders or other recordings")
    try await dragUnchanged("recording same-folder action skips persistence", root: root) {
        try dragAction(try LibraryDropRules.action(for: recording, target: .folder(b), organization: filed, knownJobIDs: known, sessionID: "persist-session"), "recording same-folder action remains nil")
    }
    let unfile = try LibraryDropRules.action(for: recording, target: .unfiled, organization: filed, knownJobIDs: known, sessionID: "persist-session")
    try dragAction(unfile, recording: "child-job", "recording unfiled action resolves nil folder")
    if case .recording(let id, let folderID) = unfile { _ = try await store.assign(jobIDs: [id], folderID: folderID) }
    let unfiled = try await LibraryFolderStore(root: root).load()
    try check(unfiled.assignments == ["parent-job": a, "grandchild-job": g, "other-job": b] && unfiled.folders == filed.folders && unfiled.revision == filed.revision + 1, "unfile drop durably removes only dragged recording assignment")
    try await dragUnchanged("recording unfiled noop skips persistence", root: root) {
        try dragAction(try LibraryDropRules.action(for: recording, target: .unfiled, organization: unfiled, knownJobIDs: known, sessionID: "persist-session"), "already-unfiled persisted recording returns nil")
    }
    try await dragUnchanged("deleted recording cannot recreate stale assignment", root: root) {
        try rejects("fresh known job list invalidates deleted recording payload") {
            _ = try LibraryDropRules.action(for: recording, target: .folder(c), organization: unfiled, knownJobIDs: known.subtracting(["child-job"]), sessionID: "persist-session")
        }
    }
    try await dragUnchanged("deleted recording destination rejects assignment atomically", root: root, rejects: true) {
        _ = try await store.assign(jobIDs: ["child-job"], folderID: "deleted-destination")
    }
    try check(try FileManager.default.contentsOfDirectory(atPath: root.path) == ["native"], "drag moves write logical metadata only, no recording artifacts")
}

private func dragQueryChecks(_ state: LibraryOrganization, parent: String, destination: String, child: String, grandchild: String) throws {
    let ids = ["parent-job", "child-job", "grandchild-job", "other-job", "unfiled-job"]
    let jobs = try ids.enumerated().map { index, id in
        try JSONDecoder().decode(Job.self, from: JSONSerialization.data(withJSONObject: ["id": id, "uuid": id, "original_title": "Meeting \(id)", "recorded_at": "2026-10-\(10 + index)", "status": "completed", "stage": "completed", "elapsed_seconds": 0, "title_status": "completed", "alignment_status": "completed"]))
    }
    func visible(_ filter: String, search: String = "") -> [String] {
        LibraryQuery.visible(jobs, assignments: state.assignments, filter: filter, search: search, index: ["child-job": "child-only transcript", "grandchild-job": "deep-only transcript"]).map(\.id)
    }
    try check(visible(parent) == ["parent-job"] && visible(destination) == ["other-job"], "after subtree move parents show only direct recording assignments")
    try check(visible(child) == ["child-job"] && visible(grandchild) == ["grandchild-job"], "after subtree move child and grandchild direct queries remain distinct")
    try check(visible(destination, search: "child-only").isEmpty && visible(child, search: "deep-only").isEmpty && visible(child, search: "child-only") == ["child-job"], "post-move transcript search cannot include descendants")
    try check(visible("unfiled") == ["unfiled-job"] && visible("all") == ["unfiled-job", "other-job", "grandchild-job", "child-job", "parent-job"], "post-move all/unfiled filters preserve date ordering and membership")
}

private func dragFreshStateChecks() async throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent("LibraryDragFreshChecks-" + UUID().uuidString)
    defer { try? FileManager.default.removeItem(at: root) }
    let store = LibraryFolderStore(root: root)
    let a = try await store.create(name: "Alpha").folders.last!.id
    let b = try await store.create(name: "Beta").folders.last!.id
    let source = try await store.create(name: "Original", parentID: a).folders.last!.id
    let destination = try await store.create(name: "Destination").folders.last!.id
    let payload = LibraryDragPayload(kind: .folder, id: source, sessionID: "fresh-session")
    func resolve(_ state: LibraryOrganization, _ target: LibraryDropTarget) throws -> LibraryDropAction? {
        try LibraryDropRules.action(for: payload, target: target, organization: state, knownJobIDs: [], sessionID: "fresh-session")
    }
    let stale = try await store.load()
    try dragAction(try resolve(stale, .folder(b)), folder: source, destination: b, "drag starts with valid original source parent")
    _ = try await LibraryFolderStore(root: root).rename(id: source, name: "Fresh name")
    let renamed = try await store.moveFolder(id: source, parentID: b)
    try check(renamed.folders.first(where: { $0.id == source }) == LibraryFolder(id: source, name: "Fresh name", parentID: b), "move rereads fresh name instead of restoring stale drag name")
    try await dragUnchanged("stale source already moved to target is noop", root: root) {
        let current = try await store.load()
        try dragAction(try resolve(current, .folder(b)), "rules use current source parent for noop")
        _ = try await store.moveFolder(id: source, parentID: b)
    }
    let back = try await store.moveFolder(id: source, parentID: a)
    try check(back.folders.first(where: { $0.id == source })?.parentID == a && back.folders.first(where: { $0.id == source })?.name == "Fresh name", "stale previously-moved source remains movable by stable ID")
    _ = try await LibraryFolderStore(root: root).create(name: "FRESH NAME", parentID: b)
    try await dragUnchanged("fresh destination collision invalidates stale approved move", root: root, rejects: true) { _ = try await store.moveFolder(id: source, parentID: b) }
    _ = try await LibraryFolderStore(root: root).delete(id: destination)
    try await dragUnchanged("deleted destination rejects current rules and store", root: root) {
        let current = try await store.load()
        try rejects("drop rules reread deleted destination") { _ = try resolve(current, .folder(destination)) }
        try await dragRejects("store rereads deleted destination") { _ = try await store.moveFolder(id: source, parentID: destination) }
    }
    _ = try await LibraryFolderStore(root: root).delete(id: source)
    try await dragUnchanged("deleted source rejects current rules and store", root: root) {
        let current = try await store.load()
        try rejects("drop rules reject deleted source") { _ = try resolve(current, .root) }
        try await dragRejects("store rejects deleted source") { _ = try await store.moveFolder(id: source, parentID: nil) }
    }

    // Drag payloads carry IDs, so a destination moved below the source after preview
    // must be rejected from the fresh graph instead of trusting the original preview.
    let branch = try await store.create(name: "Branch", parentID: a).folders.last!.id
    let leaf = try await store.create(name: "Leaf", parentID: branch).folders.last!.id
    let target = try await store.create(name: "Moving destination", parentID: b).folders.last!.id
    let branchPayload = LibraryDragPayload(kind: .folder, id: branch, sessionID: "fresh-session")
    let preview = try LibraryDropRules.action(for: branchPayload, target: .folder(target), organization: try await store.load(), knownJobIDs: [], sessionID: "fresh-session")
    try dragAction(preview, folder: branch, destination: target, "destination initially outside source subtree allows preview")
    _ = try await LibraryFolderStore(root: root).moveFolder(id: target, parentID: leaf)
    try await dragUnchanged("stale destination now deep descendant rejects", root: root) {
        let current = try await store.load()
        try rejects("rules reject newly moved descendant destination") { _ = try LibraryDropRules.action(for: branchPayload, target: .folder(target), organization: current, knownJobIDs: [], sessionID: "fresh-session") }
        try await dragRejects("store rejects newly moved descendant destination") { _ = try await store.moveFolder(id: branch, parentID: target) }
    }
}
