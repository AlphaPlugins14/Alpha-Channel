using Dalamud.Bindings.ImGui;

namespace AlphaChannel.Plugin;

// Passive, clipped draw-list artwork only: no widgets, textures, animation,
// input handling or layout changes. Classic emits no drawing commands.
internal static class UiDecorationRenderer
{
    internal enum Region { Header, Brand, Navigation, Footer }

    internal static void Draw(ImDrawListPtr draw, Vector2 origin, Vector2 size,
        UiDecorationTheme theme, ThemeColors colors, float scale, Region region, bool compact = false)
    {
        if ((int)theme<1 || (int)theme>5 || size.X<1 || size.Y<1 || scale<=0) return;
        draw.PushClipRect(origin,origin+size,true);
        try
        {
            var art=new Artwork(draw,origin,size,colors,scale);
            switch (region)
            {
                case Region.Header: art.Header(theme); break;
                case Region.Brand: art.Brand(theme,compact); break;
                case Region.Navigation: if (!compact) art.Navigation(theme); break;
                case Region.Footer: if (!compact) art.Footer(theme); break;
            }
        }
        finally { draw.PopClipRect(); }
    }

    internal static void Selection(ImDrawListPtr draw,Vector2 origin,Vector2 size,
        UiDecorationTheme theme,ThemeColors colors,float scale,bool compact)
    {
        if ((int)theme<1 || (int)theme>5) return;
        var art=new Artwork(draw,origin,size,colors,scale);
        var center=new Vector2(-6,size.Y/scale*.5f);
        switch(theme)
        {
            case UiDecorationTheme.Gaming:
                for(var i=0;i<3;i++) art.Box(center+new Vector2(-2+i*2,-4+i*3),new(2,2),.8f);
                break;
            case UiDecorationTheme.Cinema:
                art.Corner(new(2,3),new(1,1),5,.6f);
                art.Corner(new(size.X/scale-3,size.Y/scale-3),new(-1,-1),5,.6f);
                break;
            case UiDecorationTheme.Music:
                for(var i=0;i<3;i++) art.Box(center+new Vector2(-2+i*2,-2-i),new(1,4+i*2),.85f);
                break;
            case UiDecorationTheme.Cute: art.Sparkle(center,4,.9f); break;
            case UiDecorationTheme.Eorzea: art.Crystal(center,5,.85f); break;
        }
    }

