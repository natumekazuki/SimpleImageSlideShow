namespace SimpleImageSlideShow.Models
{
    public sealed record SettingsProfileSummary(string Id, string Name, bool IsActive);

    public sealed record SettingsProfile(string Id, string Name, bool IsActive, AppSettings Settings);
}
