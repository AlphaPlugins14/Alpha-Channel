using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private static readonly Vector4 RouletteLedgerGreen = new(0.025f, 0.22f, 0.145f, 1f);
    private static readonly Vector4 RouletteLedgerGreenAlt = new(0.028f, 0.19f, 0.13f, 1f);
    private static readonly Vector4 RouletteLedgerGold = new(0.84f, 0.71f, 0.34f, 1f);

    private void DrawRouletteLedgerColumns(ImDrawListPtr draw, Vector2 origin, float height, float width, bool labels)
    {
        var columns = new[] { 0f, 0.25f, 0.43f, 0.65f, 0.90f, 1f };
        for (var index = 1; index < columns.Length - 1; index++)
        {
            var x = origin.X + width * columns[index];
            draw.AddLine(new Vector2(x, origin.Y), new Vector2(x, origin.Y + height),
                ImGui.GetColorU32(new Vector4(RouletteLedgerGold.X, RouletteLedgerGold.Y, RouletteLedgerGold.Z, 0.72f)));
        }
        if (!labels) return;
        string[] headings = ["PLAYER", "CHIPS", "LAST BET", "LAST CHANGE"];
        for (var index = 0; index < headings.Length; index++)
        {
            var minX = origin.X + width * columns[index];
            var maxX = origin.X + width * columns[index + 1];
            var textSize = ImGui.CalcTextSize(headings[index]);
            draw.AddText(new Vector2((minX + maxX - textSize.X) * 0.5f, origin.Y + Ui(9f)),
                ImGui.GetColorU32(RouletteLedgerGold), headings[index]);
        }
    }

    private void DrawRouletteLedgerRow(RoulettePlayerState player, int index)
    {
        var ledger = EnsureRouletteLedgerPlayer(player.Id);
        var width = ImGui.GetContentRegionAvail().X;
        var height = Ui(52f);
        var origin = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(width, height));
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + new Vector2(width, height),
            ImGui.GetColorU32(index % 2 == 0 ? RouletteLedgerGreenAlt : RouletteLedgerGreen));
        draw.AddLine(origin + new Vector2(0f, height), origin + new Vector2(width, height),
            ImGui.GetColorU32(new Vector4(RouletteLedgerGold.X, RouletteLedgerGold.Y, RouletteLedgerGold.Z, 0.42f)));
        var circle = origin + UiVec(20f, 26f);
        draw.AddCircleFilled(circle, Ui(15f), ImGui.GetColorU32(new Vector4(0.12f, 0.10f, 0.22f, 1f)));
        draw.AddCircle(circle, Ui(15f), ImGui.GetColorU32(player.Id == LocalRouletteUserId ? AccentHover : RouletteLedgerGold), 24, Ui(2f));
        DrawAvatarIcon(draw, circle, Ui(30f));
        DrawUsernameAt(draw,origin + UiVec(43f, 18f),player.Name,Vector4.One,player.Id);
        if (player.Id == LocalRouletteUserId)
            DrawRouletteLedgerMiniBadge(draw, origin + new Vector2(width * 0.25f - Ui(48f), Ui(15f)), "YOU");

        if (!ledger.HasJoinedTable)
        {
            draw.AddText(origin + new Vector2(width * 0.25f + Ui(18f), Ui(18f)),
                ImGui.GetColorU32(new Vector4(RouletteLedgerGold.X, RouletteLedgerGold.Y, RouletteLedgerGold.Z, 0.78f)),
                "Waiting for player to join the table...");
            return;
        }

        DrawRouletteLedgerColumns(draw, origin, height, width, false);

        var chipsText = player.Chips.ToString();
        UiFonts.DrawText(draw, ImGui.GetFontSize() * 1.28f, origin + new Vector2(width * 0.25f + Ui(17f), Ui(15f)), ImGui.GetColorU32(RouletteLedgerGold), chipsText);
        var canTransfer = player.Id != LocalRouletteUserId && CanLocalTransferRouletteChipsTo(player.Id);
        var sendOrigin = origin + new Vector2(width * 0.43f - Ui(38f), Ui(10f));
        ImGui.SetCursorScreenPos(sendOrigin);
        using (UiDisabled.Scope(!canTransfer))
        {
            ImGui.PushID("rouletteSend" + player.Id);
            var sendClicked = ImGui.InvisibleButton("##send", UiVec(28f, 32f));
            var sendHovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
            ImGui.PopID();
            DrawRouletteLedgerIconButton(draw, sendOrigin, UiVec(28f, 32f), FontAwesomeIcon.ArrowRight,
                canTransfer && sendHovered, canTransfer);
            if (sendHovered && player.Id != LocalRouletteUserId)
                UiFonts.SetTooltip(canTransfer ? $"Send chips to {player.Name}" : "Place your first bet before sending chips");
            if (sendClicked && canTransfer)
                OpenRouletteTransferPopup(player.Id);
        }

        var lastBet = string.IsNullOrWhiteSpace(ledger.LastBet) ? "—" : ledger.LastBet.ToUpperInvariant();
        draw.AddText(origin + new Vector2(width * 0.43f + Ui(16f), Ui(18f)), ImGui.GetColorU32(new Vector4(0.94f, 0.95f, 0.91f, 1f)), lastBet);
        var changeColour = RouletteLedgerChangeColour(ledger.ChangeKind);
        var changeText = string.IsNullOrWhiteSpace(ledger.ChangeText) ? "—" : ledger.ChangeText.ToUpperInvariant();
        draw.AddText(origin + new Vector2(width * 0.65f + Ui(16f), Ui(18f)), ImGui.GetColorU32(changeColour), changeText);

        var historyOrigin = origin + new Vector2(width * 0.90f + (width * 0.10f - Ui(30f)) * 0.5f, Ui(10f));
        ImGui.SetCursorScreenPos(historyOrigin);
        ImGui.PushID("rouletteHistory" + player.Id);
        ImGui.InvisibleButton("##history", UiVec(30f, 32f));
        var historyHovered = ImGui.IsItemHovered();
        ImGui.PopID();
        DrawRouletteLedgerIconButton(draw, historyOrigin, UiVec(30f, 32f), FontAwesomeIcon.List, historyHovered, true);
        if (historyHovered)
            UiFonts.SetTooltip(BuildRouletteLedgerTooltip(player, ledger));

        ImGui.SetCursorScreenPos(origin + new Vector2(0f, height));
    }

    private static void DrawRouletteLedgerMiniBadge(ImDrawListPtr draw, Vector2 origin, string text)
    {
        var textSize = ImGui.CalcTextSize(text);
        var size = textSize + UiVec(14f, 8f);
        draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(new Vector4(0.27f, 0.18f, 0.55f, 1f)), Ui(4f));
        draw.AddRect(origin, origin + size, ImGui.GetColorU32(AccentHover), Ui(4f));
        draw.AddText(origin + (size - textSize) * 0.5f, ImGui.GetColorU32(Vector4.One), text);
    }

    private static void DrawRouletteLedgerIconButton(ImDrawListPtr draw, Vector2 origin, Vector2 size,
        FontAwesomeIcon icon, bool hovered, bool enabled)
    {
        var fill = hovered ? new Vector4(0.18f, 0.14f, 0.28f, 1f) : new Vector4(0.025f, 0.12f, 0.09f, 1f);
        draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(fill), Ui(5f));
        draw.AddRect(origin, origin + size, ImGui.GetColorU32(enabled ? RouletteLedgerGold : MutedText), Ui(5f));
        var text = icon.ToIconString();
        Vector2 textSize;
        using (UiFonts.PushIcon()) textSize = ImGui.CalcTextSize(text);
        UiFonts.DrawIcon(draw, ImGui.GetFontSize(), origin + (size - textSize) * 0.5f, ImGui.GetColorU32(enabled ? RouletteLedgerGold : MutedText), text);
    }

    private static Vector4 RouletteLedgerChangeColour(RouletteLedgerChangeKind kind) => kind switch
    {
        RouletteLedgerChangeKind.Win or RouletteLedgerChangeKind.TransferIn => new Vector4(0.18f, 0.88f, 0.48f, 1f),
        RouletteLedgerChangeKind.Loss or RouletteLedgerChangeKind.TransferOut or RouletteLedgerChangeKind.Bet => new Vector4(0.96f, 0.30f, 0.38f, 1f),
        RouletteLedgerChangeKind.Reset or RouletteLedgerChangeKind.Refund => RouletteLedgerGold,
        _ => new Vector4(0.88f, 0.90f, 0.94f, 1f),
    };

    private static string BuildRouletteLedgerTooltip(RoulettePlayerState player, RouletteLedgerPlayerState ledger)
    {
        if (ledger.RoundDetails.Length == 0)
            return $"{player.Name}\nNo completed round details yet.";
        var text = new StringBuilder($"{player.Name} — Round {ledger.DetailRound}\n\n");
        var totalBet = 0;
        var totalPayout = 0;
        foreach (var detail in ledger.RoundDetails)
        {
            totalBet += detail.Amount;
            totalPayout += detail.Payout;
            text.Append(detail.Amount).Append(" on ").Append(detail.Target).Append("   ")
                .Append(detail.Won ? $"Won +{detail.Payout}" : $"Lost -{detail.Amount}").AppendLine();
        }
        text.Append("\nTotal bet: ").Append(totalBet)
            .Append("\nRound change: ").Append(totalPayout - totalBet >= 0 ? "+" : string.Empty)
            .Append(totalPayout - totalBet);
        return text.ToString();
    }

    private bool CanLocalTransferRouletteChipsTo(string recipientId) =>
        RouletteActive && recipientId != LocalRouletteUserId &&
        roulettePlayers.TryGetValue(LocalRouletteUserId, out var local) && local.Chips > 0 &&
        rouletteLedger.TryGetValue(LocalRouletteUserId, out var localLedger) && localLedger.HasJoinedTable &&
        rouletteLedger.TryGetValue(recipientId, out var recipientLedger) && recipientLedger.HasJoinedTable;

    private void OpenRouletteTransferPopup(string recipientId)
    {
        if (!CanLocalTransferRouletteChipsTo(recipientId)) return;
        rouletteTransferRecipientId = recipientId;
        rouletteTransferAmount = Math.Min(25, roulettePlayers[LocalRouletteUserId].Chips);
        rouletteTransferError = null;
        rouletteTransferPopupOpen = true;
    }

    private void CloseRouletteTransferPopup()
    {
        rouletteTransferPopupOpen = false;
        rouletteTransferRecipientId = null;
        rouletteTransferError = null;
    }

    private void SubmitRouletteTransfer()
    {
        if (rouletteSessionId is null || rouletteTransferRecipientId is null ||
            !roulettePlayers.TryGetValue(LocalRouletteUserId, out var local) || rouletteTransferAmount <= 0)
            return;
        if (rouletteTransferAmount > local.Chips)
        {
            rouletteTransferError = "You do not have enough available chips.";
            return;
        }
        if (!CanLocalTransferRouletteChipsTo(rouletteTransferRecipientId))
        {
            rouletteTransferError = "This transfer is no longer available.";
            return;
        }

        if (Plugin.Cfg.RouletteSoundEffectsEnabled)
            rouletteChipSoundPlayer.Play(Plugin.Cfg.Muted ? 0 : Plugin.Cfg.Volume);
        if (IsLocalActivityHost)
            HostTransferRouletteChips(LocalRouletteUserId, rouletteTransferRecipientId, rouletteTransferAmount);
        else if (!SendArcadePlayerAction("roulette", $"T|{rouletteSessionId}|{rouletteTransferRecipientId}|{rouletteTransferAmount}"))
        { rouletteTransferError = "Waiting for previous actions to finish. Please try again."; return; }
        CloseRouletteTransferPopup();
    }

    private void DrawRouletteTransferOverlay()
    {
        if (!rouletteTransferPopupOpen || rouletteTransferRecipientId is null ||
            !roulettePlayers.TryGetValue(rouletteTransferRecipientId, out var recipient) ||
            !roulettePlayers.TryGetValue(LocalRouletteUserId, out var local))
            return;
        if (!CanLocalTransferRouletteChipsTo(rouletteTransferRecipientId))
        {
            CloseRouletteTransferPopup();
            return;
        }
        rouletteTransferAmount = Math.Clamp(rouletteTransferAmount, 1, local.Chips);

        var parentPos = ImGui.GetWindowPos();
        var parentSize = ImGui.GetWindowSize();
        var panelSize = new Vector2(MathF.Min(Ui(520f), parentSize.X - Ui(40f)), Ui(350f));
        ImGui.SetNextWindowPos(parentPos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(parentSize, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoBackground;
        if (!BeginWindowOverlay("##rouletteTransferOverlay", flags))
        {
            ImGui.End();
            return;
        }

        ImGui.GetWindowDrawList().AddRectFilled(parentPos, parentPos + parentSize,
            ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.58f)));
        ImGui.SetCursorScreenPos(parentPos + (parentSize - panelSize) * 0.5f);
        var cancel = false;
        var submit = false;
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(14f))
                   .Push(ImGuiStyleVar.ChildBorderSize, Ui(1.5f))
                   .Push(ImGuiStyleVar.WindowPadding, UiVec(24f, 20f)))
        using (ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.025f, 0.09f, 0.07f, 0.995f))
                   .Push(ImGuiCol.Border, new Vector4(Accent.X, Accent.Y, Accent.Z, 0.90f)))
        using (var card = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##rouletteTransferCard", panelSize, true,
                   ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)))
        {
            if (card)
            {
                DrawSectionTitle(FontAwesomeIcon.ArrowRight, $"Send chips to {recipient.Name}");
                ImGui.Separator();
                ImGui.Dummy(UiVec(0f, 10f));
                ImGui.TextColored(MutedText, $"Your available balance: {local.Chips} chips");
                ImGui.Dummy(UiVec(0f, 12f));

                var amountOrigin = ImGui.GetCursorScreenPos();
                var amountWidth = ImGui.GetContentRegionAvail().X;
                var buttonWidth = Ui(46f);
                if (ImGui.Button("−##rouletteTransferMinus", UiVec(buttonWidth, 46f)))
                    rouletteTransferAmount = Math.Max(1, rouletteTransferAmount - 5);
                ImGui.SameLine(0f, Ui(8f));
                using (ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.015f, 0.05f, 0.045f, 1f))
                           .Push(ImGuiCol.Border, RouletteLedgerGold))
                using (var amountPanel = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##rouletteTransferAmount",
                           new Vector2(amountWidth - buttonWidth * 2f - Ui(16f), Ui(46f)), true,
                           ImGuiWindowFlags.NoScrollbar)))
                {
                    if (amountPanel)
                    {
                        var label = $"{rouletteTransferAmount} CHIPS";
                        ImGui.SetCursorPosX((ImGui.GetWindowWidth() - ImGui.CalcTextSize(label).X) * 0.5f);
                        ImGui.SetCursorPosY(Ui(13f));
                        ImGui.TextColored(RouletteLedgerGold, label);
                    }
                }
                ImGui.SameLine(0f, Ui(8f));
                if (ImGui.Button("+##rouletteTransferPlus", UiVec(buttonWidth, 46f)))
                    rouletteTransferAmount = Math.Min(local.Chips, rouletteTransferAmount + 5);

                ImGui.SetCursorScreenPos(amountOrigin + UiVec(0f, 60f));
                int[] presets = [5, 25, 50, 100];
                var presetGap = Ui(8f);
                var presetWidth = (ImGui.GetContentRegionAvail().X - presetGap * 3f) / 4f;
                foreach (var preset in presets)
                {
                    if (preset != presets[0]) ImGui.SameLine(0f, presetGap);
                    using var disabled = UiDisabled.Scope(preset > local.Chips);
                    if (ImGui.Button($"{preset}##rouletteTransferPreset{preset}", UiVec(presetWidth, 34f)))
                        rouletteTransferAmount = preset;
                }

                if (!string.IsNullOrWhiteSpace(rouletteTransferError))
                    ImGui.TextColored(Danger, rouletteTransferError);
                else
                    ImGui.TextColored(MutedText, $"After sending: {local.Chips - rouletteTransferAmount} chips remaining");

                ImGui.SetCursorScreenPos(new Vector2(
                    ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMin().X,
                    ImGui.GetWindowPos().Y + panelSize.Y - Ui(60f)));
                var gap = Ui(10f);
                var actionWidth = (ImGui.GetContentRegionAvail().X - gap) * 0.5f;
                cancel = ImGui.Button("Cancel##rouletteTransferCancel", UiVec(actionWidth, 40f));
                ImGui.SameLine(0f, gap);
                using (ImRaii.PushColor(ImGuiCol.Button, Accent).Push(ImGuiCol.ButtonHovered, AccentHover)
                           .Push(ImGuiCol.ButtonActive, AccentActive))
                using (UiDisabled.Scope(rouletteTransferAmount <= 0 || rouletteTransferAmount > local.Chips))
                    submit = ImGui.Button($"Send {rouletteTransferAmount}##rouletteTransferSubmit", UiVec(actionWidth, 40f));
            }
        }
        if (cancel) CloseRouletteTransferPopup();
        if (submit) SubmitRouletteTransfer();
        ImGui.End();
    }
}