    private readonly struct Artwork(ImDrawListPtr draw,Vector2 origin,Vector2 size,ThemeColors colors,float scale)
    {
        private float W=>size.X/scale;
        private float H=>size.Y/scale;
        private Vector2 P(Vector2 p)=>origin+p*scale;
        private uint Ink(float alpha,bool light=false)
        {
            var ink=light?Vector4.Lerp(colors.AccentHover,Vector4.One,.24f):colors.Accent;
            ink.W=alpha; return ImGui.GetColorU32(ink);
        }
        private void Line(Vector2 a,Vector2 b,float alpha,float thickness=1)=>
            draw.AddLine(P(a),P(b),Ink(alpha),MathF.Max(.7f,thickness*scale));
        internal void Box(Vector2 p,Vector2 dimensions,float alpha)=>draw.AddRectFilled(P(p),P(p+dimensions),Ink(alpha));
        private void Circle(Vector2 p,float radius,float alpha)=>draw.AddCircle(P(p),radius*scale,Ink(alpha),40,MathF.Max(.7f,scale));
        internal void Corner(Vector2 p,Vector2 direction,float length,float alpha)
        {
            Line(p,p+new Vector2(direction.X*length,0),alpha);
            Line(p,p+new Vector2(0,direction.Y*length),alpha);
        }
        private void Ornament(Vector2 p,Vector2 direction,float alpha)
        {
            Corner(p,direction,17,alpha);
            Corner(p+direction*3,direction,8,alpha*.55f);
            Sparkle(p+new Vector2(direction.X*19,0),2,alpha*.8f);
            draw.AddBezierCubic(P(p+new Vector2(0,direction.Y*14)),P(p+direction*5),
                P(p+direction*9),P(p+new Vector2(direction.X*14,0)),Ink(alpha*.6f),MathF.Max(.7f,scale));
        }
        internal void Sparkle(Vector2 p,float radius,float alpha)
        {
            // Four tapered arms read as a star even at small UI sizes.
            var center=P(p);var ink=Ink(alpha,true);
            for(var i=0;i<4;i++)
            {
                var angle=i*MathF.PI*.5f;
                var direction=new Vector2(MathF.Sin(angle),MathF.Cos(angle));
                var side=new Vector2(direction.Y,-direction.X);
                draw.AddTriangleFilled(center+side*radius*.23f*scale,
                    center+direction*radius*scale,center-side*radius*.23f*scale,ink);
            }
        }
        internal void Crystal(Vector2 p,float height,float alpha)
        {
            var top=P(p-new Vector2(0,height));var right=P(p+new Vector2(height*.43f,-height*.12f));
            var bottom=P(p+new Vector2(0,height*.7f));var left=P(p-new Vector2(height*.43f,height*.12f));
            var middle=P(p);
            draw.AddTriangleFilled(top,left,middle,Ink(alpha*.7f));
            draw.AddTriangleFilled(top,middle,right,Ink(alpha,true));
            draw.AddTriangleFilled(left,bottom,middle,Ink(alpha*.35f));
            draw.AddTriangleFilled(middle,bottom,right,Ink(alpha*.8f));
            draw.AddLine(top,left,Ink(alpha,true),scale);
            draw.AddLine(left,bottom,Ink(alpha*.8f),scale);
            draw.AddLine(bottom,right,Ink(alpha*.8f),scale);
            draw.AddLine(right,top,Ink(alpha,true),scale);
            draw.AddLine(top,bottom,Ink(alpha*.5f,true),MathF.Max(.7f,scale*.7f));
        }
        private void Cloud(Vector2 p,float width,float alpha)
        {
            // One filled silhouette, with a transparent edge fringe. Overlapping
            // translucent circles would leave darker seams inside the cloud.
            const int steps=12;
            Span<Vector2> contour=stackalloc Vector2[steps*3+1];
            var start=new Vector2(-.5f,.22f);
            contour[0]=P(p+start*width);
            for(var segment=0;segment<3;segment++)
            {
                var a=segment==0?start:segment==1?new Vector2(-.22f,-.15f):new Vector2(.3f,-.12f);
                var b=segment==0?new Vector2(-.7f,-.15f):segment==1?new Vector2(-.2f,-.62f):new Vector2(.67f,-.2f);
                var c=segment==0?new Vector2(-.3f,-.3f):segment==1?new Vector2(.28f,-.62f):new Vector2(.64f,.22f);
                var d=segment==0?new Vector2(-.22f,-.15f):segment==1?new Vector2(.3f,-.12f):new Vector2(.42f,.22f);
                for(var step=1;step<=steps;step++)
                {
                    var t=step/(float)steps;var u=1-t;
                    contour[segment*steps+step]=P(p+width*(u*u*u*a+3*u*u*t*b+3*u*t*t*c+t*t*t*d));
                }
            }
            var center=P(p);var ink=Ink(alpha,true);var clear=Ink(0,true);
            var uv=ImGui.GetFontTexUvWhitePixel();
            draw.PrimReserve(contour.Length*9,contour.Length*9);
            for(var i=0;i<contour.Length;i++)
            {
                var previous=contour[(i+contour.Length-1)%contour.Length];
                var a=contour[i];var b=contour[(i+1)%contour.Length];
                var next=contour[(i+2)%contour.Length];
                var tangentA=Vector2.Normalize(b-previous);var tangentB=Vector2.Normalize(next-a);
                var outerA=a+new Vector2(tangentA.Y,-tangentA.X)*.75f;
                var outerB=b+new Vector2(tangentB.Y,-tangentB.X)*.75f;
                draw.PrimVtx(center,uv,ink);draw.PrimVtx(a,uv,ink);draw.PrimVtx(b,uv,ink);
                draw.PrimVtx(a,uv,ink);draw.PrimVtx(outerA,uv,clear);draw.PrimVtx(outerB,uv,clear);
                draw.PrimVtx(a,uv,ink);draw.PrimVtx(outerB,uv,clear);draw.PrimVtx(b,uv,ink);
            }
        }
        private void ScreenFrame(Vector2 p,Vector2 dimensions,float alpha)
        {
            // Wide-screen framing rather than a literal camera or spotlight.
            var clear=Ink(0);var wash=Ink(alpha*.18f);
            draw.AddRectFilledMultiColor(P(p),P(p+dimensions),clear,clear,wash,wash);
            var tick=MathF.Min(11,dimensions.Y*.3f);
            Corner(p,Vector2.One,tick,alpha);
            Corner(p+new Vector2(dimensions.X,0),new(-1,1),tick,alpha);
            Corner(p+new Vector2(0,dimensions.Y),new(1,-1),tick,alpha);
            Corner(p+dimensions,new(-1,-1),tick,alpha);
        }
        private void Equalizer(Vector2 p,float width,float height,float alpha)
        {
            for(var i=0;i<15;i++)
            {
                var amount=.22f+MathF.Abs(MathF.Sin(i*1.7f+.5f))*.78f;
                var barWidth=width/23;
                for(var j=0;j<8;j++) if(j<amount*8)
                {
                    var pos=p+new Vector2(i*width/15,-j*height/8);
                    draw.AddRectFilled(P(pos),P(pos+new Vector2(barWidth,MathF.Max(1,height/11))),
                        Ink(alpha*(.7f+j*.04f),j>=4));
                }
            }
        }
        internal void Header(UiDecorationTheme theme)
        {
            var clear=Ink(0);var wash=Ink(.035f);
            draw.AddRectFilledMultiColor(origin,origin+size,clear,clear,wash,wash);
            switch(theme)
            {
                case UiDecorationTheme.Gaming:
                    for(var i=0;i<4;i++)
                    {
                        var x=W*(.07f+i*.27f);
                        Line(new(x,H-3),new(x+W*.12f,H-3),.55f);
                        Line(new(x+W*.12f,H-3),new(x+W*.12f+6,H-9),.45f);
                        Box(new(x+W*.12f+5,H-10),new(3,3),.85f);
                    }
                    break;
                case UiDecorationTheme.Cinema:
                    ScreenFrame(new(8,7),new(W-16,H-15),.22f);
                    draw.AddRectFilledMultiColor(P(new(22,H-5)),P(new(W*.42f,H-4)),Ink(.25f),Ink(0),Ink(0),Ink(.25f));
                    draw.AddRectFilledMultiColor(P(new(W*.58f,H-5)),P(new(W-22,H-4)),Ink(0),Ink(.25f),Ink(.25f),Ink(0));
                    break;
                case UiDecorationTheme.Music:
                    // A few cropped vinyl grooves sit at the edge, leaving the
                    // header's centre and its title clear.
                    draw.PushClipRect(P(new(0,MathF.Max(0,H-17))),P(new(W,H)),true);
                    try
                    {
                        for(var i=0;i<4;i++) Circle(new(W-42,H+35),44+i*6,.19f-i*.025f);
                    }
                    finally { draw.PopClipRect(); }
                    break;
                case UiDecorationTheme.Cute:
                    Sparkle(new(W-8,H*.53f),5,.75f);Sparkle(new(W*.6f,6),3,.55f);
                    break;
                case UiDecorationTheme.Eorzea:
                    Ornament(new(4,4),Vector2.One,.6f);Ornament(new(W-4,4),new(-1,1),.6f);
                    Line(new(24,H-4),new(W*.43f,H-4),.22f);Line(new(W*.57f,H-4),new(W-24,H-4),.22f);
                    Crystal(new(W*.5f,H-5),4,.8f);
                    Sparkle(new(W*.5f-15,H-5),2,.5f);Sparkle(new(W*.5f+15,H-5),2,.5f);break;
            }
            HeaderMotif(theme);
        }

        private void HeaderMotif(UiDecorationTheme theme)
        {
            // Keep the extra detail in the bottom gutter, below the title and
            // away from the right-hand controls. All coordinates are UI units.
            if(W<150 || H<30) return;
            var p=new Vector2(20,H-9);
            switch(theme)
            {
                case UiDecorationTheme.Gaming:
                    Box(p+new Vector2(4,-7),new(3,11),.65f);
                    Box(p+new Vector2(0,-3),new(4,3),.65f);
                    Box(p+new Vector2(7,-3),new(4,3),.65f);
                    Line(p+new Vector2(18,1),p+new Vector2(46,1),.45f);
                    Line(p+new Vector2(46,1),p+new Vector2(50,-3),.45f);
                    Line(p+new Vector2(50,-3),p+new Vector2(78,-3),.45f);
                    for(var i=0;i<3;i++) Box(p+new Vector2(85+i*8,-4),new(2,2),.6f-i*.15f);
                    break;
                case UiDecorationTheme.Cinema:
                    Line(p+new Vector2(0,-6),p+new Vector2(58,-6),.5f);
                    Line(p+new Vector2(0,4),p+new Vector2(58,4),.5f);
                    for(var i=0;i<5;i++)
                    {
                        Box(p+new Vector2(3+i*11,-4),new(4,2),.4f);
                        Box(p+new Vector2(3+i*11,0),new(4,2),.4f);
                    }
                    draw.AddRectFilledMultiColor(P(p+new Vector2(58,3)),P(p+new Vector2(110,4)),
                        Ink(.5f),Ink(0),Ink(0),Ink(.5f));
                    break;
                case UiDecorationTheme.Music:
                    Line(p+new Vector2(0,4),p+new Vector2(110,4),.28f);
                    ReadOnlySpan<float> heights=[3,6,9,5,11,7,4,8,3];
                    for(var i=0;i<heights.Length;i++)
                        Box(p+new Vector2(4+i*6,3-heights[i]),new(3,heights[i]),.6f);
                    break;
                case UiDecorationTheme.Cute:
                    // A small outline cloud, rather than another filled badge.
                    var a=p+new Vector2(1,3);var b=p+new Vector2(23,3);
                    Line(a,b,.6f);
                    draw.AddBezierCubic(P(a),P(p+new Vector2(-6,-4)),P(p+new Vector2(6,-8)),
                        P(p+new Vector2(8,-3)),Ink(.6f),MathF.Max(.7f,scale));
                    draw.AddBezierCubic(P(p+new Vector2(8,-3)),P(p+new Vector2(7,-13)),
                        P(p+new Vector2(22,-13)),P(p+new Vector2(23,-4)),Ink(.6f),MathF.Max(.7f,scale));
                    draw.AddBezierCubic(P(p+new Vector2(23,-4)),P(p+new Vector2(31,-6)),
                        P(p+new Vector2(31,3)),P(b),Ink(.6f),MathF.Max(.7f,scale));
                    Sparkle(p+new Vector2(43,-2),4,.65f);
                    Sparkle(p+new Vector2(58,1),2.5f,.45f);
                    Line(p+new Vector2(70,3),p+new Vector2(105,3),.25f);
                    break;
                case UiDecorationTheme.Eorzea:
                    Crystal(p+new Vector2(53,-1),6,.7f);
                    Line(p+new Vector2(0,1),p+new Vector2(38,1),.4f);
                    Line(p+new Vector2(38,1),p+new Vector2(43,-3),.5f);
                    Line(p+new Vector2(63,-3),p+new Vector2(68,1),.5f);
                    Line(p+new Vector2(68,1),p+new Vector2(106,1),.4f);
                    Corner(p+new Vector2(7,1),new(1,-1),4,.45f);
                    Corner(p+new Vector2(99,1),new(-1,-1),4,.45f);
                    break;
            }
        }

        internal void Brand(UiDecorationTheme theme,bool compact)
        {
            if(compact)
            {
                if(theme==UiDecorationTheme.Eorzea) Crystal(new(W-5,24),5,.3f);
                else if(theme==UiDecorationTheme.Cute) Sparkle(new(5,26),3,.4f);
                else Corner(new(3,7),Vector2.One,6,.3f);
                return;
            }
            switch(theme)
            {
                case UiDecorationTheme.Gaming:
                    Box(new(18,29),new(4,16),.65f);Box(new(12,35),new(16,4),.65f);
                    Circle(new(W-26,55),4,.65f);
                    draw.AddTriangleFilled(P(new(W-47,58)),P(new(W-42,49)),P(new(W-37,58)),Ink(.6f));
                    for(var i=0;i<5;i++) Box(new(12+i*30,10+i%2*5),new(2,2),.45f);
                    break;
                case UiDecorationTheme.Cinema:
                    ScreenFrame(new(W*.5f-34,14),new(68,48),.26f);break;
                case UiDecorationTheme.Music:
                    draw.AddCircleFilled(P(new(41,32)),24*scale,Ink(.07f),40);
                    for(var i=0;i<5;i++) Circle(new(41,32),12+i*3,.22f+i*.035f);
                    draw.AddCircleFilled(P(new(41,32)),6*scale,Ink(.35f),20);
                    Circle(new(41,32),2,.85f);break;
                case UiDecorationTheme.Cute:
                    Cloud(new(39,32),34,.14f);Sparkle(new(W-23,56),5,.75f);Sparkle(new(22,60),3,.6f);break;
                case UiDecorationTheme.Eorzea:
                    Circle(new(W*.5f,38),34,.2f);Circle(new(W*.5f,38),40,.13f);
                    Crystal(new(20,36),20,.7f);Crystal(new(34,48),10,.45f);
                    Crystal(new(W-24,56),11,.5f);
                    Ornament(new(4,5),Vector2.One,.5f);Ornament(new(W-4,5),new(-1,1),.5f);
                    Sparkle(new(W*.7f,12),3,.65f);break;
            }
        }

        internal void Navigation(UiDecorationTheme theme)
        {
            for(var i=0;i<5;i++)
            {
                var y=H*(i+.3f)/5;
                switch(theme)
                {
                    case UiDecorationTheme.Gaming:
                        var x=W-5-i%2*4;
                        Line(new(x,y),new(x,y+34),.25f);Line(new(x,y+34),new(x-4,y+39),.3f);
                        Box(new(x-1,y-3),new(2,2),.55f);break;
                    case UiDecorationTheme.Cute:
                        Sparkle(new(W-9-i%2*4,y),i%2==0?2:3,.32f);break;
                    case UiDecorationTheme.Eorzea:
                        var node=new Vector2(W-4,y);
                        Sparkle(node,2.2f,.4f);
                        if(i<4) Line(node,node+new Vector2(i%2==0?-3:0,H/5),.12f);
                        break;
                }
            }
        }

        internal void Footer(UiDecorationTheme theme)
        {
            switch(theme)
            {
                case UiDecorationTheme.Gaming:
                    var controller=new Vector2(13,MathF.Max(3,H-40));
                    draw.AddRectFilled(P(controller),P(controller+new Vector2(48,25)),Ink(.14f),8*scale);
                    draw.AddRect(P(controller),P(controller+new Vector2(48,25)),Ink(.55f),8*scale);
                    Box(controller+new Vector2(10,7),new(3,12),.65f);
                    Box(controller+new Vector2(6,11),new(11,3),.65f);
                    draw.AddCircleFilled(P(controller+new Vector2(33,15)),2.5f*scale,Ink(.65f,true),12);
                    draw.AddCircleFilled(P(controller+new Vector2(40,9)),2.5f*scale,Ink(.8f),12);
                    for(var i=0;i<7;i++) Box(new(76+i*(W-84)/7,H-8-(i*17%Math.Max(1,(int)(H-6)))),new(2,2),.4f);
                    break;
                case UiDecorationTheme.Cinema:
                    var frameHeight=MathF.Min(36,H-12);
                    ScreenFrame(new(12,(H-frameHeight)*.5f),new(W-24,frameHeight),.18f);
                    break;
                case UiDecorationTheme.Music: Equalizer(new(8,H-4),W-16,MathF.Min(44,H-5),.65f);break;
                case UiDecorationTheme.Cute:
                    Cloud(new(38,H*.65f),46,.12f);Sparkle(new(W-14,H*.3f),4,.75f);
                    var paw=new Vector2(W-33,H*.66f);
                    draw.AddCircleFilled(P(paw),5*scale,Ink(.55f,true),16);
                    for(var i=0;i<3;i++) draw.AddCircleFilled(P(paw+new Vector2((i-1)*6,-9+MathF.Abs(i-1)*2)),2.5f*scale,Ink(.6f,true),12);
                    break;
                case UiDecorationTheme.Eorzea:
                    Circle(new(43,H-8),MathF.Min(43,H*.8f),.24f);
                    Crystal(new(41,H-25),MathF.Min(28,H*.38f),.78f);
                    Crystal(new(22,H-14),MathF.Min(15,H*.22f),.5f);
                    Crystal(new(61,H-16),MathF.Min(18,H*.25f),.6f);
                    Sparkle(new(80,H-36),3,.65f);Sparkle(new(16,H-45),2,.5f);break;
            }
        }
    }
}
