using AniMeido.Contracts.Playback;
using AniMeido.PluginProtocol;

namespace AniMeido.App.Services;

public sealed class HostedAnimePlaybackLauncher : IAnimePlaybackLauncher
{
    private PluginHostSupervisor? _supervisor;
    private bool _isAvailable;

    public bool IsAvailable => _isAvailable;

    public event EventHandler? AvailabilityChanged;

    internal void Attach(PluginHostSupervisor supervisor)
        => _supervisor = supervisor;

    internal void SetAvailable(bool isAvailable)
    {
        if (_isAvailable == isAvailable)
        {
            return;
        }

        _isAvailable = isAvailable;
        AvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task LaunchAsync(
        AnimePlaybackContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!_isAvailable || _supervisor is null)
        {
            throw new InvalidOperationException("在线播放插件当前不可用。");
        }

        return _supervisor.LaunchAnimePlaybackAsync(
            new AnimePlaybackRequest(
                context.AnimeId,
                context.Title,
                context.AlternateTitles),
            cancellationToken);
    }
}
