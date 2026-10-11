import Foundation
import NativeCore

private func hierarchyRejects(_ name: String, _ action: () async throws -> Void) async throws {
    do { try await action() } catch { print("PASS: \(name)"); return }
    throw NativeFailure.invalid("FAIL: \(name)")
}

// Assert transaction boundaries from durable bytes and a fresh actor, not cached results.
private func hierarchyPreserves(_ name: String, root: URL, _ action: () async throws -> Void) async throws {
    let file = root.appendingPathComponent("native/library.json")
    let before = try Data(contentsOf: file)
    let state = try await LibraryFolderStore(root: root).load()
    try await hierarchyRejects(name, action)
    try check(try Data(contentsOf: file) == before, "\(name): exact bytes unchanged")
    try check(try await LibraryFolderStore(root: root).load() == state, "\(name): revision and state unchanged on restart")
}

private func hierarchyWrite(_ source: String, root: URL) throws {
    let file = root.appendingPathComponent("native/library.json")
    try FileManager.default.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
    try Data(source.utf8).write(to: file, options: .atomic)
}

func libraryHierarchyChecks() async throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent("LibraryHierarchyChecks-" + UUID().uuidString)
    defer { try? FileManager.default.removeItem(at: root) }
    try check(LibraryOrganization().schema_version == 2, "hierarchy defaults to schema two")
    try check(LibraryFolder(id: "legacy", name: "Legacy").parentID == nil, "folder initializer remains flat by default")
    let store = LibraryFolderStore(root: root)
    let empty = try await store.load()
    try check(empty.schema_version == 2 && empty.revision == 0 && empty.folders.isEmpty, "missing hierarchy loads empty schema two")
    try check(!FileManager.default.fileExists(atPath: root.appendingPathComponent("native/library.json").path), "hierarchy load remains read only")
    let work = try await store.create(name: "Work").folders.last!.id
    let personal = try await store.create(name: "Personal").folders.last!.id
    let child = try await store.create(name: "  Projects  ", parentID: work).folders.last!.id
    let grandchild = try await store.create(name: "Research", parentID: child).folders.last!.id
    let same = try await store.create(name: "Projects", parentID: personal).folders.last!.id
    let topSame = try await store.create(name: "Projects").folders.last!.id
    var state = try await store.load()
    try check(state.revision == 6 && state.folders.first(where: { $0.id == child })?.parentID == work && state.folders.first(where: { $0.id == grandchild })?.parentID == child, "create persists child and grandchild parent links")
    try check(state.folders.filter { $0.name == "Projects" }.count == 3, "same names allowed across distinct parents including root")
    try check(try await LibraryFolderStore(root: root).load() == state, "nested graph survives restart")
    let persisted = try JSONSerialization.jsonObject(with: Data(contentsOf: root.appendingPathComponent("native/library.json"))) as! [String: Any]
    let persistedFolders = persisted["folders"] as! [[String: Any]]
    try check(Set(persisted.keys) == ["schema_version", "revision", "folders", "assignments"] && persistedFolders.first(where: { $0["id"] as? String == grandchild })?["parentID"] as? String == child, "schema two persists explicit parentID inside logical metadata")
    try check(!FileManager.default.fileExists(atPath: root.appendingPathComponent("native/library-v1-backup.json").path), "fresh schema two does not fabricate v1 backup")

    // Interleave input order and children before their ancestors to exercise actual traversal.
    let graph = [
        LibraryFolder(id: "g", name: "Grand", parentID: "c"),
        LibraryFolder(id: "z", name: "Zed"),
        LibraryFolder(id: "b", name: "Second", parentID: "z"),
        LibraryFolder(id: "a", name: "Alpha"),
        LibraryFolder(id: "c", name: "Child", parentID: "z"),
        LibraryFolder(id: "x", name: "Else", parentID: "a")
    ]
    let rows = LibraryFolderHierarchy.rows(graph)
    try check(rows.map(\.id) == ["z", "b", "c", "g", "a", "x"], "tree uses stable preorder with interleaved input")
    try check(rows.map(\.depth) == [0, 1, 1, 2, 0, 1], "tree depths reflect ancestors")
    try check(rows.map(\.hasChildren) == [true, false, true, false, true, false], "tree child flags reflect graph")
    try check(rows.map(\.folder.id) == rows.map(\.id), "tree rows retain folder identity")
    try check(rows.map(\.folder) == [graph[1], graph[2], graph[4], graph[0], graph[3], graph[5]], "tree rows preserve full folder values")
    try check(LibraryFolderHierarchy.rows(graph, collapsedIDs: ["z"]).map(\.id) == ["z", "a", "x"], "collapsed root hides entire subtree but retains root")
    try check(LibraryFolderHierarchy.rows(graph, collapsedIDs: ["c", "missing"]).map(\.id) == ["z", "b", "c", "a", "x"], "collapsed nested branch hides only descendants")
    try check(LibraryFolderHierarchy.rows(graph, collapsedIDs: ["g"]).map(\.id) == rows.map(\.id), "collapsing a leaf leaves preorder unchanged")
    try check(LibraryFolderHierarchy.path(for: "g", in: graph) == "Zed / Child / Grand", "folder path joins full ancestry")
    try check(LibraryFolderHierarchy.path(for: "a", in: graph) == "Alpha", "root path has no separator")
    try check(Set(LibraryFolderHierarchy.descendantIDs(of: "z", in: graph)) == ["b", "c", "g"], "descendants exclude self and unrelated branches")
    try check(LibraryFolderHierarchy.descendantIDs(of: "g", in: graph).isEmpty, "leaf has no descendants")
    try check(LibraryFolderHierarchy.descendantIDs(of: "missing", in: graph).isEmpty, "missing folder has no descendants")
    try check(LibraryFolderHierarchy.parentCandidates(for: "z", in: graph).map(\.id) == ["a", "x"], "parent picker excludes self and all descendants")
    try check(LibraryFolderHierarchy.parentCandidates(for: "c", in: graph).map(\.id) == ["z", "b", "a", "x"], "parent picker retains ancestors and unrelated siblings")
    try check(LibraryFolderHierarchy.parentCandidates(for: nil, in: graph).map(\.id) == rows.map(\.id), "new folder picker offers all folders in tree order")
    try check(LibraryFolderHierarchy.parentCandidates(for: "g", in: graph).map(\.id) == ["z", "b", "c", "a", "x"], "leaf editor excludes only itself")
    try check(LibraryFolderHierarchy.rows([]).isEmpty && LibraryFolderHierarchy.parentCandidates(for: nil, in: []).isEmpty, "empty hierarchy has no rows or parent choices")

    state = try await store.assign(jobIDs: ["at-root"], folderID: work)
    state = try await store.assign(jobIDs: ["at-child"], folderID: child)
    state = try await store.assign(jobIDs: ["at-grandchild"], folderID: grandchild)
    state = try await store.assign(jobIDs: ["at-other"], folderID: same)
    let assignments = state.assignments
    let beforeMoveRevision = state.revision
    state = try await store.update(id: child, name: "Moved", parentID: personal)
    try check(state.revision == beforeMoveRevision + 1 && state.folders.first(where: { $0.id == child })?.name == "Moved" && state.folders.first(where: { $0.id == child })?.parentID == personal, "combined rename and reparent commits once")
    try check(state.assignments == assignments && state.folders.first(where: { $0.id == grandchild })?.parentID == child, "move retains job assignments and subtree links")
    try check(LibraryFolderHierarchy.path(for: grandchild, in: state.folders) == "Personal / Moved / Research", "move updates descendant full path")
    state = try await store.rename(id: child, name: "Renamed")
    try check(state.folders.first(where: { $0.id == child })?.parentID == personal && state.assignments == assignments, "legacy rename preserves parent and assignments")
    state = try await store.update(id: child, name: "Detached", parentID: nil)
    try check(state.folders.first(where: { $0.id == child })?.parentID == nil && state.folders.first(where: { $0.id == grandchild })?.parentID == child && state.assignments == assignments, "move to root retains subtree and assignments")

    try await hierarchyPreserves("self-parent move rejects", root: root) { _ = try await store.update(id: child, name: "Self", parentID: child) }
    try await hierarchyPreserves("descendant-parent move rejects", root: root) { _ = try await store.update(id: child, name: "Cycle", parentID: grandchild) }
    try await hierarchyPreserves("missing parent move rejects", root: root) { _ = try await store.update(id: child, name: "No parent", parentID: "absent") }
    try await hierarchyPreserves("missing parent create rejects", root: root) { _ = try await store.create(name: "Ghost", parentID: "absent") }
    try await hierarchyPreserves("empty parent ID is not treated as root", root: root) { _ = try await store.create(name: "Ghost", parentID: "") }
    try await hierarchyPreserves("missing update target rejects", root: root) { _ = try await store.update(id: "absent", name: "Ghost", parentID: work) }
    try await hierarchyPreserves("failed combined duplicate update is atomic", root: root) { _ = try await store.update(id: child, name: "PROJECTS", parentID: personal) }
    try await hierarchyPreserves("failed combined invalid name update is atomic", root: root) { _ = try await store.update(id: child, name: "bad\nname", parentID: personal) }
    // The original child moved out of Work; recreate a sibling before checking uniqueness.
    _ = try await store.create(name: "Projects", parentID: work)
    try await hierarchyPreserves("sibling duplicate create rejects", root: root) { _ = try await store.create(name: " PROJECTS ", parentID: work) }
    // Root Projects remains present, so moving its same-name counterpart to root must fail.
    try await hierarchyPreserves("move name collision at root rejects", root: root) { _ = try await store.update(id: same, name: "Projects", parentID: nil) }
    _ = try await store.create(name: "Café", parentID: work)
    try await hierarchyPreserves("Unicode normalized sibling create rejects", root: root) { _ = try await store.create(name: "Cafe\u{0301}", parentID: work) }
    try await hierarchyPreserves("Unicode normalized sibling update rejects", root: root) { _ = try await store.update(id: same, name: "CAFE\u{0301}", parentID: work) }
    let otherCafe = try await store.create(name: "Cafe\u{0301}", parentID: personal)
    try check(otherCafe.folders.last?.name == "Café" && otherCafe.folders.last?.parentID == personal, "Unicode normalized names remain allowed under different parents")
    try await hierarchyPreserves("nonleaf delete rejects", root: root) { _ = try await store.delete(id: child) }
    state = try await store.delete(id: grandchild)
    try check(!state.folders.contains(where: { $0.id == grandchild }) && state.assignments["at-grandchild"] == nil && state.assignments["at-child"] == child && state.assignments["at-other"] == same, "leaf deletion unfiles only direct assignments")
    state = try await store.delete(id: child)
    try check(state.assignments["at-child"] == nil && state.assignments["at-root"] == work && state.assignments["at-other"] == same && state.folders.contains(where: { $0.id == topSame }), "former parent becomes deletable after last child removal")

    // A valid externally replaced v2 graph must also be reread rather than overwritten.
    let external = root.appendingPathComponent("external-v2")
    let externalStore = LibraryFolderStore(root: external)
    _ = try await externalStore.create(name: "Cached")
    try hierarchyWrite(#"{"schema_version":2,"revision":80,"folders":[{"id":"parent","name":"External"},{"id":"nested","name":"Child","parentID":"parent"}],"assignments":{"outside-job":"nested"}}"#, root: external)
    let reread = try await externalStore.create(name: "New", parentID: "nested")
    try check(reread.revision == 81 && reread.assignments == ["outside-job": "nested"] && reread.folders.map(\.id).prefix(2) == ["parent", "nested"] && reread.folders.last?.parentID == "nested", "mutation rereads external schema two subtree and revision")

    try hierarchyDirectQueryChecks(parent: personal, child: same)
    try await hierarchyMigrationChecks(root: root)
    try await hierarchyInvalidFileChecks(root: root)
    try await hierarchyConcurrentChecks(root: root)
}

