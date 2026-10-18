using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Vemo.Core;
using Vemo.Runtime;

namespace Vemo.App;

public sealed partial class MainWindow
{
    bool speakerOperation;
    CancellationTokenSource? speakerCancellation;
    string? loadedSpeakerJob, loadedSpeakerRevision, loadingSpeakerJob;
    SpeakerDocument? loadedSpeakers;
    string SpeakerModelDirectory => Path.Combine(store.Root, "runtime-auto", "speaker-models");
    bool CanAnalyzeSpeakers() => !speakerOperation && runner.ActiveJobId == null && Selected().Count() == 1 && Current() is { Trashed: false, State: JobState.Completed or JobState.Failed or JobState.Cancelled } && !string.IsNullOrWhiteSpace(transcript.Text);

    // Sidecar decoding and exact transcript hashing run off the dispatcher.
    void LoadSpeakers(RecordingJob job)
    {
        if (loadingSpeakerJob == job.Id || loadedSpeakerJob == job.Id && loadedSpeakerRevision == job.ArtifactRevision) return;
        loadingSpeakerJob = job.Id;
        _ = Execute(async () =>
        {
            try
            {
                var document = await Task.Run(() => store.ReadSpeakerDocument(job.Id), work.Token);
                if (loadingSpeakerJob != job.Id || store.Get(job.Id).ArtifactRevision != job.ArtifactRevision) return;
                loadedSpeakerJob = job.Id; loadedSpeakerRevision = job.ArtifactRevision; loadedSpeakers = document;
            }
            finally { if (loadingSpeakerJob == job.Id) loadingSpeakerJob = null; }
            if (Current()?.Id == job.Id) Detail();
        });
    }
    async Task InstallSpeakerModels()
    {
        if (speakerOperation) return;
        speakerOperation = true; speakerCancellation = CancellationTokenSource.CreateLinkedTokenSource(work.Token); UpdateActions();
        try
        {
            status.Text = "선택한 화자 모델을 설치하고 검증하는 중…";
            await Task.Run(() => SpeakerProvision.PrepareAsync(SpeakerModelDirectory, runtimeHttp, ct: speakerCancellation.Token), speakerCancellation.Token);
            status.Text = "화자 모델을 설치했습니다. 녹음의 화자 분석을 직접 실행하세요.";
        }
        finally { speakerCancellation.Dispose(); speakerCancellation = null; speakerOperation = false; UpdateActions(); }
    }
    async Task AnalyzeSpeakers()
    {
        if (!CanAnalyzeSpeakers() || Current() is not { } job) return;
        var selection = await ChooseSpeakerCount();
        if (selection == null || !CanAnalyzeSpeakers() || Current()?.Id != job.Id) return;
        speakerOperation = true; speakerCancellation = CancellationTokenSource.CreateLinkedTokenSource(work.Token); UpdateActions();
        try
        {
            status.Text = "화자 분석 중 · 다시 분석하면 이름이 초기화됩니다.";
            var helper = Path.Combine(AppContext.BaseDirectory, "diarization", "Vemo.Diarization.exe");
            await Task.Run(() => runner.AnalyzeSpeakersAsync(job.Id, new LocalSpeakerAnalysisEngine(helper, SpeakerModelDirectory, selection.Count), speakerCancellation.Token), speakerCancellation.Token);
            loadedSpeakerJob = null;
            status.Text = "화자 분석을 저장했습니다. 화자 이름을 눌러 수정하세요.";
        }
        catch (OperationCanceledException) { status.Text = "화자 분석을 취소했습니다. 이전 결과를 유지합니다."; }
        finally { speakerCancellation.Dispose(); speakerCancellation = null; speakerOperation = false; Detail(); }
    }
    sealed record SpeakerCountChoice(int? Count)
    {
        public override string ToString() => Count is { } count ? $"{count}명" : "자동 · 실험적";
    }
    async Task<SpeakerCountChoice?> ChooseSpeakerCount()
    {
        var choices = new[] { new SpeakerCountChoice(null) }.Concat(Enumerable.Range(1, 20).Select(n => new SpeakerCountChoice(n))).ToArray();
        var selector = new ComboBox { ItemsSource = choices, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        NativeWhite.Label(selector, "화자 수");
        var dialog = new Window { Title = "화자 분석", Width = 380, Height = 260, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var body = new StackPanel { Margin = new Thickness(20), Spacing = 16 };
        body.Children.Add(NativeWhite.Text("화자 수를 알면 지정해 주세요. 자동 분석은 실험적이며 실제보다 많은 화자를 나눌 수 있습니다.", "secondary"));
        body.Children.Add(selector);
        body.Children.Add(NativeWhite.Text("다시 분석하면 저장된 화자 이름이 초기화됩니다.", "caption"));
        var start = new Button { Content = "분석 시작", HorizontalAlignment = HorizontalAlignment.Right };
        start.Click += (_, _) => dialog.Close(selector.SelectedItem as SpeakerCountChoice);
        body.Children.Add(start); dialog.Content = body;
        return await dialog.ShowDialog<SpeakerCountChoice?>(this).WaitAsync(work.Token);
    }
    Task CancelSpeakers() { speakerCancellation?.Cancel(); return Task.CompletedTask; }
    async Task RenameSpeaker(string id)
    {
        var job = Current(); var document = job != null && loadedSpeakerJob == job.Id ? loadedSpeakers : null;
        if (job == null || job.Trashed || document == null || speakerOperation) return;
        var identity = document.Speakers.Single(s => s.Id == id);
        var input = new TextBox { Text = identity.Name, MinWidth = 260 };
        NativeWhite.Label(input, "화자 이름");
        var error = NativeWhite.Text("", "secondary");
        var dialog = new Window { Title = "화자 이름 수정", Width = 360, Height = 230, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
        var body = new StackPanel { Spacing = 14, Margin = new Thickness(20) }; body.Children.Add(input); body.Children.Add(error);
        var save = new Button { Content = "저장", HorizontalAlignment = HorizontalAlignment.Right };
        save.Click += async (_, _) =>
        {
            save.IsEnabled = false;
            try
            {
                var name = input.Text ?? "";
                await work.Start(token => Task.Run(() => store.RenameSpeaker(job.Id, document.Generation, id, name), token));
                dialog.Close(true);
            }
            catch (Exception e) { error.Text = e.Message; save.IsEnabled = true; }
        };
        body.Children.Add(save); dialog.Content = body;
        await dialog.ShowDialog<bool>(this).WaitAsync(work.Token);
        loadedSpeakerJob = null; Detail();
    }
}
