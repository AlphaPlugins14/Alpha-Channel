using System.Numerics;

namespace AlphaChannel.Plugin;

internal sealed record ProfileThemeDesign(string Id, string Name, string Description, Vector4 Background, Vector4 Accent, Vector4 Secondary);
internal static class ProfileThemes
{
    private static Vector4 Rgb(int hex) => new((hex>>16&255)/255f,(hex>>8&255)/255f,(hex&255)/255f,1f);
    internal static readonly ProfileThemeDesign[] All =
    [
        new("midnight-premiere", "Midnight Premiere", "Charcoal, champagne gold and cinema spotlights.", Rgb(0x191A20), Rgb(0xD9AF68), Rgb(0xAB7A38)),
        new("neon-arcade", "Neon Arcade", "Violet and cyan with a subtle arcade grid.", Rgb(0x11162E), Rgb(0x28D6EC), Rgb(0xAC45F5)),
        new("after-hours", "After Hours", "Indigo, magenta and equaliser details.", Rgb(0x1B1230), Rgb(0xCB52DF), Rgb(0x597DED)),
        new("cozy-watch-party", "Cozy Watch Party", "Warm charcoal and gentle amber light.", Rgb(0x251C19), Rgb(0xE5A45A), Rgb(0xA9713C)),
        new("haunted-screening", "Haunted Screening", "Plum, orange, pumpkins and little ghosts.", Rgb(0x221424), Rgb(0xE7833C), Rgb(0xAD79D0)),
        new("starlight", "Starlight", "Midnight blue, silver borders and sparse stars.", Rgb(0x101F35), Rgb(0xA1C7EF), Rgb(0x5D93D4)),
    ];
    internal static ProfileThemeDesign? Find(string? id) => All.FirstOrDefault(t=>t.Id==id);
}
