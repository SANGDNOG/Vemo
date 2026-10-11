import Foundation

public struct LibraryFolderTreeRow: Equatable, Sendable, Identifiable {
    public let folder: LibraryFolder
    public let depth: Int
    public let hasChildren: Bool
    public var id: String { folder.id }

    public init(folder: LibraryFolder, depth: Int, hasChildren: Bool) {
        self.folder = folder; self.depth = depth; self.hasChildren = hasChildren
    }
}

/// Iterative graph helpers also terminate safely for malformed, unvalidated input.
public enum LibraryFolderHierarchy {
    public static func rows(_ folders: [LibraryFolder], collapsedIDs: Set<String> = []) -> [LibraryFolderTreeRow] {
        let graph = indexed(folders)
        var children: [String: [LibraryFolder]] = [:]
        var roots: [LibraryFolder] = []
        for folder in graph.ordered {
            if let parent = folder.parentID, graph.byID[parent] != nil {
                children[parent, default: []].append(folder)
            } else { roots.append(folder) }
        }
        var visited = Set<String>()
        var result: [LibraryFolderTreeRow] = []
        // The second pass includes disconnected cycles; visited suppresses duplicate rows.
        for root in roots + graph.ordered where !visited.contains(root.id) {
            var stack: [(LibraryFolder, Int, Bool)] = [(root, 0, false)]
            while let (folder, depth, hidden) = stack.popLast() {
                guard visited.insert(folder.id).inserted else { continue }
                let descendants = children[folder.id] ?? []
                if !hidden {
                    result.append(LibraryFolderTreeRow(folder: folder, depth: depth, hasChildren: !descendants.isEmpty))
                }
                let hideChildren = hidden || collapsedIDs.contains(folder.id)
                for child in descendants.reversed() { stack.append((child, depth + 1, hideChildren)) }
            }
        }
        return result
    }

    public static func path(for id: String, in folders: [LibraryFolder]) -> String {
        let graph = indexed(folders)
        var names: [String] = []
        var visited = Set<String>()
        var current: String? = id
        while let node = current, visited.insert(node).inserted, let folder = graph.byID[node] {
            names.append(folder.name)
            current = folder.parentID
        }
        return names.reversed().joined(separator: " / ")
    }

    public static func descendantIDs(of id: String, in folders: [LibraryFolder]) -> Set<String> {
        let graph = indexed(folders)
        guard graph.byID[id] != nil else { return [] }
        var children: [String: [String]] = [:]
        for folder in graph.ordered {
            if let parent = folder.parentID { children[parent, default: []].append(folder.id) }
        }
        var visited: Set<String> = [id]
        var stack = children[id] ?? []
        while let node = stack.popLast() {
            guard visited.insert(node).inserted else { continue }
            stack.append(contentsOf: children[node] ?? [])
        }
        visited.remove(id)
        return visited
    }

    public static func parentCandidates(for id: String?, in folders: [LibraryFolder]) -> [LibraryFolderTreeRow] {
        var excluded = Set<String>()
        if let id { excluded = descendantIDs(of: id, in: folders); excluded.insert(id) }
        return rows(folders).filter { !excluded.contains($0.id) }
    }

    private static func indexed(_ folders: [LibraryFolder]) -> (ordered: [LibraryFolder], byID: [String: LibraryFolder]) {
        var byID: [String: LibraryFolder] = [:]
        var ordered: [LibraryFolder] = []
        for folder in folders where byID[folder.id] == nil {
            byID[folder.id] = folder
            ordered.append(folder)
        }
        return (ordered, byID)
    }
}
