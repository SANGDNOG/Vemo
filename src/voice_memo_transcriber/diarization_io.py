"""Descriptor-relative reads/immutable sidecar commits inside the owned state root."""
import hashlib
import os
import stat
import uuid
from pathlib import Path
from .store import owned_directory, atomic_json_at
from .diarization import LIMIT, decode_document


def relative_parts(root, path):
    root = Path(root).absolute(); path = Path(path)
    if not path.is_absolute() or '..' in path.parts: raise ValueError('unsafe_speaker_source')
    try:
        parts = path.relative_to(root).parts
    except ValueError:
        # macOS exposes /private/var through /var (including tempfile paths).
        # Resolve only an alias of the root, never any descendant: the latter
        # must still pass the descriptor-relative O_NOFOLLOW traversal below.
        for ancestor in path.parents:
            if ancestor.resolve() == root.resolve():
                parts = path.relative_to(ancestor).parts
                break
        else:
            raise ValueError('unsafe_speaker_source') from None
    return parts


def read_owned(root, path, maximum=LIMIT, digest=False):
    parts = relative_parts(root, path)
    with owned_directory(root, parts[:-1]) as parent:
        fd = os.open(parts[-1], os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC | os.O_NONBLOCK, dir_fd=parent)
        with os.fdopen(fd, 'rb') as source:
            before = os.fstat(source.fileno())
            if not stat.S_ISREG(before.st_mode) or before.st_nlink != 1 or before.st_size > maximum:
                raise ValueError('unsafe_speaker_source')
            result = hashlib.sha256() if digest else bytearray()
            count = 0
            for block in iter(lambda: source.read(1024*1024), b''):
                count += len(block)
                if count > maximum: raise ValueError('speaker_source_too_large')
                if digest: result.update(block)
                else: result.extend(block)
            after = os.fstat(source.fileno())
            current = os.stat(parts[-1], dir_fd=parent, follow_symlinks=False)
            identity = lambda st: (st.st_dev, st.st_ino, st.st_size, st.st_mtime_ns, st.st_ctime_ns)
            if count != before.st_size or identity(before) != identity(after) or identity(before) != identity(current):
                raise ValueError('speaker_source_changed')
    return result.hexdigest() if digest else bytes(result)


def sources(root, audio_path, artifact_path):
    audio_hash = read_owned(root, audio_path, maximum=16*1024**3, digest=True)
    transcript = decode_document(read_owned(root, artifact_path))
    if not isinstance(transcript, dict) or not isinstance(transcript.get('text'), str) or not isinstance(transcript.get('segments'), list):
        raise ValueError('invalid_speaker_transcript')
    return audio_hash, transcript


def commit(root, artifact_path, doc):
    # Immutable candidate first, SQLite pointer afterward: failed/cancelled work never
    # replaces a previously committed sidecar, even across engine restarts.
    parts = relative_parts(root, artifact_path)[:-1]
    with owned_directory(root, parts) as parent:
        try: os.mkdir('speakers', 0o700, dir_fd=parent)
        except FileExistsError: pass
        os.fsync(parent)
    with owned_directory(root, (*parts, 'speakers')) as parent:
        candidate = uuid.uuid4().hex
        os.mkdir(candidate, 0o700, dir_fd=parent); os.fsync(parent)
    with owned_directory(root, (*parts, 'speakers', candidate)) as parent:
        atomic_json_at(parent, 'speakers.json', doc)
    return str(Path(root).joinpath(*parts, 'speakers', candidate, 'speakers.json'))
