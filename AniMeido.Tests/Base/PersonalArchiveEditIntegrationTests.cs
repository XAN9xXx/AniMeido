using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;

namespace AniMeido.Tests;

public sealed class PersonalArchiveEditIntegrationTests : DbTestBase
{
    [Fact]
    public async Task TheProductionReaderOpensTheRequestedArchiveAndTags()
    {
        await RunProductionMigrationAsync();
        var archive = new ArchiveService(DbFactory);
        await archive.SaveArchiveWithTagsAsync(17, "Original title", 6.5, "Original summary", ["Original tag"]);
        await archive.SaveArchiveWithTagsAsync(18, "Other title", 8.5, "Other summary", ["Other tag"]);
        var editor = new PersonalArchiveEditViewModel(archive);
        var shown = false;

        await editor.EditAsync(17, (existing, tags) =>
        {
            shown = true;
            Assert.NotNull(existing);
            Assert.Equal(17, existing.AnimeId);
            Assert.Equal(6.5, existing.PersonalRating);
            Assert.Equal("Original summary", existing.SummaryNote);
            Assert.Equal(new[] { "Original tag" }, tags);
            return Task.CompletedTask;
        }, () => true, default);

        Assert.True(shown);
        Assert.False(editor.IsEditing);
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    public void TheActualClickAndBothNavigationExitsUseTheEditorLifetime()
    {
        var directory = Path.Combine(AppDataRootsTests.RepositoryRoot, "Plugins", "AniMeido.Plugin.Base", "Views");
        var page = File.ReadAllText(Path.Combine(directory, "AnimeDetailPage.xaml.cs"));
        var xaml = File.ReadAllText(Path.Combine(directory, "AnimeDetailPage.xaml"));
        Assert.Contains("Click=\"OnPersonalArchiveClick\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PersonalArchiveButton\"", xaml, StringComparison.Ordinal);
        var click = Method("private async void OnPersonalArchiveClick(", "private void UpdatePersonalArchiveAvailability(");
        Assert.Contains("_personalArchiveEdit.EditAsync(", click, StringComparison.Ordinal);
        Assert.Contains("cancellation.Token", click, StringComparison.Ordinal);
        Assert.Contains("CancelPersonalArchiveEdit();",
            Method("private void OnPageUnloaded(", "private void UpdatePlaybackAvailability("), StringComparison.Ordinal);
        Assert.Contains("CancelPersonalArchiveEdit();",
            Method("public async Task OnNavigatedToAsync(", "private async Task LoadCurrentAnimeAsync("), StringComparison.Ordinal);
        var cancel = Method("private void CancelPersonalArchiveEdit(", "private async Task ShowPersonalArchiveAsync(");
        Assert.Contains("_personalArchiveCancellation?.Cancel();", cancel, StringComparison.Ordinal);
        Assert.Contains("_personalArchiveDialog?.Hide();", cancel, StringComparison.Ordinal);
        var dialog = Method("private async Task ShowPersonalArchiveAsync(", "private async void OnOnlinePlayClick(");
        Assert.DoesNotContain("_currentAnimeId", dialog, StringComparison.Ordinal);

        string Method(string start, string next)
        {
            var begin = page.IndexOf(start, StringComparison.Ordinal);
            Assert.True(begin >= 0);
            var end = page.IndexOf(next, begin + start.Length, StringComparison.Ordinal);
            Assert.True(end > begin);
            return page[begin..end];
        }
    }
}