private func hierarchyDirectQueryChecks(parent: String, child: String) throws {
    func job(_ id: String, _ title: String, _ date: String) throws -> Job {
        try JSONDecoder().decode(Job.self, from: JSONSerialization.data(withJSONObject: ["id": id, "uuid": id, "original_title": title, "recorded_at": date, "status": "completed", "stage": "completed", "elapsed_seconds": 0, "title_status": "completed", "alignment_status": "completed"]))
    }
    let jobs = [try job("p", "Parent meeting", "2026-10-09"), try job("c", "Child meeting", "2026-10-11"), try job("u", "Unfiled", "2026-10-10")]
    let assignments = ["p": parent, "c": child]
    let index = ["p": "parent 한국어", "c": "child 한국어"]
    func visible(_ filter: String, _ search: String) -> [String] {
        LibraryQuery.visible(jobs, assignments: assignments, filter: filter, search: search, index: index).map(\.id)
    }
    try check(visible(parent, "") == ["p"] && visible(child, "") == ["c"], "nested filters remain direct assignments only")
    try check(visible(parent, "meeting") == ["p"] && visible(parent, "한국어") == ["p"], "parent title and transcript search stays direct only")
    try check(visible(parent, "child").isEmpty && visible(child, "parent").isEmpty, "search cannot leak across parent child assignments")
    try check(visible("all", "meeting") == ["c", "p"] && visible("unfiled", "") == ["u"], "all and unfiled semantics and date ordering survive nested folders")
}

