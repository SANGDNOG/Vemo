import Foundation
import NativeCore

private func folderRejects(_ name: String, _ action: () async throws -> Void) async throws {
    do { try await action() } catch { print("PASS: \(name)"); return }
    throw NativeFailure.invalid("FAIL: \(name)")
}

func libraryFolderChecks() async throws {
    try await libraryFolderSubtreeDeletionChecks()
    let root = FileManager.default.temporaryDirectory.appendingPathComponent("LibraryFolderChecks-" + UUID().uuidString)
    defer { try? FileManager.default.removeItem(at: root) }
    let file = root.appendingPathComponent("native/library.json")
    let store = LibraryFolderStore(root: root)
    let empty = try await store.load()
    try check(empty.schema_version == 2 && empty.revision == 0 && empty.folders.isEmpty && empty.assignments.isEmpty,
              "missing library loads empty version two without writing")
    try check(!FileManager.default.fileExists(atPath: file.path), "load does not create library")
    let created = try await store.create(name: "  Work  ")
    try check(created.revision == 1 && created.folders.count == 1 && created.folders.first?.name == "Work",
              "create persists trimmed folder and increments revision")
    let work = created.folders[0].id
    try check(!work.isEmpty, "folder has stable nonempty string ID")
    let personal = try await store.create(name: "Personal")
    let other = personal.folders[1].id
    let assigned = try await store.assign(jobIDs: ["job:not-memo-uuid", "job-two", "job:not-memo-uuid"], folderID: work)
    try check(assigned.assignments == ["job:not-memo-uuid": work, "job-two": work] && assigned.revision == 3,
              "batch assignment keys are exact job IDs and commits once")
    let restarted = LibraryFolderStore(root: root)
    try check(try await restarted.load() == assigned, "new store restores folders and assignments after restart")
    let renamed = try await restarted.rename(id: work, name: "Projects")
    try check(renamed.folders.first?.id == work && renamed.folders.first?.name == "Projects" && renamed.assignments == assigned.assignments,
              "rename preserves folder identity and recording assignments")
    _ = try await restarted.assign(jobIDs: ["job-two"], folderID: other)
    let deleted = try await restarted.delete(id: work)
    try check(deleted.folders.map(\.id) == [other] && deleted.assignments == ["job-two": other],
              "delete unfiles only recordings assigned to removed folder")
    let unfiled = try await restarted.assign(jobIDs: ["job-two"], folderID: nil)
    try check(unfiled.assignments.isEmpty, "nil destination unfiles job")
    let bytes = try Data(contentsOf: file)
    for name in ["", "   ", "bad\nname", "bad\u{0000}name", String(repeating: "x", count: 101)] {
        try await folderRejects("invalid folder name rejected") { _ = try await restarted.create(name: name) }
        try await folderRejects("invalid rename rejected") { _ = try await restarted.rename(id: other, name: name) }
    }
    try await folderRejects("case insensitive duplicate rejected") { _ = try await restarted.create(name: " PERSONAL ") }
    _ = try await restarted.create(name: "Café")
    try await folderRejects("canonical Unicode duplicate rejected") { _ = try await restarted.create(name: "Cafe\u{0301}") }
    try await folderRejects("duplicate rename rejected") { _ = try await restarted.rename(id: other, name: "CAFÉ") }
    let beforeInvalid = try Data(contentsOf: file)
    try await folderRejects("unknown assignment folder rejected as a whole") { _ = try await restarted.assign(jobIDs: ["one", "two"], folderID: "unknown") }
    try await folderRejects("unknown rename rejected") { _ = try await restarted.rename(id: "unknown", name: "New") }
    try await folderRejects("unknown deletion rejected") { _ = try await restarted.delete(id: "unknown") }
    try check(try Data(contentsOf: file) == beforeInvalid, "failed mutations preserve exact durable bytes")
    try check(bytes != beforeInvalid, "successful mutations persisted")

    // Every mutation must reread: valid replacement cannot be overwritten by cached state.
    let external = Data(#"{"schema_version":1,"revision":80,"folders":[{"id":"external","name":"External"}],"assignments":{"external-job":"external"}}"#.utf8)
    try external.write(to: file, options: .atomic)
    let updated = try await store.create(name: "After replacement")
    try check(updated.revision == 81 && updated.assignments == ["external-job": "external"] && updated.folders.first?.id == "external",
              "mutation rereads valid external replacement")
    let badStates = [
        "{broken json\n",
        #"{"schema_version":3,"revision":0,"folders":[],"assignments":{}}"#,
        #"{"schema_version":1,"revision":-1,"folders":[],"assignments":{}}"#,
        #"{"schema_version":1,"revision":0,"folders":[{"id":"same","name":"A"},{"id":"same","name":"B"}],"assignments":{}}"#,
        #"{"schema_version":1,"revision":0,"folders":[{"id":"a","name":"A"},{"id":"b","name":"a"}],"assignments":{}}"#,
        #"{"schema_version":1,"revision":0,"folders":[{"id":"a","name":""}],"assignments":{}}"#,
        #"{"schema_version":1,"revision":0,"folders":[],"assignments":{"job":"missing"}}"#,
        #"{"schema_version":1,"revision":9223372036854775807,"folders":[],"assignments":{}}"#
    ]
    for source in badStates {
        let bad = Data(source.utf8)
        try bad.write(to: file, options: .atomic)
        if !source.contains("9223372036854775807") {
            try await folderRejects("bad/unsupported library exposes load error") { _ = try await store.load() }
        }
        try await folderRejects("bad library cannot be overwritten by create") { _ = try await store.create(name: "New") }
        try await folderRejects("bad library cannot be overwritten by rename") { _ = try await store.rename(id: "external", name: "New") }
        try await folderRejects("bad library cannot be overwritten by delete") { _ = try await store.delete(id: "external") }
        try await folderRejects("bad library cannot be overwritten by assignment") { _ = try await store.assign(jobIDs: ["new-job"], folderID: nil) }
        try check(try Data(contentsOf: file) == bad, "bad library preserved byte for byte")
    }
    // A real filesystem write failure must not return a successful in-memory mutation.
    let blocked = root.appendingPathComponent("blocked")
    try FileManager.default.createDirectory(at: blocked, withIntermediateDirectories: true)
    try Data("obstruction".utf8).write(to: blocked.appendingPathComponent("native"))
    let blockedStore = LibraryFolderStore(root: blocked)
    try await folderRejects("non-directory native path reports write failure") { _ = try await blockedStore.create(name: "Blocked") }
    try check(try Data(contentsOf: blocked.appendingPathComponent("native")) == Data("obstruction".utf8), "write failure preserves obstructing file")

    let concurrentRoot = root.appendingPathComponent("concurrent")
    let concurrent = LibraryFolderStore(root: concurrentRoot)
    try await withThrowingTaskGroup(of: Void.self) { group in
        for index in 0..<32 {
            group.addTask { _ = try await concurrent.create(name: "Folder \(index)") }
        }
        try await group.waitForAll()
    }
    let all = try await LibraryFolderStore(root: concurrentRoot).load()
    try check(all.folders.count == 32 && Set(all.folders.map(\.id)).count == 32 && all.revision == 32,
              "concurrent actor mutations survive restart without lost updates")
    let destination = all.folders[0].id
    try await withThrowingTaskGroup(of: Void.self) { group in
        for index in 0..<32 {
            group.addTask { _ = try await concurrent.assign(jobIDs: ["synthetic-job-\(index)"], folderID: destination) }
        }
        try await group.waitForAll()
    }
    let final = try await concurrent.load()
    try check(final.assignments.count == 32 && final.revision == 64 && Set(final.assignments.values) == [destination],
              "concurrent assignment batches preserve all job IDs")
    let object = try JSONSerialization.jsonObject(with: Data(contentsOf: concurrentRoot.appendingPathComponent("native/library.json"))) as! [String: Any]
    try check(Set(object.keys) == ["schema_version", "revision", "folders", "assignments"], "library uses exact versioned public JSON fields")
    let children = try FileManager.default.contentsOfDirectory(atPath: concurrentRoot.path)
    try check(children == ["native"], "logical organization writes only native state and no recording artifacts")
}

private func libraryFolderSubtreeDeletionChecks() async throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent("LibrarySubtreeChecks-" + UUID().uuidString)
    defer { try? FileManager.default.removeItem(at: root) }
    let file = root.appendingPathComponent("native/library.json")
    try FileManager.default.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
    // Children precede parents; the removed branch has siblings and a grandchild.
    let folders = [
        LibraryFolder(id: "grandchild", name: "Grandchild", parentID: "child"),
        LibraryFolder(id: "peer-child", name: "Child", parentID: "peer"),
        LibraryFolder(id: "child", name: "Child", parentID: "branch"),
        LibraryFolder(id: "sibling", name: "Sibling", parentID: "branch"),
        LibraryFolder(id: "branch", name: "Branch", parentID: "ancestor"),
        LibraryFolder(id: "peer", name: "Peer", parentID: "ancestor"),
        LibraryFolder(id: "ancestor", name: "Ancestor"),
        LibraryFolder(id: "other-root", name: "Other")
    ]
    let original = LibraryOrganization(revision: 40, folders: folders, assignments: [
        "branch-job": "branch", "child-job": "child", "grandchild-job": "grandchild", "sibling-job": "sibling",
        "ancestor-job": "ancestor", "peer-job": "peer", "peer-child-job": "peer-child", "other-job": "other-root"
    ])
    try JSONEncoder().encode(original).write(to: file, options: .atomic)
    // Synthetic recording/output sentinels: unfiling must preserve actual artifacts.
    let artifacts = ["recordings/child.m4a", "outputs/grandchild/transcript.txt", "outputs/grandchild/notes.md"]
    for path in artifacts {
        let url = root.appendingPathComponent(path)
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data(("synthetic " + path).utf8).write(to: url)
    }
    let store = LibraryFolderStore(root: root)
    let deleted = try await store.delete(id: "branch")
    let expected = LibraryOrganization(revision: 41, folders: [folders[1], folders[5], folders[6], folders[7]], assignments: [
        "ancestor-job": "ancestor", "peer-job": "peer", "peer-child-job": "peer-child", "other-job": "other-root"
    ])
    try check(deleted == expected, "subtree deletion removes all descendants and assignments in one revision, preserving ancestors and peers")
    try check(try JSONDecoder().decode(LibraryOrganization.self, from: Data(contentsOf: file)) == expected,
              "subtree deletion durably commits folders and assignments together")
    try check(try await LibraryFolderStore(root: root).load() == expected, "subtree deletion survives a fresh store restart")
    for path in artifacts {
        try check(try Data(contentsOf: root.appendingPathComponent(path)) == Data(("synthetic " + path).utf8),
                  "subtree deletion preserves synthetic recording/transcript/notes bytes")
    }
    let bytes = try Data(contentsOf: file)
    for id in ["missing", "", "branch", "grandchild"] {
        do {
            _ = try await store.delete(id: id)
            throw NativeFailure.invalid("FAIL: unknown/deleted subtree deletion must reject")
        } catch LibraryFolderStoreError.unknownFolder(let rejected) {
            try check(rejected == id, "unknown/deleted subtree target reports its ID")
        }
        try check(try Data(contentsOf: file) == bytes, "unknown/deleted subtree target preserves durable bytes and revision")
    }
    let rootDeleted = try await store.delete(id: "ancestor")
    try check(rootDeleted == LibraryOrganization(revision: 42, folders: [folders[7]], assignments: ["other-job": "other-root"]),
              "root deletion removes remaining descendant branches while preserving unrelated root")
    try check(try await LibraryFolderStore(root: root).load() == rootDeleted, "root subtree deletion survives restart")
}
