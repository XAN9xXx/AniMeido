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
}
