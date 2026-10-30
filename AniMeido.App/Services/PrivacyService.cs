namespace AniMeido.App.Services;

public sealed class PrivacyService(DesktopSettingsStore settings)
{
    public async Task<bool> IsAcceptedAsync(CancellationToken cancellationToken = default)
        => (await settings.LoadAsync(cancellationToken)).PrivacyAccepted == true;

    public async Task AcceptAsync(CancellationToken cancellationToken = default)
        => await settings.UpdateAsync(value => value with { PrivacyAccepted = true }, cancellationToken);
}
