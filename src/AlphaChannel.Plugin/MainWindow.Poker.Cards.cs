using AlphaChannel.Plugin.Video;
using System.Numerics;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private static string PokerCardName(int card)
    {
        if (card is < 0 or >= 52) return "Face-down card";
        var rank = (card % 13 + 2) switch
        {
            14 => "Ace", 13 => "King", 12 => "Queen", 11 => "Jack", var number => number.ToString(),
        };
        var suit = (card / 13) switch
        {
            0 => "Spades", 1 => "Hearts", 2 => "Diamonds", _ => "Clubs",
        };
        return $"{rank} of {suit}";
    }

    private bool PokerSoloPrivateCardsVisible => pokerLocalCardsHand == pokerHand &&
        pokerLocalFirst >= 0 && pokerLocalSecond >= 0 &&
        pokerPlayers.Count(p => p.InHand && p.Id != PokerBotId) == 1 &&
        pokerPlayers.Any(p => p.InHand && p.Id == PokerBotId);

    // Use the same visibility rule for drawing and hover text. A host knows every
    // dealt card, but hovering a face-down card must never disclose that information.
    private (int First, int Second)? PokerVisibleSeatCards(PokerPlayer seat, bool showPrivate)
    {
        if (showPrivate && seat.Id == LocalPokerUserId && pokerLocalCardsHand == pokerHand &&
            pokerLocalFirst is >= 0 and < 52 && pokerLocalSecond is >= 0 and < 52)
            return (pokerLocalFirst, pokerLocalSecond);
        if (pokerPhase == PokerPhase.Showdown && seat.InHand && !seat.Folded &&
            seat.First is >= 0 and < 52 && seat.Second is >= 0 and < 52)
            return (seat.First, seat.Second);
        return null;
    }

    private string? PokerCardTooltipAt(Vector2 uv, bool showPrivate)
    {
        if (!PokerActive || pokerPreviewOverlay != PokerPreviewOverlay.None ||
            uv.X is < 0f or > 1f || uv.Y is < 0f or > 1f) return null;
        var x = uv.X * WhiteboardRenderer.Width;
        var y = uv.Y * WhiteboardRenderer.Height;
        bool Contains(int left, int top, int width, int height) =>
            x >= left && x < left + width && y >= top && y < top + height;
        for (var index = 0; index < 5; index++)
        {
            if (!Contains(424 + index * 87, 359, 76, 108)) continue;
            if (index >= pokerBoard.Count) return "Face-down card";
            var elapsed = (DateTime.UtcNow - pokerRevealUtc).TotalSeconds -
                (index >= pokerBoard.Count - (pokerBoard.Count == 3 ? 3 : 1)
                    ? (index - (pokerBoard.Count == 3 ? 0 : pokerBoard.Count - 1)) * .12 : -1);
            return elapsed is >= 0 and < .18 ? "Face-down card" : PokerCardName(pokerBoard[index]);
        }
        var soloPrivate = PokerSoloPrivateCardsVisible;
        if (soloPrivate && LocalPokerPlayer?.InHand == true)
        {
            if (Contains(267, 367, 65, 91)) return PokerCardName(pokerLocalFirst);
            if (Contains(341, 367, 65, 91)) return PokerCardName(pokerLocalSecond);
        }
        foreach (var seat in pokerPlayers)
        {
            var (sx, sy, _) = PokerSeatBounds(seat.Seat);
            var index = Contains(sx + 10, sy + 43, 31, 39) ? 0 :
                Contains(sx + 47, sy + 43, 31, 39) ? 1 : -1;
            if (index < 0) continue;
            if (PokerVisibleSeatCards(seat, true) is { } cards)
                return PokerCardName(index == 0 ? cards.First : cards.Second);
            return seat.InHand ? "Face-down card" : null;
        }
        return null;
    }
}
