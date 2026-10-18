namespace Vemo.Core;

public sealed partial class LibraryStore
{
    public SpeakerDocument? ReadSpeakerDocument(string id)
    {
        RecordingJob job; string path, transcript;
        lock (gate)
        {
            job = Get(id); if (job.State == JobState.Purged) return null;
            path = Path.Combine(ArtifactDirectory(id, job.ArtifactRevision), SpeakerDocument.FileName);
            transcript = ReadTranscript(id);
        }
        var result = SpeakerDocument.ReadOptional(path, job.AudioSha256, transcript);
        lock (gate) return Get(id) == job ? result : null;
    }

    public void SaveSpeakerDocument(string id, SpeakerDocument document, string? expectedGeneration = null, string? expectedDocument = null, RecordingJob? expectedJob = null, CancellationToken ct = default)
    {
        lock (gate)
        {
            var job = Get(id);
            if (expectedJob != null && job != expectedJob) throw new InvalidDataException("Recording changed during analysis.");
            if (job.Trashed || job.State is JobState.Purged or JobState.Queued or JobState.Transcribing or JobState.GeneratingNotes) throw new InvalidOperationException("현재 작업을 마친 뒤 화자를 분석하세요.");
            if (expectedGeneration != null && (ReadSpeakerDocument(id)?.Generation ?? "") != expectedGeneration)
                throw new InvalidDataException("화자 정보가 변경되었습니다. 분석을 다시 실행하세요.");
            if (expectedDocument != null && (ReadSpeakerDocument(id)?.Serialize() ?? "") != expectedDocument)
                throw new InvalidDataException("화자 이름 또는 분석이 변경되었습니다. 다시 분석하세요.");
            var json = document.Serialize(); SpeakerDocument.Parse(json, job.AudioSha256, ReadTranscript(id));
            ct.ThrowIfCancellationRequested();
            SafeFiles.Atomic(Path.Combine(ArtifactDirectory(id, job.ArtifactRevision), SpeakerDocument.FileName), json);
        }
    }
    public void RenameSpeaker(string id, string generation, string speakerId, string name)
    {
        lock (gate)
        {
            var previous = ReadSpeakerDocument(id) ?? throw new InvalidDataException("저장된 화자 분석이 없습니다.");
            if (previous.Generation != generation) throw new InvalidDataException("화자 분석이 변경되었습니다. 다시 열어 주세요.");
            SaveSpeakerDocument(id, previous.Rename(speakerId, name));
        }
    }
}
