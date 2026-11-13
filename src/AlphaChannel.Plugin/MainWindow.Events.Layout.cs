using AlphaChannel.Plugin.Auth;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private string eventsFilterWorldSearch="";
    private readonly HashSet<string> eventsExpandedSeries=[];

    private void DrawEventsToolbar()
    {
        var width=ImGui.GetContentRegionAvail().X;
        var narrow=width<Ui(590);
        var origin=ImGui.GetCursorScreenPos();
        var view=eventsMyView?2:eventsCalendarView?1:0;
        EventUnderlineTabs("eventsNavigation",["Explore","Calendar","My events"],ref view,MathF.Min(Ui(330),narrow?width:width-Ui(208)),40);
        if(view!=(eventsMyView?2:eventsCalendarView?1:0))
        {eventsMyView=view==2;eventsCalendarView=view==1;eventsSelectedDay=null;eventsFilter=0;}
        if(!narrow)ImGui.SetCursorScreenPos(origin+new Vector2(width-Ui(192),0));
        using(UiDisabled.Scope(eventsSyncBusy))
            if(AlphaChatButton("eventsRefresh","",FontAwesomeIcon.SyncAlt,Ui(36),Ui(36)))RefreshEventsNow();
        if(ImGui.IsItemHovered())UiFonts.SetTooltip("Refresh events");
        ImGui.SameLine(0,Ui(10));
        if(AlphaChatButton("eventsCreate","Create event",FontAwesomeIcon.Plus,Ui(146),Ui(36),primary:true))OpenEventEditor(null,false);
        ImGui.SetCursorScreenPos(origin+new Vector2(0,Ui(narrow?80:40)));
        DrawEventsToolbarRule(width);ImGui.Spacing();

        var search=ImGui.GetCursorScreenPos();
        var filterCount=(eventsWorldFilter.Length>0?1:0)+(eventsCategoryFilter.Length>0?1:0)+(eventsHideAdult?1:0);
        ImGui.SetNextItemWidth(narrow?width:MathF.Max(Ui(100),width-Ui(294)));
        using(ImRaii.PushStyle(ImGuiStyleVar.FramePadding,UiVec(30,7)))
            ImGui.InputTextWithHint("##eventSearch","Search events…",ref eventsSearch,240);
        var searchEnd=ImGui.GetItemRectMax();
        DrawUiIcon(FontAwesomeIcon.Search,Ui(14),search+new Vector2(Ui(10),(searchEnd.Y-search.Y-Ui(14))*.5f),MutedText);
        if(ImGui.IsItemHovered())UiFonts.SetTooltip("Search events, organisers or locations");
        if(!narrow)ImGui.SameLine(0,Ui(10));
        ImGui.SetNextItemWidth(narrow?(width-Ui(130)):Ui(154));
        using(ImRaii.PushColor(ImGuiCol.Button,FrameBg).Push(ImGuiCol.ButtonHovered,AccentInk(.14f)).Push(ImGuiCol.ButtonActive,AccentInk(.22f)))
        {
            if(UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo("##eventWorldFilter",eventsWorldFilter.Length==0?"All worlds":eventsWorldFilter,ImGuiComboFlags.HeightLarge)))
            {
                DrawEventsWorldFilter();ImGui.EndCombo();
            }
        }
        ImGui.SameLine(0,Ui(10));
        if(AlphaChatButton("eventFilters",filterCount>0?$"Filters ({filterCount})":"Filters",FontAwesomeIcon.Filter,Ui(120),Ui(32)))ImGui.OpenPopup("##eventsFiltersPopup");
        if(ImGui.IsPopupOpen("##eventsFiltersPopup"))
        {
            var root=ImGuiP.GetCurrentWindow().RootWindow;
            var popupSize=new Vector2(MathF.Min(Ui(292),root.Size.X-Ui(24)),Ui(236));
            PositionPartyChatPopup(popupSize);
            ImGui.SetNextWindowSize(new Vector2(popupSize.X,0),ImGuiCond.Always);
        }
        using(var filterStyle=ImRaii.PushStyle(ImGuiStyleVar.PopupRounding,Ui(10))
            .Push(ImGuiStyleVar.PopupBorderSize,Ui(1)).Push(ImGuiStyleVar.WindowPadding,UiVec(14,12))
            .Push(ImGuiStyleVar.ItemSpacing,UiVec(8,8)))
        using(var filterColors=ImRaii.PushColor(ImGuiCol.PopupBg,CardBg).Push(ImGuiCol.Border,AccentInk(.65f)))
        using(var popup=UiFonts.Window(ImGui.GetFontSize(), ImRaii.Popup("##eventsFiltersPopup")))
        {
            if(popup)
            {
                var filterWidth=ImGui.GetContentRegionAvail().X;
                DrawSocialHeading("Filter events");ImGui.Spacing();
                using(ImRaii.PushColor(ImGuiCol.ChildBg,Vector4.Zero))
                using(var box=UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##eventFilterFields",new Vector2(filterWidth,Ui(108)),false)))
                {
                    if(box){EventCategoryPicker("Category",ref eventsCategoryFilter,true);ImGui.Spacing();EventCheckbox("Hide 18+ events",ref eventsHideAdult);}
                }
                if(ImGui.Button("Clear filters",new Vector2((filterWidth-Ui(8))*.5f,Ui(30))))ClearEventsFilters();
                ImGui.SameLine(0,Ui(8));if(ImGui.Button("Done",new Vector2((filterWidth-Ui(8))*.5f,Ui(30))))ImGui.CloseCurrentPopup();
            }
        }
        ImGui.SetCursorScreenPos(search+new Vector2(0,Ui(narrow?80:44)));
        DrawEventsFilterChips(width);
        var statusOrigin=ImGui.GetCursorScreenPos();
        if(eventsMyView)EventUnderlineTabs("eventsMine",["Going","Organising","Past"],ref eventsMyTab,MathF.Min(Ui(320),width),34);
        else EventUnderlineTabs("eventsStatus",[eventsCalendarView?"All events":"Upcoming","Live now"],ref eventsFilter,Ui(226),34,liveDot:true);
        if(!eventsCalendarView&&!eventsMyView)
        {
            if(!narrow)ImGui.SetCursorScreenPos(statusOrigin+new Vector2(width-Ui(165),0));
            ImGui.SetNextItemWidth(Ui(165));
            var periods=EventBrowseRange.Labels;
            using(ImRaii.PushColor(ImGuiCol.Button,FrameBg).Push(ImGuiCol.ButtonHovered,AccentInk(.14f)).Push(ImGuiCol.ButtonActive,AccentInk(.22f)))
                if(UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo("##eventsDate",periods[eventsDateFilter])))
                {for(var i=0;i<periods.Length;i++)if(ImGui.Selectable(periods[i],eventsDateFilter==i))eventsDateFilter=i;ImGui.EndCombo();}
        }
        ImGui.SetCursorScreenPos(statusOrigin+new Vector2(0,Ui(!eventsCalendarView&&!eventsMyView&&narrow?74:34)));
        DrawEventsToolbarRule(width);ImGui.Spacing();
        if(eventsMyView&&eventsMyTab==2)
        {
            DrawSocialHelper($"{DateTime.Today.AddDays(-eventsPastDays):d MMM yyyy} — {DateTime.Today.AddDays(-eventsPastDays+60):d MMM yyyy}");
            using(UiDisabled.Scope(eventsPastDays<=60))if(ImGui.SmallButton("Newer"))eventsPastDays-=60;
            ImGui.SameLine();using(UiDisabled.Scope(eventsPastDays>=360))if(ImGui.SmallButton("Older"))eventsPastDays+=60;
        }
    }

    private void DrawEventsToolbarRule(float width)
    {
        var p=ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddLine(p,p+new Vector2(width,0),ImGui.GetColorU32(ImGuiCol.Separator));
        ImGui.Dummy(new Vector2(width,Ui(1)));
    }

    private void DrawEventsWorldFilter()
    {
        ImGui.SetNextItemWidth(Ui(250));ImGui.InputTextWithHint("##filterWorldSearch","Find a world or data centre…",ref eventsFilterWorldSearch,80);
        if(ImGui.Selectable("All worlds",eventsWorldFilter.Length==0)){eventsWorldFilter="";ImGui.CloseCurrentPopup();}
        using var list=UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##eventFilterWorldList",UiVec(260,220),false));
        if(!list)return;
        if("Online".Contains(eventsFilterWorldSearch,StringComparison.OrdinalIgnoreCase)&&ImGui.Selectable("Online · Any world",eventsWorldFilter=="Online")){eventsWorldFilter="Online";ImGui.CloseCurrentPopup();}
        foreach(var centre in FfxivEventWorlds.All.Select(w=>w.Centre).Distinct().Order())
            if(centre.Contains(eventsFilterWorldSearch,StringComparison.OrdinalIgnoreCase)&&ImGui.Selectable(centre+" · All worlds",eventsWorldFilter==centre)){eventsWorldFilter=centre;ImGui.CloseCurrentPopup();}
        foreach(var world in FfxivEventWorlds.All.Where(w=>(w.Name+" "+w.Centre).Contains(eventsFilterWorldSearch,StringComparison.OrdinalIgnoreCase)))
            if(ImGui.Selectable(world.Name+" · "+world.Centre,eventsWorldFilter==world.Name)){eventsWorldFilter=world.Name;ImGui.CloseCurrentPopup();}
    }

    private void ClearEventsFilters(){eventsWorldFilter="";eventsCategoryFilter="";eventsHideAdult=false;}
    private void DrawEventsFilterChips(float width)
    {
        var x=ImGui.GetCursorScreenPos().X;
        var any=false;
        void Chip(string id,string label,Action remove)
        {
            var chipWidth=ImGui.CalcTextSize(label+"  ×").X+Ui(22);
            if(any&&ImGui.GetItemRectMax().X+Ui(8)+chipWidth<=x+width)ImGui.SameLine(0,Ui(8));
            using var colors=ImRaii.PushColor(ImGuiCol.Button,AccentInk(.12f)).Push(ImGuiCol.Border,AccentInk(.55f));
            if(ImGui.Button(label+"  ×##"+id,new Vector2(MathF.Min(width,chipWidth),Ui(28))))remove();
            any=true;
        }
        if(eventsWorldFilter.Length>0)Chip("worldChip",eventsWorldFilter,()=>eventsWorldFilter="");
        if(eventsCategoryFilter.Length>0)Chip("categoryChip",eventsCategoryFilter,()=>eventsCategoryFilter="");
        if(eventsHideAdult)Chip("adultChip","Hide 18+",()=>eventsHideAdult=false);
        if(any)
        {
            if(ImGui.GetItemRectMax().X+Ui(60)<=x+width)ImGui.SameLine(0,Ui(12));
            using(ImRaii.PushColor(ImGuiCol.Text,AccentHover))ImGui.TextUnformatted("Clear");
            if(ImGui.IsItemClicked())ClearEventsFilters();
            if(ImGui.IsItemHovered())ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            ImGui.Spacing();
        }
    }

    private void EventUnderlineTabs(string id,string[] labels,ref int selected,float width,float height,bool liveDot=false)
    {
        var origin=ImGui.GetCursorScreenPos();var draw=ImGui.GetWindowDrawList();var cellWidth=width/labels.Length;var h=Ui(height);
        for(var i=0;i<labels.Length;i++)
        {
            var p=origin+new Vector2(i*cellWidth,0);var size=new Vector2(cellWidth,h);
            ImGui.SetCursorScreenPos(p);
            if(ImGui.InvisibleButton("##"+id+i,size))selected=i;
            var hovered=ImGui.IsItemHovered();
            if(hovered){ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);draw.AddRectFilled(p,p+size,ImGui.GetColorU32(AccentInk(.06f)),Ui(5));}
            var text=ImGui.CalcTextSize(labels[i]);var dot=liveDot&&i==1;var left=(cellWidth-text.X-(dot?Ui(16):0))*.5f;
            if(dot)draw.AddCircleFilled(p+new Vector2(left+Ui(4),h*.5f),Ui(4),ImGui.GetColorU32(events.Any(e=>EventSchedule.Live(e,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))?EventGreen:MutedText));
            draw.AddText(p+new Vector2(left+(dot?Ui(16):0),(h-text.Y)*.5f),ImGui.GetColorU32(selected==i?(id=="eventsNavigation"?Vector4.One:AccentHover):hovered?Vector4.One:MutedText),labels[i]);
            if(selected==i)draw.AddRectFilled(p+new Vector2(0,h-Ui(2)),p+size,ImGui.GetColorU32(Accent));
        }
        ImGui.SetCursorScreenPos(origin);ImGui.Dummy(new Vector2(width,h));
    }
}
