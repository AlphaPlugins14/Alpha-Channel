namespace AlphaChannel.Contracts;

// DisplayName is both the cosmetic "gamer tag" shown everywhere and the unique key friends search
// by (see FriendService.FindAccountByDisplayNameAsync) - unlike a split handle/display-name model,
// AlphaChannel merged those two roles into one field, so the format itself has to stay narrow enough
// to be an unambiguous search key (no leading/trailing/doubled whitespace, no lookalike/invisible
// unicode tricks) while still allowing enough personality (spaces, non-ASCII letters for
// international players) to feel like a real gamer tag. Shared between server (authoritative) and
// plugin (live as-you-type feedback) so the two can never drift.
public static class DisplayNameRules
{
    public const int MinLength = 3;
    public const int MaxLength = 20;

    public static bool IsValid(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length < MinLength || trimmed.Length > MaxLength)
        {
            return false;
        }

        var previousWasSpace = false;
        foreach (var character in trimmed)
        {
            if (character == ' ')
            {
                if (previousWasSpace)
                {
                    return false;
                }

                previousWasSpace = true;
                continue;
            }

            previousWasSpace = false;
            if (!IsAllowedChar(character))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAllowedChar(char character) =>
        char.IsLetterOrDigit(character) || character is '_' or '-';
}
