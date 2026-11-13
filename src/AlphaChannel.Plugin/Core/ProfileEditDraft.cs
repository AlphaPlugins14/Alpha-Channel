namespace AlphaChannel.Plugin.Core;

// A private draft: selecting a picture never uploads it until Save is pressed.
internal sealed record ProfileEditValues(string? Icon, string Color, string Bio, string Status,
    bool UseImage, string? ImagePath, string? Theme);

internal sealed class ProfileEditDraft(ProfileEditValues initial)
{
    internal ProfileEditValues Baseline = initial;
    internal string? Icon = initial.Icon;
    internal string Color = initial.Color;
    internal string Bio = initial.Bio;
    internal string Status = initial.Status;
    internal bool UseImage = initial.UseImage;
    internal string? ImagePath = initial.ImagePath;
    internal string? Theme = initial.Theme;
    internal ProfileEditValues Values => new(Icon, Color, Bio, Status, UseImage, UseImage ? ImagePath : null, Theme);
    internal bool Dirty => Values != Baseline;
    internal bool CanNavigate(bool saving) => !saving && !Dirty;
}
