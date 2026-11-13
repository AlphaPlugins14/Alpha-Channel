using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using System.Globalization;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private readonly Dictionary<string,DateTime> eventDatePickerMonths=[];
    // Keep the picker focused on our main regions. Retain IANA IDs so seasonal
    // clock changes follow each location's rules rather than a fixed UTC offset.
    private static readonly string[] EventTimezones=
    [
        "Etc/UTC",
        "Europe/London", "Europe/Lisbon", "Europe/Paris", "Europe/Berlin",
        "Europe/Helsinki", "Europe/Athens", "Europe/Istanbul",
        "America/New_York", "America/Chicago", "America/Denver", "America/Phoenix",
        "America/Los_Angeles", "America/Anchorage", "Pacific/Honolulu",
        "America/Toronto", "America/Winnipeg", "America/Edmonton", "America/Vancouver",
        "America/Halifax", "America/St_Johns",
        "Australia/Perth", "Australia/Darwin", "Australia/Brisbane",
        "Australia/Adelaide", "Australia/Sydney", "Australia/Melbourne"
    ];
    private string eventTimezoneSearch="";
    private string eventWorldSearch="";
    private void EventWorldPicker()
    {
        ImGui.TextUnformatted("World");ImGui.SetNextItemWidth(-1);
        if(UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo("##eventWorldPicker",eventDraft.World,ImGuiComboFlags.HeightLarge)))
        {
            ImGui.SetNextItemWidth(-1);ImGui.InputTextWithHint("##eventWorldSearch","Find a world…",ref eventWorldSearch,80);
            if(ImGui.Selectable("Online · Any world",eventDraft.World=="Online")){eventDraft.World="Online";eventDraft.DataCentre="";ImGui.CloseCurrentPopup();}
            using(var list=UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##eventWorldList",UiVec(240,170),false)))if(list)
                foreach(var world in FfxivEventWorlds.All.Where(w=>(w.Name+" "+w.Centre+" "+w.Region).Contains(eventWorldSearch,StringComparison.OrdinalIgnoreCase)))
                    if(ImGui.Selectable(world.Name+" · "+world.Centre,eventDraft.World==world.Name)){eventDraft.World=world.Name;eventDraft.DataCentre=world.Centre;ImGui.CloseCurrentPopup();}
            ImGui.EndCombo();
        }
        ImGui.TextUnformatted("Data centre");ImGui.SetNextItemWidth(-1);
        var centre=eventDraft.World=="Online"?"All data centres":eventDraft.DataCentre;
        ImGui.InputText("##eventDataCentre",ref centre,80,ImGuiInputTextFlags.ReadOnly);
    }
    private void EventTimezonePicker()
    {
        ImGui.TextUnformatted("Time zone");ImGui.SetNextItemWidth(-1);
        if(!UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo("##eventTimezone",eventDraft.Timezone,ImGuiComboFlags.HeightLarge)))return;
        ImGui.SetNextItemWidth(-1);ImGui.InputTextWithHint("##eventTimezoneSearch","Find a city or time zone…",ref eventTimezoneSearch,80);
        if(ImGui.Selectable("Your local time zone")){eventDraft.Timezone=TimeZoneInfo.Local.HasIanaId?TimeZoneInfo.Local.Id:TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id,out var local)?local:"Etc/UTC";ImGui.CloseCurrentPopup();}
        using(var list=UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##eventTimezoneList",UiVec(280,220),false)))if(list)
            foreach(var zone in EventTimezones.Where(z=>z.Contains(eventTimezoneSearch,StringComparison.OrdinalIgnoreCase)))
                if(ImGui.Selectable(zone,eventDraft.Timezone==zone)){eventDraft.Timezone=zone;ImGui.CloseCurrentPopup();}
        ImGui.EndCombo();
    }
    private void EventDatePicker(string label,ref string value,float width)
    {
        var valid=DateTime.TryParseExact(value,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var selected);
        if(!valid)selected=DateTime.Today;
        if(AlphaChatButton("eventDate"+label,valid?selected.ToString("d MMM yyyy"):"Choose date",FontAwesomeIcon.CalendarAlt,width,Ui(32)))
        {eventDatePickerMonths[label]=new(selected.Year,selected.Month,1);ImGui.OpenPopup("##eventDatePicker"+label);}
        using var colours=ImRaii.PushColor(ImGuiCol.PopupBg,CardBg).Push(ImGuiCol.Border,AccentInk(.55f));
        using var style=ImRaii.PushStyle(ImGuiStyleVar.PopupRounding,Ui(10)).Push(ImGuiStyleVar.PopupBorderSize,Ui(1)).Push(ImGuiStyleVar.WindowPadding,UiVec(12,12));
        using var popup=UiFonts.Window(ImGui.GetFontSize(), ImRaii.Popup("##eventDatePicker"+label));if(!popup)return;
        var month=eventDatePickerMonths.GetValueOrDefault(label,new DateTime(selected.Year,selected.Month,1));
        if(AlphaChatButton("prevDateMonth","",FontAwesomeIcon.ChevronLeft,Ui(28),Ui(28)))month=month.AddMonths(-1);
        ImGui.SameLine();ImGui.TextUnformatted(month.ToString("MMMM yyyy"));ImGui.SameLine();
        if(AlphaChatButton("nextDateMonth","",FontAwesomeIcon.ChevronRight,Ui(28),Ui(28)))month=month.AddMonths(1);
        eventDatePickerMonths[label]=month;
        if(ImGui.BeginTable("##datePickerDays",7,ImGuiTableFlags.SizingFixedSame))
        {
            foreach(var day in new[]{"M","T","W","T","F","S","S"})ImGui.TableSetupColumn(day,ImGuiTableColumnFlags.WidthFixed,Ui(30));
            ImGui.TableNextRow();for(var col=0;col<7;col++){ImGui.TableSetColumnIndex(col);DrawSocialHelper(new[]{"M","T","W","T","F","S","S"}[col]);}
            var start=month.AddDays(-((int)month.DayOfWeek+6)%7);
            for(var row=0;row<6;row++)
            {
                ImGui.TableNextRow();
                for(var col=0;col<7;col++)
                {
                    ImGui.TableSetColumnIndex(col);var date=start.AddDays(row*7+col);
                    using var id=ImRaii.PushId(date.ToString("yyyyMMdd"));
                    using(ImRaii.PushColor(ImGuiCol.Text,date==selected?Vector4.One:date.Month==month.Month?MutedText:new Vector4(MutedText.X,MutedText.Y,MutedText.Z,.45f)))
                        if(ImGui.Selectable(date.Day.ToString(),date.Date==selected.Date,ImGuiSelectableFlags.None,UiVec(28,24))){value=date.ToString("yyyy-MM-dd");ImGui.CloseCurrentPopup();}
                }
            }
            ImGui.EndTable();
        }
        if(ImGui.SmallButton("Today")){value=DateTime.Today.ToString("yyyy-MM-dd");ImGui.CloseCurrentPopup();}
        ImGui.SameLine();if(ImGui.SmallButton("Tomorrow")){value=DateTime.Today.AddDays(1).ToString("yyyy-MM-dd");ImGui.CloseCurrentPopup();}
        ImGui.SetNextItemWidth(Ui(230));ImGui.InputTextWithHint("##typedEventDate","YYYY-MM-DD",ref value,10);
    }
}
