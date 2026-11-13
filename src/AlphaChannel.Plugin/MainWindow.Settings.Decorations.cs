using Dalamud.Bindings.ImGui;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private void DrawDecorationThemeSettings()
    {
        DrawOtherSettingsPanel("##appearanceDecorationTheme",245f,()=>
        {
            DrawSocialHeading("Decoration theme",1.1f);
            DrawSettingsHelper("Add subtle artwork to the header and sidebar. Uses your chosen accent colour and background.");
            ImGui.Dummy(UiVec(0,5));
            ImGui.SetNextItemWidth(MathF.Min(Ui(260f),ImGui.GetContentRegionAvail().X));
            if(UiFonts.Window(ImGui.GetFontSize(),ImGui.BeginCombo("##decorationTheme",ThemeCatalog.Label(Plugin.Cfg.UiDecorationTheme))))
            {
                try
                {
                    foreach(var theme in Enum.GetValues<UiDecorationTheme>())
                    {
                        var selected=Plugin.Cfg.UiDecorationTheme==theme;
                        if(ImGui.Selectable(ThemeCatalog.Label(theme),selected))
                        {
                            Plugin.Cfg.UiDecorationTheme=theme;
                            Plugin.Cfg.Save();
                        }
                        if(selected) ImGui.SetItemDefaultFocus();
                    }
                }
                finally { ImGui.EndCombo(); }
            }
            ImGui.Dummy(UiVec(0,7));
            DrawDecorationThemePreview();
        },autoHeight:true);
    }

    private void DrawDecorationThemePreview()
    {
        var origin=ImGui.GetCursorScreenPos();
        var size=new Vector2(MathF.Min(Ui(460f),ImGui.GetContentRegionAvail().X),Ui(102f));
        var draw=ImGui.GetWindowDrawList();
        var sideWidth=MathF.Min(Ui(108f),size.X*.3f);
        var headerHeight=Ui(36f);
        var theme=Plugin.Cfg.UiDecorationTheme;
        var previewScale=Ui(.65f);
        draw.PushClipRect(origin,origin+size,true);
        try
        {
            draw.AddRectFilled(origin,origin+size,ImGui.GetColorU32(Colors.WindowBg),Ui(7));
            draw.AddRectFilled(origin,origin+new Vector2(sideWidth,size.Y),ImGui.GetColorU32(Colors.SidebarBg),Ui(7));
            UiDecorationRenderer.Draw(draw,origin,new(sideWidth,Ui(32f)),theme,Colors,Ui(.4f),UiDecorationRenderer.Region.Brand);
            UiDecorationRenderer.Draw(draw,origin+new Vector2(0,Ui(69)),new(sideWidth,size.Y-Ui(69)),theme,Colors,previewScale,UiDecorationRenderer.Region.Footer);
            var headerOrigin=origin+new Vector2(sideWidth,0);
            UiDecorationRenderer.Draw(draw,headerOrigin,new(size.X-sideWidth,headerHeight),theme,Colors,previewScale,UiDecorationRenderer.Region.Header);
            var pill=origin+UiVec(6,47);var pillSize=new Vector2(sideWidth-Ui(12),Ui(18));
            draw.AddRectFilled(pill,pill+pillSize,ImGui.GetColorU32(AccentInk(.2f)),Ui(5));
            UiDecorationRenderer.Selection(draw,pill,pillSize,theme,Colors,previewScale,false);
            UiFonts.DrawText(draw,ImGui.GetFontSize()*.7f,origin+UiVec(9,33),ImGui.GetColorU32(Colors.MutedText),"ALPHA CHANNEL");
            UiFonts.DrawText(draw,ImGui.GetFontSize()*.85f,pill+UiVec(8,3),ImGui.GetColorU32(Vector4.One),"Home");
            UiFonts.DrawText(draw,ImGui.GetFontSize()*.9f,headerOrigin+UiVec(10,7),ImGui.GetColorU32(Colors.MutedText),ThemeCatalog.Label(theme));
            var cardY=headerHeight+Ui(10);
            var cardWidth=(size.X-sideWidth-Ui(36))/3;
            for(var i=0;i<3;i++)
            {
                var card=origin+new Vector2(sideWidth+Ui(9)+i*(cardWidth+Ui(9)),cardY);
                draw.AddRectFilled(card,card+new Vector2(cardWidth,Ui(49)),ImGui.GetColorU32(Colors.CardBg),Ui(4));
                draw.AddRect(card,card+new Vector2(cardWidth,Ui(49)),ImGui.GetColorU32(AccentInk(.25f)),Ui(4));
                draw.AddRectFilled(card+UiVec(5,34),card+new Vector2(cardWidth-Ui(5),Ui(43)),ImGui.GetColorU32(Colors.Accent),Ui(3));
            }
            draw.AddRect(origin,origin+size,ImGui.GetColorU32(SettingsReviewBorder),Ui(7));
        }
        finally { draw.PopClipRect(); }
        ImGui.Dummy(size);
    }
}
