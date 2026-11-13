using AlphaChannel.Plugin.Auth;
using AlphaChannel.Contracts;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool quickViewBusy;
    private string quickViewInput = "";
    private string? quickViewError;

    private void DrawQuickViewBar()
    {
        using var font=UiFonts.PushRelative(.90f);
        ImGui.SetNextItemWidth(MathF.Max(Ui(120),ImGui.GetContentRegionAvail().X-Ui(98)));
        var enter=ImGui.InputTextWithHint("##quickViewCode","Enter a Discord event code...",ref quickViewInput,32,ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();using(UiDisabled.Scope(quickViewBusy))if(ImGui.Button("Open##quickView",UiVec(88,0))||enter)OpenQuickView(quickViewInput);
        if(quickViewError is not null)DrawSocialHelperWrapped(quickViewError);
        ImGui.Spacing();
    }
    internal void OpenQuickView(string code)
    {
        OpenUi();
        if(CurrentSession is not { } session){quickViewError="Sign in before opening a Quick View Code.";Plugin.ChatGui.Print("[Alpha Channel] "+quickViewError);return;}
        if(quickViewBusy)return;
        code=code.Trim().Trim('`').ToUpperInvariant();
        if(!System.Text.RegularExpressions.Regex.IsMatch(code,@"^EV-[23456789A-HJ-NP-Z]{5}-[23456789A-HJ-NP-Z]{5}$"))
        {quickViewError="Enter a complete EV- event Quick View Code.";Plugin.ChatGui.Print("[Alpha Channel] "+quickViewError);return;}
        quickViewBusy=true;quickViewError=null;
        _=Task.Run(async()=>
        {
            try
            {
                var result=await DiscordFeedClient.Resolve(session.Token,code,youtubeFeedToken);
                await Plugin.Framework.RunOnFrameworkThread(()=>
                {
                    if(!IsCurrentMessageSession(session))return;
                    if(result.Kind=="event"&&result.EventId is { } eventId){currentPage=HomePage.Events;ReloadEventDetails(eventId);}
                    else{quickViewError="That code is unavailable or has expired.";Plugin.ChatGui.Print("[Alpha Channel] "+quickViewError);}
                });
            }
            catch{await Plugin.Framework.RunOnFrameworkThread(()=>{quickViewError="Quick view is unavailable. Please try again.";Plugin.ChatGui.Print("[Alpha Channel] "+quickViewError);});}
            finally{await Plugin.Framework.RunOnFrameworkThread(()=>quickViewBusy=false);}
        });
    }
}
