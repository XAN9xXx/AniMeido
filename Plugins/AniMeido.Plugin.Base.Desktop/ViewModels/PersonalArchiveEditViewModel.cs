using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;

namespace AniMeido.Plugin.Base.ViewModels;

internal sealed class PersonalArchiveEditViewModel
{
    private readonly Func<int, CancellationToken, Task<AnimeArchive?>> _readArchive;
    private readonly Func<int, CancellationToken, Task<IReadOnlyList<string>>> _readTags;

    public bool IsEditing { get; private set; }

    public PersonalArchiveEditViewModel(ArchiveService archive)
        : this(archive.GetArchiveAsync, archive.GetAnimeTagsAsync)
    {
    }

    internal PersonalArchiveEditViewModel(
        Func<int, CancellationToken, Task<AnimeArchive?>> readArchive,
        Func<int, CancellationToken, Task<IReadOnlyList<string>>> readTags)
    {
        _readArchive = readArchive;
        _readTags = readTags;
    }

    public async Task EditAsync(
        int animeId,
        Func<AnimeArchive?, IReadOnlyList<string>, Task> edit,
        Func<bool> isCurrent,
        CancellationToken cancellationToken)
    {
        if (IsEditing || cancellationToken.IsCancellationRequested || !isCurrent())
            return;

        IsEditing = true;
        try
        {
            var archive = await _readArchive(animeId, cancellationToken);
            if (cancellationToken.IsCancellationRequested || !isCurrent())
                return;

            var tags = await _readTags(animeId, cancellationToken);
            if (cancellationToken.IsCancellationRequested || !isCurrent())
                return;

            await edit(archive, tags);
        }
        finally
        {
            IsEditing = false;
        }
    }
}