private func hierarchyMigrationChecks(root: URL) async throws {
    let source = #"{"schema_version":1,"revision":7,"folders":[{"id":"old","name":"Legacy"},{"id":"other","name":"Other"}],"assignments":{"legacy-job":"old"}}"# + "\n"
    for operation in ["create", "rename", "update", "assign", "delete"] {
        let destination = root.appendingPathComponent("migration-" + operation)
        try hierarchyWrite(source, root: destination)
        let store = LibraryFolderStore(root: destination)
        let file = destination.appendingPathComponent("native/library.json")
        let backup = destination.appendingPathComponent("native/library-v1-backup.json")
        let old = try await store.load()
        try check(old.schema_version == 1 && old.revision == 7 && old.folders.allSatisfy({ $0.parentID == nil }) && old.assignments == ["legacy-job": "old"], "\(operation): v1 flat load retains state")
        try check(try Data(contentsOf: file) == Data(source.utf8) && !FileManager.default.fileExists(atPath: backup.path), "\(operation): v1 load neither writes nor backs up")
        try await hierarchyPreserves("\(operation): invalid v1 mutation does not upgrade", root: destination) { _ = try await store.create(name: "LEGACY") }
        try check(!FileManager.default.fileExists(atPath: backup.path), "\(operation): rejected v1 mutation creates no backup")
        let changed: LibraryOrganization
        switch operation {
        case "create": changed = try await store.create(name: "Nested", parentID: "old")
        case "rename": changed = try await store.rename(id: "old", name: "Renamed")
        case "update": changed = try await store.update(id: "old", name: "Moved", parentID: "other")
        case "assign": changed = try await store.assign(jobIDs: ["new-job"], folderID: "old")
        default: changed = try await store.delete(id: "other")
        }
        try check(changed.schema_version == 2 && changed.revision == 8, "\(operation): first successful v1 mutation upgrades once")
        try check(try Data(contentsOf: backup) == Data(source.utf8), "\(operation): migration backs up exact original v1 bytes")
        let restarted = try await LibraryFolderStore(root: destination).load()
        try check(changed.assignments["legacy-job"] == "old" && restarted == changed, "\(operation): upgraded state and assignments survive restart")
        _ = try await store.create(name: "Later")
        try check(try Data(contentsOf: backup) == Data(source.utf8), "\(operation): later v2 writes retain original backup")
        // Reintroduce another v1 file to ensure migration never overwrites earlier history.
        let replacement = source.replacingOccurrences(of: "\"revision\":7", with: "\"revision\":40")
        try hierarchyWrite(replacement, root: destination)
        let replaced = try await store.create(name: "Replacement")
        try check(replaced.revision == 41 && replaced.schema_version == 2 && (try Data(contentsOf: backup)) == Data(source.utf8), "\(operation): external v1 replacement reread preserves existing backup")
    }
    // A backup write failure must not upgrade or mutate the v1 library.
    let blocked = root.appendingPathComponent("blocked-backup")
    try hierarchyWrite(source, root: blocked)
    let backup = blocked.appendingPathComponent("native/library-v1-backup.json")
    try FileManager.default.createDirectory(at: backup, withIntermediateDirectories: true)
    try Data("keep".utf8).write(to: backup.appendingPathComponent("sentinel"))
    try await hierarchyPreserves("backup obstruction prevents migration", root: blocked) { _ = try await LibraryFolderStore(root: blocked).create(name: "New") }
    try check(try Data(contentsOf: backup.appendingPathComponent("sentinel")) == Data("keep".utf8), "migration failure preserves backup obstruction")
}

