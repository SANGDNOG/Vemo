import Foundation

/// Folder metadata only limits the library view; it never changes an engine job.
public enum LibraryQuery {
    public static func visible(_ jobs: [Job], assignments: [String: String], filter: String, search: String, index: [String: String]) -> [Job] {
        jobs.filter { job in
            guard (job.deleted_at != nil) == (filter == "trash") else { return false }
            let inFolder = filter == "trash" || filter == "all" || (filter == "unfiled" ? assignments[job.id] == nil : assignments[job.id] == filter)
            return inFolder && (search.isEmpty || job.title.localizedCaseInsensitiveContains(search) || (index[job.id] ?? "").localizedCaseInsensitiveContains(search))
        }.sorted {
            let a = $0.recorded_at ?? "", b = $1.recorded_at ?? ""
            return a == b ? $0.id < $1.id : a > b
        }
    }
}
