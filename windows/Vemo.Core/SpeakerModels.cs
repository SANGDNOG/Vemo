namespace Vemo.Core;

public sealed record SpeakerModelPin(string File, string Url, long Bytes, string Sha256);
public static class SpeakerModels
{
    public static readonly SpeakerModelPin[] Pins = [
        new("segmentation.onnx", "https://huggingface.co/csukuangfj/sherpa-onnx-pyannote-segmentation-3-0/resolve/9403a6902bb58e3d5ae8c7e77c3422de279db2e0/model.onnx", 5992913, "220ad67ca923bef2fa91f2390c786097bf305bceb5e261d4af67b38e938e1079"),
        new("embedding.onnx", "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx", 39593761, "1a331345f04805badbb495c775a6ddffcdd1a732567d5ec8b3d5749e3c7a5e4b")];
    public const string Notices = "Optional speaker analysis: sherpa-onnx 1.13.8 (Apache-2.0, k2-fsa); ONNX Runtime (MIT, Microsoft).\npyannote segmentation-3.0: MIT, Copyright 2022 CNRS; https://huggingface.co/pyannote/segmentation-3.0/blob/main/LICENSE\n3D-Speaker eres2net base zh-cn 16k: Apache-2.0, Alibaba; https://github.com/modelscope/3D-Speaker/blob/main/LICENSE\nModels downloaded separately from pinned public upstream URLs; no Python or model repository code is executed.\n";
    public static void Verify(string directory)
    {
        foreach (var pin in Pins) { var path = Path.Combine(directory, pin.File); SafeFiles.NoLinks(path); if (!File.Exists(path)) throw new FileNotFoundException("화자 모델을 먼저 설치하세요: " + pin.File); if (new FileInfo(path).Length != pin.Bytes || SafeFiles.Hash(path) != pin.Sha256) throw new InvalidDataException("화자 모델 무결성 확인 실패: " + pin.File); }
    }
}
