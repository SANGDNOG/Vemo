import Foundation

public enum ZIPFolderLayout {
    public static func components(jobID: String, organization: LibraryOrganization) throws -> [String] {
        guard [1,2].contains(organization.schema_version), organization.revision >= 0 else {
            throw LibraryFolderStoreError.invalidOrganization
        }
        var byID: [String:LibraryFolder] = [:]
        var siblings: [String?:[LibraryFolder]] = [:]
        for folder in organization.folders {
            guard !folder.id.isEmpty, byID[folder.id] == nil,
                  organization.schema_version == 2 || folder.parentID == nil else { throw LibraryFolderStoreError.invalidOrganization }
            byID[folder.id] = folder; siblings[folder.parentID,default:[]].append(folder)
        }
        guard organization.folders.allSatisfy({ $0.parentID == nil || byID[$0.parentID!] != nil }),
              organization.assignments.values.allSatisfy({ byID[$0] != nil }) else { throw LibraryFolderStoreError.invalidOrganization }
        var finished = Set<String>()
        for folder in organization.folders {
            var chain = Set<String>(), current: String? = folder.id
            while let id = current, !finished.contains(id) {
                guard chain.insert(id).inserted else { throw LibraryFolderStoreError.invalidOrganization }
                current = byID[id]?.parentID
            }
            finished.formUnion(chain)
        }
        var mapped: [String:String] = [:]
        for children in siblings.values {
            var used = Set<String>()
            for folder in children.sorted(by:{ $0.id < $1.id }) {
                let base = clean(folder.name)
                var ordinal = 1, component = base
                while !used.insert(key(component)).inserted {
                    ordinal += 1
                    let suffix = " (\(ordinal))"
                    component = bounded(base,bytes:120 - suffix.utf8.count) + suffix
                }
                mapped[folder.id] = component
            }
        }
        var result: [String] = [], current = organization.assignments[jobID]
        while let id = current {
            result.append(mapped[id]!); current = byID[id]?.parentID
        }
        return result.reversed()
    }

    private static func key(_ value: String) -> String {
        value.precomposedStringWithCanonicalMapping.folding(options:.caseInsensitive,locale:Locale(identifier:"en_US_POSIX"))
    }
    private static func clean(_ value: String) -> String {
        let forbidden = CharacterSet.controlCharacters.union(CharacterSet(charactersIn:"/\\:*?\"<>|"))
        let text = value.precomposedStringWithCanonicalMapping.unicodeScalars.map { forbidden.contains($0) ? "_" : String($0) }.joined()
            .trimmingCharacters(in:CharacterSet.whitespacesAndNewlines.union(CharacterSet(charactersIn:".")))
        let result = bounded(text,bytes:120)
        return result.isEmpty ? "폴더" : result
    }
    private static func bounded(_ value: String, bytes: Int) -> String {
        var result = ""
        for character in value {
            guard result.utf8.count + String(character).utf8.count <= bytes else { break }
            result.append(character)
        }
        return result
    }
}
