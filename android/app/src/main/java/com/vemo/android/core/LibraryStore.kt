package com.vemo.android.core

import java.io.File
import java.nio.file.Files
import java.nio.file.LinkOption.NOFOLLOW_LINKS
import java.util.UUID

data class LibraryFolder(val id: String, val name: String, val parentId: String?)
data class LibraryState(val folders: List<LibraryFolder>, val recordingFolders: Map<String, String>)

/** Independent local sidecar. Reads validate before use; writes never touch recording data. */
class LibraryStore(private val file: File) {
    @Synchronized fun snapshot(): LibraryState = load()

    @Synchronized fun createFolder(name: String, parentId: String? = null): LibraryFolder {
        val state = load()
        if (parentId != null) requireFolder(state, parentId)
        val folder = LibraryFolder(UUID.randomUUID().toString(), folderName(name.trim()), parentId)
        save(state.copy(folders = state.folders + folder))
        return folder
    }

    @Synchronized fun renameFolder(id: String, name: String) {
        val state = load()
        requireFolder(state, id)
        val normalized = folderName(name.trim())
        save(state.copy(folders = state.folders.map { if (it.id == id) it.copy(name = normalized) else it }))
    }

    /** Reparents one folder; stable IDs keep its descendants and recordings together. */
    @Synchronized fun moveFolder(id: String, parentId: String?) {
        val state = load()
        val folder = requireFolder(state, id)
        if (parentId != null) requireFolder(state, parentId)
        if (folder.parentId == parentId) return
        val byId = state.folders.associateBy { it.id }
        var ancestor = parentId
        while (ancestor != null) {
            if (ancestor == id) throw ProtocolException("폴더를 자기 자신이나 하위 폴더 안으로 이동할 수 없습니다.")
            ancestor = byId.getValue(ancestor).parentId
        }
        save(state.copy(folders = state.folders.map { if (it.id == id) it.copy(parentId = parentId) else it }))
    }

    @Synchronized fun deleteFolder(id: String) {
        val state = load()
        requireFolder(state, id)
        val children = state.folders.groupBy { it.parentId }
        val removed = mutableSetOf<String>()
        val pending = java.util.ArrayDeque<String>()
        pending.add(id)
        while (pending.isNotEmpty()) {
            val current = pending.removeFirst()
            removed.add(current)
            children[current].orEmpty().forEach { pending.add(it.id) }
        }
        save(LibraryState(state.folders.filterNot { it.id in removed },
            state.recordingFolders.filterValues { it !in removed }))
    }

    @Synchronized fun assign(recordingId: String, folderId: String?) {
        validUuid(recordingId)
        val state = load()
        if (folderId != null) requireFolder(state, folderId)
        val assignments = state.recordingFolders.toMutableMap()
        if (folderId == null) assignments.remove(recordingId) else assignments[recordingId] = folderId
        save(state.copy(recordingFolders = assignments))
    }

    @Synchronized fun folderFor(recordingId: String): String? {
        validUuid(recordingId)
        return load().recordingFolders[recordingId]
    }

    @Synchronized fun path(folderId: String): String {
        val state = load()
        var folder: LibraryFolder? = requireFolder(state, folderId)
        val byId = state.folders.associateBy { it.id }
        val names = mutableListOf<String>()
        while (folder != null) {
            names.add(folder.name)
            folder = folder.parentId?.let { byId[it] }
        }
        return names.asReversed().joinToString("/")
    }

    private fun requireFolder(state: LibraryState, id: String): LibraryFolder {
        validUuid(id)
        return state.folders.firstOrNull { it.id == id }
            ?: throw ProtocolException("선택한 폴더를 찾을 수 없습니다. 녹음 원본은 보존됩니다.")
    }

    private fun folderName(name: String): String {
        if (name.isBlank() || name != name.trim() || name.length > 255 || name.any { it.isISOControl() || it == '/' || it == '\\' })
            throw ProtocolException("폴더 이름은 1–255자로 입력하고 구분자나 제어 문자를 제외하세요.")
        return name
    }

    private fun validate(state: LibraryState) {
        val byId = state.folders.associateBy { it.id }
        if (byId.size != state.folders.size) throw ProtocolException("Duplicate library folder ID")
        val names = mutableSetOf<Pair<String?, String>>()
        for (folder in state.folders) {
            validUuid(folder.id)
            folderName(folder.name)
            folder.parentId?.let {
                validUuid(it)
                if (it !in byId) throw ProtocolException("Missing library parent")
            }
            if (!names.add(folder.parentId to folder.name)) throw ProtocolException("같은 폴더에 동일한 이름이 있습니다.")
        }
        val checked = mutableSetOf<String>()
        for (folder in state.folders) {
            val visiting = mutableSetOf<String>()
            var current: String? = folder.id
            while (current != null && current !in checked) {
                if (!visiting.add(current)) throw ProtocolException("Cyclic library folders")
                current = byId.getValue(current).parentId
            }
            checked.addAll(visiting)
        }
        state.recordingFolders.forEach { (recording, folder) ->
            validUuid(recording); validUuid(folder)
            if (folder !in byId) throw ProtocolException("Missing assigned library folder")
        }
    }

    private fun rejectLinks() {
        if (Files.isSymbolicLink(file.toPath()) || Files.isSymbolicLink(file.absoluteFile.parentFile!!.toPath()))
            throw ProtocolException("Symlink library metadata rejected")
    }

    private fun load(): LibraryState {
        rejectLinks()
        if (!Files.exists(file.toPath(), NOFOLLOW_LINKS)) return LibraryState(emptyList(), emptyMap())
        if (!Files.isRegularFile(file.toPath(), NOFOLLOW_LINKS)) throw ProtocolException("Library metadata must be a regular file")
        try {
            val obj = file.inputStream().use { Json.parse(readBounded(it, Wire.MAX_JSON)) }
            if (obj.keySet() != setOf("version", "folders", "recordingFolders") || Json.long(obj, "version") != 1L)
                throw ProtocolException("Unsupported library schema")
            val folders = obj["folders"]
            val assignments = obj["recordingFolders"]
            if (!folders.isJsonArray || !assignments.isJsonObject) throw ProtocolException("Invalid library collections")
            val parsedFolders = folders.asJsonArray.map { value ->
                if (!value.isJsonObject) throw ProtocolException("Invalid library folder")
                val folder = value.asJsonObject
                if (folder.keySet() != setOf("id", "name", "parentId")) throw ProtocolException("Invalid library folder fields")
                LibraryFolder(Json.string(folder, "id"), Json.string(folder, "name"),
                    if (folder["parentId"].isJsonNull) null else Json.string(folder, "parentId"))
            }
            val parsedAssignments = assignments.asJsonObject.keySet().associateWith { Json.string(assignments.asJsonObject, it) }
            return LibraryState(parsedFolders, parsedAssignments).also { validate(it) }
        } catch (error: ProtocolException) {
            throw ProtocolException("라이브러리 정보를 읽을 수 없습니다. 기존 파일과 녹음은 보존했습니다. ${error.message}")
        }
    }

    private fun save(state: LibraryState) {
        validate(state)
        rejectLinks()
        val bytes = Json.bytes(linkedMapOf("version" to 1, "folders" to state.folders, "recordingFolders" to state.recordingFolders))
        if (bytes.size > Wire.MAX_JSON) throw ProtocolException("Library metadata exceeds size limit")
        val parent = file.absoluteFile.parentFile!!
        if (!parent.exists() && !parent.mkdirs()) throw java.io.IOException("Cannot create library storage")
        atomicWrite(file.absoluteFile, bytes)
    }
}
