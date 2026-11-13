using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private static readonly Vector4 EventGreen=new(.2f,.85f,.45f,1);
    private static readonly Vector4 EventOrange=new(1,.63f,.28f,1);
    private bool EventCheckbox(string label,ref bool value)
    {
        using(ImRaii.PushStyle(ImGuiStyleVar.FramePadding,UiVec(2,2)).Push(ImGuiStyleVar.FrameRounding,Ui(4)))
            return ImGui.Checkbox(label,ref value);
    }
    private void EventSectionHeading(string label,FontAwesomeIcon icon)
    {
        ImGui.Spacing();
        if(UiFonts.Active)
        {
            var p=ImGui.GetCursorScreenPos();var size=MeasureUiIcon(icon,ImGui.GetFontSize());
            DrawUiIcon(icon,ImGui.GetFontSize(),p,AccentHover);ImGui.Dummy(size);
        }
        else using(UiFonts.PushIcon())ImGui.TextColored(AccentHover,icon.ToIconString());
        ImGui.SameLine(0,Ui(8));DrawSocialHeading(label);ImGui.Spacing();
    }

    private void DrawEventCalendarTooltip(CommunityEvent e)
    {
        using var style=ImRaii.PushStyle(ImGuiStyleVar.PopupRounding,Ui(10))
            .Push(ImGuiStyleVar.PopupBorderSize,Ui(1)).Push(ImGuiStyleVar.WindowPadding,UiVec(14,12))
            .Push(ImGuiStyleVar.ItemSpacing,UiVec(8,6));
        using var colors=ImRaii.PushColor(ImGuiCol.PopupBg,CardBg).Push(ImGuiCol.Border,AccentInk(.65f));
        UiFonts.BeginTooltip();
        using(ImRaii.TextWrapPos(Ui(340)))
        {
            DrawSocialHeading(e.Title);
            DrawSocialHelper(EventLocalTime(e.StartAt)+" — "+DateTimeOffset.FromUnixTimeMilliseconds(e.EndAt).ToLocalTime().ToString("HH:mm"));
            DrawSocialHelper(e.World+" · "+e.Location);
            DrawSocialHelper(e.Category);
            if(e.Adult)ImGui.TextColored(EventOrange,"18+ · Adults only");
        }
        ImGui.EndTooltip();
    }
    private void EventSegments(string id,string[] labels,ref int selected,float width,float height=32)
    {
        var origin=ImGui.GetCursorScreenPos();var draw=ImGui.GetWindowDrawList();var h=Ui(height);
        draw.AddRectFilled(origin,origin+new Vector2(width,h),ImGui.GetColorU32(FrameBg),Ui(8));
        draw.AddRect(origin,origin+new Vector2(width,h),ImGui.GetColorU32(AccentInk(.35f)),Ui(8));
        for(var i=0;i<labels.Length;i++)
        {
            var min=origin+new Vector2(width*i/labels.Length,0);var cell=new Vector2(width/labels.Length,h);
            ImGui.SetCursorScreenPos(min);
            if(ImGui.InvisibleButton("##"+id+i,cell))selected=i;
            if(selected==i||ImGui.IsItemHovered())draw.AddRectFilled(min+UiVec(3,3),min+cell-UiVec(3,3),ImGui.GetColorU32(selected==i?AccentInk(.32f):CardBgHover),Ui(6));
            if(ImGui.IsItemHovered())ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            var text=ImGui.CalcTextSize(labels[i]);draw.AddText(min+(cell-text)*.5f,ImGui.GetColorU32(selected==i?Vector4.One:MutedText),labels[i]);
        }
        ImGui.SetCursorScreenPos(origin);ImGui.Dummy(new Vector2(width,h));
    }
    private void EventBadge(string text,Vector4 colour)
    {
        var p=ImGui.GetCursorScreenPos();var size=ImGui.CalcTextSize(text)+UiVec(12,6);var draw=ImGui.GetWindowDrawList();
        draw.AddRectFilled(p,p+size,ImGui.GetColorU32(new Vector4(colour.X,colour.Y,colour.Z,.12f)),Ui(4));
        draw.AddRect(p,p+size,ImGui.GetColorU32(new Vector4(colour.X,colour.Y,colour.Z,.45f)),Ui(4));
        draw.AddText(p+UiVec(6,3),ImGui.GetColorU32(colour),text);ImGui.Dummy(size);
    }
    private void EventIconTile(Vector2 p,Vector2 size,string icon,Vector4 colour)
    {
        var draw=ImGui.GetWindowDrawList();draw.AddRectFilled(p,p+size,ImGui.GetColorU32(new Vector4(colour.X,colour.Y,colour.Z,.12f)),Ui(10));
        draw.AddRect(p,p+size,ImGui.GetColorU32(new Vector4(colour.X,colour.Y,colour.Z,.4f)),Ui(10));
        var glyph=EventIcon(icon);var fontSize=Ui(28);
        Vector2 symbolSize;
        if(UiFonts.Active)symbolSize=MeasureUiIcon(glyph,fontSize);
        else using(UiFonts.PushIcon())symbolSize=ImGui.CalcTextSize(glyph.ToIconString())*(fontSize/ImGui.GetFontSize());
        DrawUiIcon(glyph,fontSize,p+(size-symbolSize)*.5f,colour);
    }
    private string EventRecurrenceLabel(EventRecurrence r)=>r.Interval==1?r.Frequency switch{"daily"=>"Daily","weekly"=>"Weekly",_=>"Monthly"}:
        $"Every {r.Interval} {r.Frequency switch{"daily"=>"days","weekly"=>"weeks",_=>"months"}}";
    private void EventNotice(string? text,bool error=false)
    {
        if(string.IsNullOrEmpty(text))return;
        using(ImRaii.PushColor(ImGuiCol.Text,error?new Vector4(1,.5f,.45f,1):EventGreen))ImGui.TextWrapped(text);
    }
    private bool EventAttendanceButton(string id,string label,float width,float height,bool going)
    {
        if(!going)return AlphaChatButton(id,label,FontAwesomeIcon.Check,width,height,primary:true);
        var p=ImGui.GetCursorScreenPos();var size=new Vector2(width,height);var clicked=ImGui.InvisibleButton("##"+id,size);var draw=ImGui.GetWindowDrawList();
        draw.AddRectFilled(p,p+size,ImGui.GetColorU32(new Vector4(.07f,ImGui.IsItemHovered()?.29f:.22f,.16f,1)),Ui(8));
        draw.AddRect(p,p+size,ImGui.GetColorU32(new Vector4(EventGreen.X,EventGreen.Y,EventGreen.Z,.6f)),Ui(8));
        Vector2 iconSize;
        if(UiFonts.Active)iconSize=MeasureUiIcon(FontAwesomeIcon.Check,ImGui.GetFontSize());
        else using(UiFonts.PushIcon())iconSize=ImGui.CalcTextSize(FontAwesomeIcon.Check.ToIconString());
        var text=ImGui.CalcTextSize(label);var x=(width-iconSize.X-Ui(8)-text.X)*.5f;
        DrawUiIcon(FontAwesomeIcon.Check,ImGui.GetFontSize(),p+new Vector2(x,(height-iconSize.Y)*.5f),EventGreen);
        draw.AddText(p+new Vector2(x+iconSize.X+Ui(8),(height-text.Y)*.5f),ImGui.GetColorU32(EventGreen),label);
        if(ImGui.IsItemHovered())ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);return clicked;
    }
    private string? eventLastActionNotice;
    private DateTime eventActionNoticeUntil;
    private void DrawEventActionNotice()
    {
        if(eventActionNotice!=eventLastActionNotice){eventLastActionNotice=eventActionNotice;eventActionNoticeUntil=DateTime.UtcNow.AddSeconds(4);}
        if(DateTime.UtcNow>=eventActionNoticeUntil){eventActionNotice=null;eventLastActionNotice=null;return;}
        EventNotice(eventActionNotice);
    }
    private static readonly string[] EventCategories=["Games Night","Meetup","Club Venue","Market","March","Movie Night","Social Hangout","Meet & Greet","Performance","Other"];
    private void EventCategoryPicker(string label,ref string value,bool filter=false)
    {
        ImGui.TextUnformatted(label);ImGui.SetNextItemWidth(-1);
        if(!UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo("##eventCategory"+label,value.Length==0?"All categories":value)))return;
        if(filter&&ImGui.Selectable("All categories",value.Length==0))value="";
        foreach(var category in EventCategories)
            if(ImGui.Selectable(category,value==category))value=category;
        ImGui.EndCombo();
    }
}
