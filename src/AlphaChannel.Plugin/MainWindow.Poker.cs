using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private string? pokerWhiteboardSessionId;
    private WhiteboardRenderer? pokerPageRenderer;
    private bool pokerPageRendered;
    private enum PokerPreviewOverlay { None, Help, Settings }
    private enum PokerPreviewControl { None, Help, Settings, Close, Overlay, Music, Seat, Leave, Deal, Fold, CheckCall, BetRaise, AllIn }
    private PokerPreviewOverlay pokerPreviewOverlay;
    private int pokerControlSeat = -1;
    private const int PokerDealButtonX = 430;
    private const int PokerDealButtonY = 366;
    private const int PokerDealButtonWidth = 420;
    private const int PokerDealButtonHeight = 84;
    private bool CanDealPokerFromLobby => IsLocalActivityHost && pokerPhase == PokerPhase.Waiting &&
        pokerPlayers.Count > 0 && (pokerBotEnabled || pokerPlayers.Count >= 2);

    internal bool PokerTvControlsInteractive => PokerActive;

    private bool PokerActive => whiteboardActive &&
        (IsLocalActivityHost
            ? pokerWhiteboardSessionId is not null &&
              string.Equals(whiteboardSessionId, pokerWhiteboardSessionId, StringComparison.Ordinal)
            : stream.Mode == StreamMode.Viewing &&
              whiteboardSessionId is not null &&
              string.Equals(stream.CurrentRoomState?.Url,
                  WhiteboardUrlPrefix + whiteboardSessionId + "?activity=poker",
                  StringComparison.OrdinalIgnoreCase));

    private void OpenPokerPage()
    {
        if (!CanLaunchActivity(ActiveActivityKind.Poker)) return;
        currentPage = HomePage.Poker;
        lastActivitiesPage = HomePage.Poker;
    }

    private void StartPokerTable()
    {
        if (!IsLocalActivityHost || CurrentSession is null ||
            ActiveActivity != ActiveActivityKind.None) return;
        pokerStartingChips = pokerCustomRules ? pokerCustomChips : 1000;
        PokerBigBlind = pokerCustomRules ? pokerCustomBlind : 20;
        PokerSmallBlind = PokerBigBlind / 2;
        PokerTurnSeconds = pokerCustomRules ? pokerCustomTurnSeconds : 25;
        pokerBotEnabled = !pokerCustomRules || pokerCustomBot;
        pokerPaused = false;
        pokerActionSequence = 0;
        pokerLedger.Clear();
        pokerLocalFirst = pokerLocalSecond = pokerLocalCardsHand = -1;
        pokerPendingPrivateCards = null;
        pokerButton = pokerActingSeat = -1;
        pokerPot = pokerCurrentBet = 0;
        StartHostWhiteboard(WhiteboardMode.Solo, ActiveActivityKind.Poker);
        pokerWhiteboardSessionId = whiteboardSessionId;
        pokerSessionId = Guid.NewGuid().ToString("N");
        pokerPhase = PokerPhase.Waiting;
        pokerPhaseEndsUtc = DateTime.MaxValue;
        pokerPlayers.Clear();
        pokerBoard.Clear();
        pokerHand = 0;
        pokerRevision = 1;
        pokerMessage = "SIT TO PLAY";
        pokerPreviewOverlay = PokerPreviewOverlay.None;
        whiteboardTvDirty = true;
        ResetPokerMusic();
        SendPokerState();
        BeginPokerMusicLoad();
    }

    private void StopPokerTable()
    {
        if (IsLocalActivityHost && pokerWhiteboardSessionId is { } session &&
            string.Equals(whiteboardSessionId, session, StringComparison.Ordinal))
        {
            _ = stream.SendPokerCommandAsync("X|" + pokerSessionId);
            SendAndApplyWhiteboardCommand("X|" + session);
        }
        ResetPokerLocal();
        pokerPreviewOverlay = PokerPreviewOverlay.None;
        whiteboardTvDirty = true;
    }

    internal void UpdatePokerPreview()
    {
        if (!IsOpen || currentPage != HomePage.Poker || pokerPageRendered) return;
        if (pokerPageRenderer is null && !screenController.Engine.TryInitializeGraphics()) return;
        pokerPageRenderer ??= DxHandler.RunOnRenderThreadAndWait(
            "AlphaChannel.PokerPage.Create",
            () => new WhiteboardRenderer("AlphaChannel.PokerPage"),
            TimeSpan.FromSeconds(30));
        var pixels = new byte[WhiteboardRenderer.Width * WhiteboardRenderer.Height * 4];
        ApplyPokerTvOverlay(pixels, WhiteboardRenderer.Width, WhiteboardRenderer.Height, true);
        pokerPageRenderer.Upload(pixels);
        pokerPageRendered = true;
    }

    private void DrawPokerPage()
    {
        lastActivitiesPage = HomePage.Poker;
        if (!PokerActive)
        {
            DrawPokerSetupPage();
            return;
        }
        ImGui.PushTextWrapPos();
        ImGui.TextColored(MutedText, PokerActive
            ? "Take an open seat. The host deals the first hand; later hands deal automatically unless paused."
            : "Play Poker EX on your own TV or share it with a Watch Party.");
        ImGui.PopTextWrapPos();
        ImGui.Spacing();
        DrawPokerPrivateHand();

        var available = ImGui.GetContentRegionAvail();
        var width = MathF.Min(available.X, MathF.Max(Ui(260f), (available.Y - Ui(125f)) * 16f / 9f));
        var size = new Vector2(width, width * 9f / 16f);
        var origin = ImGui.GetCursorScreenPos() + new Vector2(MathF.Max(0f, (available.X - width) * .5f), 0f);
        ImGui.SetCursorScreenPos(origin);
        ImGui.InvisibleButton("##pokerPreview", size);
        if (ImGui.IsItemHovered())
        {
            var uv = (ImGui.GetIO().MousePos - origin) / size;
            if (PokerCardTooltipAt(uv, showPrivate: true) is { } cardTooltip)
                UiFonts.SetTooltip(cardTooltip);
            if (PokerControlAt(uv) != PokerPreviewControl.None)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) SubmitPokerPreviewControlAt(uv);
            }
        }
        var draw = ImGui.GetWindowDrawList();
        if (pokerPageRenderer?.PreviewHandle is { } handle && handle != nint.Zero)
            draw.AddImage(new ImTextureID(handle), origin, origin + size);
        else
            draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(CardBg));

        ImGui.SetCursorScreenPos(origin + new Vector2(0f, size.Y + Ui(12f)));
        if (PokerActive)
        {
            ImGui.TextColored(MutedText, string.IsNullOrWhiteSpace(pokerMusicTitle)
                ? "Loading Jazz from Incompetech..."
                : $"Music: {pokerMusicTitle} — Kevin MacLeod (incompetech.com), CC BY 4.0");
            ImGui.TextColored(MutedText, stream.Mode == StreamMode.None ? "Poker EX is showing on your TV." : "Poker EX is showing on the Watch Party TV.");
            if (IsLocalActivityHost)
            {
                if (ImGui.Button(pokerPaused ? "Resume table##pokerPause" : "Pause after hand##pokerPause")) TogglePokerPause();
                ImGui.SameLine();
                if (ImGui.Button("End Poker##pokerEnd")) StopPokerTable();
            }
            DrawPokerSessionLedger();
        }
        else if (IsLocalActivityHost)
            ImGui.TextColored(MutedText, "Starting Poker EX on the Watch Party TV...");
        else
            ImGui.TextColored(MutedText, stream.Mode == StreamMode.Viewing
                ? "Waiting for the Watch Party host to start Poker EX."
                : "Host a Watch Party to start Poker EX.");
    }

    private void DrawPokerPrivateHand()
    {
        var local = LocalPokerPlayer;
        if (local is null)
        {
            ImGui.TextColored(MutedText, "Choose an open seat in the table below to receive your cards.");
            ImGui.Spacing();
            return;
        }

        ImGui.TextColored(AccentHover, $"YOUR PRIVATE HAND  -  SEAT {local.Seat + 1:00}");
        if (!local.InHand)
        {
            ImGui.TextColored(MutedText, "You will be dealt in at the next hand.");
            ImGui.Spacing();
            return;
        }
        if (pokerLocalCardsHand != pokerHand || pokerLocalFirst < 0 || pokerLocalSecond < 0)
        {
            ImGui.TextColored(MutedText, "Waiting for your private cards. Requesting them again from the dealer...");
            ImGui.Spacing();
            return;
        }

        var draw = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var cardWidth = Ui(67f);
        var cardHeight = Ui(79f);
        var gap = Ui(8f);
        var cards = new[] { pokerLocalFirst, pokerLocalSecond };
        for (var index = 0; index < cards.Length; index++)
        {
            var card = cards[index];
            var min = origin + new Vector2(index * (cardWidth + gap), 0f);
            var max = min + new Vector2(cardWidth, cardHeight);
            var suit = PokerSuit(card);
            var suitName = suit switch
            {
                'H' => "HEARTS", 'D' => "DIAMONDS", 'C' => "CLUBS", _ => "SPADES",
            };
            var ink = suit is 'H' or 'D'
                ? ImGui.GetColorU32(new Vector4(.70f, .15f, .23f, 1f))
                : ImGui.GetColorU32(new Vector4(.11f, .10f, .17f, 1f));
            draw.AddRectFilled(min, max, ImGui.GetColorU32(new Vector4(1f, .96f, .87f, 1f)), Ui(4f));
            draw.AddRect(min, max, ImGui.GetColorU32(new Vector4(.83f, .64f, .35f, 1f)), Ui(4f));
            UiFonts.DrawText(draw, ImGui.GetFontSize() * 1.65f, min + UiVec(9f, 8f), ink, PokerRank(card));
            UiFonts.DrawText(draw, ImGui.GetFontSize() * .72f, min + UiVec(6f, 53f), ink, suitName);
            if (ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(min, max))
                UiFonts.SetTooltip(PokerCardName(card));
        }
        ImGui.Dummy(new Vector2(cardWidth * 2f + gap, cardHeight + Ui(5f)));
        ImGui.Spacing();
    }

    private PokerPreviewControl PokerControlAt(Vector2 uv)
    {
        if (uv.X is < 0f or > 1f || uv.Y is < 0f or > 1f) return PokerPreviewControl.None;
        var x = uv.X * WhiteboardRenderer.Width;
        var y = uv.Y * WhiteboardRenderer.Height;
        if (pokerPreviewOverlay != PokerPreviewOverlay.None)
        {
            if (pokerPreviewOverlay == PokerPreviewOverlay.Settings &&
                x is >= 440f and <= 840f && y is >= 455f and <= 500f)
                return PokerPreviewControl.Music;
            if (x is >= 870f and <= 915f && y is >= 183f and <= 218f)
                return PokerPreviewControl.Close;
            return PokerPreviewControl.Overlay;
        }
        if (x is >= 1116f and <= 1161f && y is >= 47f and <= 84f)
            return PokerPreviewControl.Help;
        if (x is >= 1173f and <= 1218f && y is >= 47f and <= 84f)
            return PokerPreviewControl.Settings;
        if (!PokerActive || pokerSessionId is null) return PokerPreviewControl.None;
        if (CanDealPokerFromLobby &&
            x >= PokerDealButtonX && x <= PokerDealButtonX + PokerDealButtonWidth &&
            y >= PokerDealButtonY && y <= PokerDealButtonY + PokerDealButtonHeight)
            return PokerPreviewControl.Deal;
        pokerControlSeat = PokerSeatAt(x, y);
        if (pokerControlSeat >= 0)
        {
            var player = pokerPlayers.FirstOrDefault(p => p.Seat == pokerControlSeat);
            if (player is null && LocalPokerPlayer is null) return PokerPreviewControl.Seat;
            if (player?.Id == LocalPokerUserId && pokerPhase != PokerPhase.Playing)
                return PokerPreviewControl.Leave;
        }
        if (pokerPhase == PokerPhase.Playing && LocalPokerPlayer?.Seat == pokerActingSeat &&
            pokerLocalCardsHand == pokerHand && pokerLocalFirst >= 0 && pokerLocalSecond >= 0 &&
            y is >= 487f and <= 529f)
        {
            if (x is >= 390f and <= 510f) return PokerPreviewControl.Fold;
            if (x is >= 515f and <= 635f) return PokerPreviewControl.CheckCall;
            if (x is >= 640f and <= 760f && LocalPokerPlayer is { } me &&
                !me.Acted && me.Stack >= Math.Max(0, pokerCurrentBet - me.Bet) + pokerLastRaise)
                return PokerPreviewControl.BetRaise;
            if (x is >= 765f and <= 885f && LocalPokerPlayer is { } player &&
                (!player.Acted || player.Stack <= Math.Max(0, pokerCurrentBet - player.Bet)))
                return PokerPreviewControl.AllIn;
        }
        return PokerPreviewControl.None;
    }

    private static int PokerSeatAt(float x, float y)
    {
        for (var index = 0; index < 16; index++)
        {
            var (sx, sy, width) = PokerSeatBounds(index);
            if (x >= sx && x <= sx + width && y >= sy && y <= sy + 91) return index;
        }
        return -1;
    }

    private static (int X, int Y, int Width) PokerSeatBounds(int index) => index switch
    {
        < 6 => (52 + index * 198, 105, 188),
        6 => (27, 265, 205),
        7 => (27, 431, 205),
        8 => (1048, 265, 205),
        9 => (1048, 431, 205),
        _ => (52 + (index - 10) * 198, 590, 188),
    };

    internal bool IsPokerTvControlAt(Vector2 uv) => PokerActive &&
        (PokerControlAt(uv) != PokerPreviewControl.None || PokerCardTooltipAt(uv, showPrivate: false) is not null);

    internal void SetPokerTvHover(Vector2? uv)
    {
        if (uv is { } point && PokerCardTooltipAt(point, showPrivate: false) is { } tooltip)
            UiFonts.SetTooltip(tooltip);
    }

    internal void SubmitPokerTvControlAt(Vector2 uv)
    {
        if (PokerActive) SubmitPokerPreviewControlAt(uv);
    }

    private void SubmitPokerPreviewControlAt(Vector2 uv)
    {
        switch (PokerControlAt(uv))
        {
            case PokerPreviewControl.Music:
                Plugin.Cfg.PokerMusicEnabled = !Plugin.Cfg.PokerMusicEnabled;
                Plugin.Cfg.Save();
                pokerMusicPlayer.SetVolume(PokerMusicVolume);
                break;
            case PokerPreviewControl.Help:
                pokerPreviewOverlay = PokerPreviewOverlay.Help;
                break;
            case PokerPreviewControl.Settings:
                pokerPreviewOverlay = PokerPreviewOverlay.Settings;
                break;
            case PokerPreviewControl.Deal:
                DealPokerFromLobby();
                break;
            case PokerPreviewControl.Seat:
                SitPokerSeat(pokerControlSeat);
                break;
            case PokerPreviewControl.Leave:
                RequestLeavePokerSeat();
                break;
            case PokerPreviewControl.Fold:
                RequestPokerAction("fold");
                break;
            case PokerPreviewControl.CheckCall:
                RequestPokerAction(LocalPokerPlayer?.Bet == pokerCurrentBet ? "check" : "call");
                break;
            case PokerPreviewControl.BetRaise:
                RequestPokerAction(pokerCurrentBet == 0 ? "raise" : "raise");
                break;
            case PokerPreviewControl.AllIn:
                RequestPokerAction("allin");
                break;
            case PokerPreviewControl.Close:
            case PokerPreviewControl.Overlay:
                pokerPreviewOverlay = PokerPreviewOverlay.None;
                break;
            default:
                return;
        }
        pokerPageRendered = false;
        whiteboardTvDirty = true;
    }

    private static void DrawPokerActivityPreview(ImDrawListPtr draw, Vector2 min, Vector2 max)
    {
        var size = max - min;
        var gold = ImGui.GetColorU32(new Vector4(.94f, .77f, .42f, 1f));
        var felt = ImGui.GetColorU32(new Vector4(.025f, .31f, .24f, 1f));
        var card = ImGui.GetColorU32(new Vector4(1f, .95f, .84f, 1f));
        var plum = ImGui.GetColorU32(new Vector4(.16f, .11f, .23f, 1f));
        var center = (min + max) * .5f;
        void FillOval(Vector2 radii, uint colour)
        {
            for (var segment = 0; segment < 48; segment++)
            {
                var first = segment * MathF.Tau / 48f;
                var second = (segment + 1) * MathF.Tau / 48f;
                var a = center + new Vector2(MathF.Cos(first) * radii.X, MathF.Sin(first) * radii.Y);
                var b = center + new Vector2(MathF.Cos(second) * radii.X, MathF.Sin(second) * radii.Y);
                draw.AddTriangleFilled(center, a, b, colour);
            }
        }
        FillOval(size * new Vector2(.43f, .32f), gold);
        FillOval(size * new Vector2(.41f, .29f), felt);
        for (var index = 0; index < 5; index++)
        {
            var left = center + new Vector2((index - 2) * size.X * .09f - size.X * .035f, -size.Y * .13f);
            draw.AddRectFilled(left, left + size * new Vector2(.07f, .26f), card, Ui(2f));
        }
        for (var index = 0; index < 6; index++)
        {
            var left = min + new Vector2(size.X * (.12f + index * .135f), size.Y * .02f);
            draw.AddRectFilled(left, left + size * new Vector2(.095f, .13f), plum, Ui(2f));
            draw.AddRect(left, left + size * new Vector2(.095f, .13f), gold, Ui(2f));
            left.Y = max.Y - size.Y * .15f;
            draw.AddRectFilled(left, left + size * new Vector2(.095f, .13f), plum, Ui(2f));
            draw.AddRect(left, left + size * new Vector2(.095f, .13f), gold, Ui(2f));
        }
    }
}
