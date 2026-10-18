using Avalonia.Headless.XUnit;
using Vemo.Core;
using Xunit;
using static Vemo.App.Tests.UiFixture;

namespace Vemo.App.Tests;

public sealed class RetranscriptionActionTests
{
    [AvaloniaTheory]
    [InlineData(JobState.Completed, false, true)]
    [InlineData(JobState.Failed, false, true)]
    [InlineData(JobState.Cancelled, false, true)]
    [InlineData(JobState.Queued, false, false)]
    [InlineData(JobState.Transcribing, false, false)]
    [InlineData(JobState.GeneratingNotes, false, false)]
    [InlineData(JobState.Completed, true, false)]
    [InlineData(JobState.Failed, true, false)]
    [InlineData(JobState.Cancelled, true, false)]
    public async Task MenuAllowsOnlyInactiveUntrashedRecovery(JobState state, bool trashed, bool enabled)
    {
        await using var ui = new UiFixture(); var id = ui.UnfiledId;
        if (state == JobState.Transcribing)
        {
            ui.Store.UpdateSettings(ui.Store.Settings with { QueuePaused = false });
            Assert.True(ui.Store.TryStart(id));
            ui.Store.UpdateSettings(ui.Store.Settings with { QueuePaused = true });
        }
        if (state is JobState.GeneratingNotes or JobState.Completed) ui.Store.SaveTranscript(id, "old transcript");
        if (state == JobState.Completed) ui.Store.Complete(id, "old notes");
        if (state is JobState.Failed or JobState.Cancelled) ui.Store.Fail(id, "fixture", state == JobState.Cancelled);
        if (trashed) ui.Store.Trash([id]);
        ui.Navigate(trashed ? 2 : 1); ui.SelectJob(Array.IndexOf(ui.VisibleTitles(), "미분류 녹음"));
        Assert.Equal(enabled, Menu(ui.Jobs.ContextMenu!, "원음부터 다시 전사").IsEnabled);
    }

    [AvaloniaTheory]
    [InlineData(JobState.Completed)]
    [InlineData(JobState.Failed)]
    [InlineData(JobState.Cancelled)]
    public async Task ActionQueuesFreshRevisionWithExistingModelsAndKeepsCommittedResults(JobState state)
    {
        await using var ui = new UiFixture(); var id = ui.UnfiledId;
        ui.Store.SaveTranscript(id, "old transcript"); ui.Store.Complete(id, "old notes");
        ui.Store.Rename(id, "수동 제목");
        var committed = ui.Store.Get(id);
        if (state != JobState.Completed) ui.Store.Fail(id, "fixture", state == JobState.Cancelled);
        ui.Store.UpdateSettings(ui.Store.Settings with { Models = ui.Store.Settings.Models with { WhisperProfile = "small" } });
        ui.Navigate(1); ui.SelectJob(Array.IndexOf(ui.VisibleTitles(), "수동 제목"));
        RightClick(ui.Window, Realize(ui.Jobs, ui.Jobs.SelectedIndex));
        ActivateMenu(ui.Window, ui.Jobs.ContextMenu!, "원음부터 다시 전사");
        await Until(() => ui.Store.Get(id).State == JobState.Queued, "Recovery must queue the selected recording.");
        var queued = ui.Store.Get(id);
        Assert.NotNull(queued.PendingArtifactRevision); Assert.Equal(committed.Models, queued.Models);
        Assert.Equal("수동 제목", queued.Title); Assert.Equal(TitleOwnership.Manual, queued.TitleOwnership);
        Assert.Equal("", ui.Store.ReadTranscriptForProcessing(id));
        Assert.Equal("old transcript", ui.Store.ReadTranscript(id)); Assert.Equal("old notes", ui.Store.ReadNotes(id));
        Assert.Null(ui.Store.Get(ui.FiledId).PendingArtifactRevision);
        Assert.False(Menu(ui.Jobs.ContextMenu!, "원음부터 다시 전사").IsEnabled);
    }
}
