using System.Numerics;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;

namespace AlphaChannel.Plugin.Video;

internal sealed class PixelsScreenRenderer
{
    internal const int RankingsPageSize=6;
    internal static int RankingsActionAt(Vector2 uv)
    {
        if(!float.IsFinite(uv.X)||!float.IsFinite(uv.Y))return 0;
        var x=uv.X*1280;var y=uv.Y*720;
        if(x>=972&&x<1036&&y>=100&&y<136||x>=1172&&x<1260&&y>=12&&y<50)return 1;
        if(y>=578&&y<614){if(x>=244&&x<364)return 2;if(x>=916&&x<1036)return 3;}
        return 0;
    }
    private readonly FontFamily font;
    internal PixelsScreenRenderer(string fontPath) { font = new FontCollection().Add(fontPath); }
    internal byte[] Render(PixelsBoard board, int selected, int colour, string? hover,bool rankingsOpen=false,int rankingsPage=0,string? localAccount=null)
    {
        var frame = new byte[1280 * 720 * 4];
        void Rect(int x, int y, int w, int h, uint rgb) => ArcadeRaster.FillRectangle(frame, 1280, 720, x, y, w, h, rgb);
        Rect(0,0,1280,720,0x080D1D); Rect(126,62,1028,580,0x365B89);
        for (var i = 0; i < board.Colours.Length; i++) Rect(128 + i % 128 * 8, 64 + i / 128 * 8, 8, 8,
            board.Colours[i] >= 0 ? PixelsBoard.Palette[board.Colours[i]] : (i % 128 + i / 128) % 2 == 0 ? 0x141E31u : 0x172238u);
        if (selected >= 0)
        {
            var x = 128 + selected % 128 * 8; var y = 64 + selected / 128 * 8;
            Rect(x,y,8,1,0xFFFFFF); Rect(x,y+7,8,1,0xFFFFFF); Rect(x,y,1,8,0xFFFFFF); Rect(x+7,y,1,8,0xFFFFFF);
        }
        Rect(825,12,327,38,board.Blocked ? 0x662C3A : board.RemainingSeconds == 0 && board.Ready ? 0x164F42u : 0x142744u);
        Rect(1172,12,88,38,0x1B3355);
        Rect(1196,30,8,12,0x61A5FA); Rect(1212,24,8,18,0x61A5FA); Rect(1228,18,8,24,0x61A5FA);
        for (var i = 0; i < PixelsBoard.Palette.Length; i++)
        {
            var x = 200 + i * 32;
            if (i == colour) Rect(x-2,661,32,34,0xFFFFFF);
            Rect(x,663,28,30,PixelsBoard.Palette[i]);
        }
        var canPlace = board.Ready && !board.Blocked && board.RemainingSeconds == 0 && selected >= 0;
        Rect(1024,656,152,44,canPlace ? 0x367FF6u : 0x1E304F);
        using var image = Image.LoadPixelData<Bgra32>(frame,1280,720);
        image.Mutate(ctx =>
        {
            void Text(string text, float size, float x, float y, uint rgb = 0xEAF2FF) => ctx.DrawText(text,font.CreateFont(size),Color.FromRgb((byte)(rgb>>16),(byte)(rgb>>8),(byte)rgb),new PointF(x,y));
            Text("PIXELS",28,26,14); Text("One shared canvas · one pixel every 3 minutes",16,170,24,0x97ACCA);
            Text(board.Timer,19,841,21);
            Text("Colour",17,128,670,0x97ACCA); Text("Place pixel",19,1048,667);
            if (!string.IsNullOrEmpty(hover))
            {
                ctx.Fill(Color.FromRgb(12,20,36),new Rectangle(148,600,650,32)); Text(hover.Length > 70 ? hover[..67]+"…" : hover,16,160,607);
            }
            if(rankingsOpen)
            {
                var rows=board.Rankings();var pages=Math.Max(1,(rows.Length+RankingsPageSize-1)/RankingsPageSize);
                var page=Math.Clamp(rankingsPage,0,pages-1);
                ctx.Fill(Color.FromRgba(0,0,0,165),new Rectangle(0,54,1280,666));
                ctx.Fill(Color.FromRgb(17,26,47),new Rectangle(220,80,840,560));
                ctx.Draw(Color.FromRgb(59,130,246),2,new Rectangle(220,80,840,560));
                Text("Pixels rankings",27,244,100);
                Text("Current ownership · updates live with the board",17,244,140,0x97ACCA);
                void Button(string label,int x,int y,int w,bool enabled=true)
                {
                    ctx.Fill(Color.FromRgb(27,51,85),new Rectangle(x,y,w,36));
                    ctx.Draw(Color.FromRgb(54,91,137),1,new Rectangle(x,y,w,36));
                    Text(label,17,x+14,y+8,enabled?0xEAF2FFu:0x647591u);
                }
                Button("Close",972,100,64);
                if(rows.Length==0)Text("The canvas is waiting for its first pixel.",21,260,220,0x97ACCA);
                for(var index=page*RankingsPageSize;index<Math.Min(rows.Length,(page+1)*RankingsPageSize);index++)
                {
                    var row=rows[index];var y=178+(index%RankingsPageSize)*60;
                    ctx.Fill(Color.FromRgb(14,23,40),new Rectangle(240,y-4,800,56));
                    Text((index+1)+".",21,252,y,0x61A5FA);
                    var name=row.Name+(row.Id==localAccount?" (you)":"");var labelFont=font.CreateFont(21);
                    while(name.Length>1&&TextMeasurer.MeasureSize(name,new TextOptions(labelFont)).Width>550)name=name[..^2]+"…";
                    Text(name,21,300,y,row.Id==localAccount?0x33DA8Bu:0xEAF2FFu);
                    Text($"{row.Count:N0} squares",17,880,y+2,0x97ACCA);
                    Text($"{100d*row.Count/(128*72):0.0}% of canvas",15,300,y+26,0x97ACCA);
                    ctx.Fill(Color.FromRgb(27,51,85),new Rectangle(300,y+47,560,4));
                    if(row.Count>0)ctx.Fill(Color.FromRgb(59,130,246),new Rectangle(300,y+47,Math.Max(2,560*row.Count/(128*72)),4));
                }
                Button("Previous",244,578,120,page>0);Button("Next",916,578,120,page<pages-1);
                Text($"Page {page+1} of {pages} · {rows.Length:N0} contributors",17,460,587,0x97ACCA);
            }
        });
        image.CopyPixelDataTo(frame); return frame;
    }
}
