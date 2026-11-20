namespace AniMeido.App.Services;

public sealed partial record DesktopSettings
{
    public string VisualStyle { get; init; } = "Fluent11";
    public string FontStyle { get; init; } = "System";
    public string CatalogCardStyle { get; init; } = "Cover";
    public bool ShowPlanReminders { get; init; }
}