private func hierarchyInvalidFileChecks(root: URL) async throws {
    let cases: [(String, String)] = [
        ("cycle", #"{"schema_version":2,"revision":9,"folders":[{"id":"a","name":"A","parentID":"b"},{"id":"b","name":"B","parentID":"a"}],"assignments":{}}"#),
        ("self parent", #"{"schema_version":2,"revision":9,"folders":[{"id":"a","name":"A","parentID":"a"}],"assignments":{}}"#),
        ("missing parent", #"{"schema_version":2,"revision":9,"folders":[{"id":"a","name":"A","parentID":"missing"}],"assignments":{}}"#),
        ("future schema", #"{"schema_version":3,"revision":9,"folders":[],"assignments":{}}"#),
        ("v1 nested graph", #"{"schema_version":1,"revision":9,"folders":[{"id":"a","name":"A"},{"id":"b","name":"B","parentID":"a"}],"assignments":{}}"#),
        ("normalized sibling duplicate", #"{"schema_version":2,"revision":9,"folders":[{"id":"p","name":"Parent"},{"id":"a","name":"Café","parentID":"p"},{"id":"b","name":"CAFE\u0301","parentID":"p"}],"assignments":{}}"#),
        ("duplicate ID", #"{"schema_version":2,"revision":9,"folders":[{"id":"a","name":"A"},{"id":"a","name":"B"}],"assignments":{}}"#),
        ("dangling assignment", #"{"schema_version":2,"revision":9,"folders":[],"assignments":{"job":"gone"}}"#),
        ("corrupt JSON", "{broken\n")
    ]
    for (name, source) in cases {
        let destination = root.appendingPathComponent("invalid-" + name.replacingOccurrences(of: " ", with: "-"))
        try hierarchyWrite(source, root: destination)
        let store = LibraryFolderStore(root: destination)
        let file = destination.appendingPathComponent("native/library.json")
        try await hierarchyRejects("\(name): load rejects") { _ = try await store.load() }
        try await hierarchyRejects("\(name): create rejects") { _ = try await store.create(name: "New") }
        try await hierarchyRejects("\(name): combined update rejects") { _ = try await store.update(id: "a", name: "New", parentID: nil) }
        try await hierarchyRejects("\(name): rename rejects") { _ = try await store.rename(id: "a", name: "New") }
        try await hierarchyRejects("\(name): delete rejects") { _ = try await store.delete(id: "a") }
        try await hierarchyRejects("\(name): assignment rejects") { _ = try await store.assign(jobIDs: ["job"], folderID: nil) }
        try check(try Data(contentsOf: file) == Data(source.utf8), "\(name): all failed operations preserve bytes")
        try check(!FileManager.default.fileExists(atPath: destination.appendingPathComponent("native/library-v1-backup.json").path), "\(name): rejection creates no migration backup")
    }
    for version in [1, 2] {
        let destination = root.appendingPathComponent("exhausted-\(version)")
        try hierarchyWrite("{\"schema_version\":\(version),\"revision\":\(Int.max),\"folders\":[{\"id\":\"a\",\"name\":\"A\"}],\"assignments\":{}}", root: destination)
        let store = LibraryFolderStore(root: destination)
        try await hierarchyPreserves("v\(version) revision guard rejects create", root: destination) { _ = try await store.create(name: "New", parentID: "a") }
        try await hierarchyPreserves("v\(version) revision guard rejects update", root: destination) { _ = try await store.update(id: "a", name: "New", parentID: nil) }
        try check(!FileManager.default.fileExists(atPath: destination.appendingPathComponent("native/library-v1-backup.json").path), "v\(version) revision exhaustion creates no backup")
    }
}

private func hierarchyConcurrentChecks(root: URL) async throws {
    let destination = root.appendingPathComponent("concurrent-hierarchy")
    let store = LibraryFolderStore(root: destination)
    let parent = try await store.create(name: "Parent").folders.last!.id
    let successes = await withTaskGroup(of: Bool.self, returning: Int.self) { group in
        for index in 0..<24 {
            group.addTask {
                do { _ = try await store.create(name: index.isMultiple(of: 2) ? "Café" : "CAFE\u{0301}", parentID: parent); return true }
                catch { return false }
            }
        }
        var count = 0
        for await success in group { if success { count += 1 } }
        return count
    }
    let duplicateState = try await LibraryFolderStore(root: destination).load()
    try check(successes == 1 && duplicateState.folders.count == 2 && duplicateState.revision == 2, "concurrent normalized sibling duplicate has exactly one durable winner")
    try await withThrowingTaskGroup(of: Void.self) { group in
        for index in 0..<24 {
            group.addTask { _ = try await store.create(name: "Child \(index)", parentID: parent) }
        }
        try await group.waitForAll()
    }
    let final = try await LibraryFolderStore(root: destination).load()
    try check(final.revision == 26 && final.folders.count == 26 && Set(final.folders.map(\.id)).count == 26 && final.folders.filter({ $0.parentID == parent }).count == 25, "serialized concurrent unique child creates survive restart without lost updates")
    try check(LibraryFolderHierarchy.rows(final.folders).count == 26 && LibraryFolderHierarchy.rows(final.folders, collapsedIDs: [parent]).count == 1, "concurrent durable children form one collapsible subtree")
}
