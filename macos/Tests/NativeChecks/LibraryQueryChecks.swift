import Foundation
import NativeCore

func libraryQueryChecks() throws {
    func job(_ id: String, _ title: String, _ date: String) throws -> Job {
        try JSONDecoder().decode(Job.self, from: JSONSerialization.data(withJSONObject: ["id":id, "uuid":id, "original_title":title, "recorded_at":date, "status":"completed", "stage":"completed", "elapsed_seconds":0, "title_status":"completed", "alignment_status":"completed"]))
    }
    let jobs = [try job("a", "Lecture", "2026-10-10"), try job("b", "Meeting", "2026-10-11"), try job("c", "Other", "2026-10-09")]
    func ids(_ filter: String, _ search: String, _ expected: [String]) throws {
        let actual = LibraryQuery.visible(jobs, assignments: ["a":"f", "b":"f"], filter: filter, search: search, index: ["a":"database 한국어", "b":"API"]).map(\.id)
        try check(actual == expected, "library filter \(filter)/\(search) stays scoped and date ordered")
    }
    try ids("all", "", ["b", "a", "c"])
    try ids("unfiled", "", ["c"])
    try ids("f", "", ["b", "a"])
    try ids("f", "한국어", ["a"])
    try ids("f", "MEETING", ["b"])
    try ids("unknown", "", [])
    try ids("unfiled", "API", [])
}
