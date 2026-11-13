using AlphaChannel.Plugin.Auth;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;
internal sealed partial class MainWindow
{
    private JsonElement alphaChatRestrictions;
#if DEBUG
    private bool alphaChatRestrictionsOpen;
    private sealed record AlphaChatModeration(string Action,AlphaChatUser User,long Sequence);
    private AlphaChatModeration? alphaChatModeration;
    private int alphaChatKickMinutes=30;
    private string alphaChatModerationReason="";
    private void RequestAlphaChatModeration(string action,AlphaChatUser user,long sequence=0)
    { alphaChatModeration=new(action,user,sequence); alphaChatModerationReason=""; }
#endif
    private void DrawAlphaChatModerationOverlay()
    {
#if DEBUG
        if(!alphaChatCanModerate || !alphaChat.Connected) { alphaChatModeration=null; alphaChatRestrictionsOpen=false; return; }
        if(alphaChatModeration is null && !alphaChatRestrictionsOpen) return;
        var parent=ImGui.GetWindowPos(); var parentSize=ImGui.GetWindowSize();
        var size=new Vector2(MathF.Min(Ui(480f),parentSize.X-Ui(32f)),MathF.Min(Ui(alphaChatRestrictionsOpen?460f:280f),parentSize.Y-Ui(32f)));
        var position=parent+(parentSize-size)*.5f;
        ImGui.SetNextWindowPos(parent,ImGuiCond.Always); ImGui.SetNextWindowSize(parentSize,ImGuiCond.Always); ImGui.SetNextWindowBgAlpha(0f);
        var flags=ImGuiWindowFlags.NoTitleBar|ImGuiWindowFlags.NoResize|ImGuiWindowFlags.NoMove|ImGuiWindowFlags.NoSavedSettings|ImGuiWindowFlags.NoBackground|ImGuiWindowFlags.NoScrollbar|ImGuiWindowFlags.NoDocking;
        if(!BeginWindowOverlay("##alphaChatModeration",flags)) { ImGui.End(); return; }
        ImGui.GetWindowDrawList().AddRectFilled(parent,parent+parentSize,ImGui.GetColorU32(new Vector4(0,0,0,.54f)));
        ImGui.SetCursorScreenPos(position);
        using(ImRaii.PushStyle(ImGuiStyleVar.ChildRounding,Ui(14f)).Push(ImGuiStyleVar.WindowPadding,UiVec(20f,18f)))
        using(ImRaii.PushColor(ImGuiCol.ChildBg,CardBg).Push(ImGuiCol.Border,AccentHover))
        using(var panel=UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##alphaChatModerationPanel",size,true)))
        {
            if(panel)
            {
                if(alphaChatRestrictionsOpen)
                {
                    ImGui.TextUnformatted("Alpha Chat restrictions");
                    using(var list=UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##restrictionsList",new Vector2(0,size.Y-Ui(100f)),false)))
                        if(list.Success && alphaChatRestrictions.ValueKind==JsonValueKind.Array)
                            foreach(var row in alphaChatRestrictions.EnumerateArray())
                            {
                                var account=row.GetProperty("account_id").GetString()!;
                                using var id=ImRaii.PushId(account);
                                DrawUsername(account,alphaChatUsers.FirstOrDefault(u=>u.AccountId==account)?.DisplayName??account);
                                var expires=row.GetProperty("expires").GetInt64();
                                ImGui.TextColored(MutedText,expires==0?"Banned":$"Until {DateTimeOffset.FromUnixTimeMilliseconds(expires).LocalDateTime:g}");
                                if(ImGui.Button("Restore chat access")) RequestAlphaChatModeration("unban",new(account,account,null,"#9966FA",null,0));
                                ImGui.Separator();
                            }
                    if(ImGui.Button("Close")) alphaChatRestrictionsOpen=false;
                }
                if(alphaChatModeration is { } action)
                {
                    alphaChatRestrictionsOpen=false;
                    DrawUsername(action.User.AccountId,action.Action=="delete"?"Delete this message?":$"{action.Action} {action.User.DisplayName}?");
                    ImGui.Spacing();
                    if(action.Action=="kick")
                    { ImGui.SetNextItemWidth(Ui(150f)); ImGui.InputInt("Minutes",ref alphaChatKickMinutes); alphaChatKickMinutes=Math.Clamp(alphaChatKickMinutes,1,43200); }
                    ImGui.SetNextItemWidth(-1); ImGui.InputTextWithHint("##moderationReason","Reason (optional)",ref alphaChatModerationReason,200);
                    ImGui.Spacing(); ImGui.TextWrapped("This action affects the real Alpha Chat service."); ImGui.Spacing();
                    if(ActivePartyButton("Confirm",UiVec(120f,36f),danger:true))
                    { _=alphaChat.CommandAsync(new {type="moderate",action=action.Action,accountId=action.User.AccountId,sequence=action.Sequence,minutes=alphaChatKickMinutes,reason=alphaChatModerationReason}); alphaChatModeration=null; _=alphaChat.CommandAsync(new{type="restrictions"}); }
                    ImGui.SameLine(); if(ActivePartyButton("Cancel",UiVec(120f,36f))) alphaChatModeration=null;
                }
            }
        }
        if(ImGui.IsKeyPressed(ImGuiKey.Escape)) { alphaChatModeration=null; alphaChatRestrictionsOpen=false; }
        ImGui.End();
#endif
    }
}
