namespace AlphaChannel.Plugin.Video;

// A separate transparent surround: the picture's UVs and full area stay intact.
internal static class TvFrameShader
{
    internal const string Source = """
VOut FrameVS(uint id : SV_VertexID)
{
    bool premium=audioTextMeta.w>=7;
    float x = (-1.0 + 2.0 * (float)(id / 2) / (float)CURVE_SEGMENTS) * (premium?1.470588235:1.14);
    float y = (id % 2 == 0 ? -1.0 : 1.0) * (premium?1.764705882:1.22);
    VOut o;
    o.pos = mul(float4(x,y,curvature*x*x,1),worldViewProj);
    o.uv = float2(x,y);
    return o;
}
float4 FramePS(VOut i, bool front : SV_IsFrontFace) : SV_TARGET
{
    ClipSceneDepth(i.pos);
    [loop] for(int r=0;r<uiRectCount;r++)
    {
        float4 rect=uiRects[r];
        if(all(i.pos.xy>=rect.xy)&&all(i.pos.xy<rect.xy+rect.zw)) discard;
    }
    float2 p=i.uv, a=abs(p);
    // Never draw in the video, including when its content is transparent.
    if(a.x<=1 && a.y<=1) discard;
    int style=(int)audioTextMeta.w;
    if(style==0) discard;
    if(style>=7)
    {
        // t1 is shared with title text in separate passes; this artwork is also used by the UI preview.
        float4 art=titleTex.Sample(smp,float2((p.x/1.470588235+1)*.5,(1-p.y/1.764705882)*.5));
        if(art.a<.003)discard;
        if(!front)art.rgb*=.45;
        return art;
    }
    float2 extent = style==1 ? float2(.022,.035) : style==2 ? float2(.035,.055) :
        style==3 ? float2(.070,.115) : style==4 ? float2(.095,.165) :
        style==5 ? float2(.105,.175) : float2(.095,.155);
    float2 q=(a-1)/extent;
    float edge=max(q.x,q.y);
    float3 accent=reactionColor[0].rgb;
    float glow=audioTextMeta.z;
    if(edge>1)
    {
        if(style==2 && glow>0 && edge<1.75)
            return float4(accent,(1-smoothstep(1,1.75,edge))*(glow==2?.28:.10));
        discard;
    }
    float across=max((a.x-1)/extent.x,(a.y-1)/extent.y);
    float bevel=sin(saturate(across)*3.14159265);
    float inner=1-smoothstep(.06,.18,across);
    float outer=smoothstep(.82,1,across);
    float light=saturate(.55+.3*p.y-.16*p.x);
    float3 c=float3(.055,.06,.075)+bevel*.075*light;
    if(style==1) c+=float3(.23,.25,.28)*(inner*.65+outer*.3);
    if(style==2)
    {
        float tube=exp(-pow((across-.55)*8,2));
        c+=accent*(glow==0?.20:.23+tube*(glow==2?1.1:.75));
    }
    if(style==3)
    {
        float corner=step(.90,a.x)*step(.82,a.y);
        c+=corner*float3(.20,.18,.14)*(.5+bevel);
        float2 screw=(a-float2(1.035,1.055))/float2(1,.6);
        c=lerp(c,float3(.5,.48,.43),1-smoothstep(.005,.012,length(screw)));
    }
    if(style==4)
    {
        // Soft moulded casing, rounded outer corners and a small power light.
        float2 rounded=max((a-float2(1.045,1.075))/float2(1,.6),0);
        if(length(rounded)>.051) discard;
        c=float3(.24,.225,.205)+bevel*.12*light-inner*.10;
        float led=1-smoothstep(.006,.014,length((p-float2(.88,-1.055))/float2(1,1.6667)));
        c=lerp(c,float3(.18,1,.12),led);
    }
    if(style==5)
    {
        float grain=sin(p.x*110+p.y*80+sin(p.y*35)*2)*.018;
        c=float3(.18,.075,.027)+grain+bevel*float3(.10,.055,.018);
        float gold=max(inner,outer*.7);
        float corner=step(.86,a.x)*step(.76,a.y);
        float scroll=.5+.5*sin((a.x-a.y)*95+sin((a.x+a.y)*60)*2);
        gold=max(gold,corner*smoothstep(.4,.65,scroll));
        c=lerp(c,float3(.62,.39,.10)*(.65+bevel*.6),gold);
    }
    if(style==6)
    {
        if(a.x>1.04 && a.y>1.07 && (a.x-1.04)+(a.y-1.07)*.6>.07) discard;
        float rail=step(1.025,a.x)*step(a.x,1.055)*step(.3,a.y)*step(a.y,.85);
        c+=rail*(p.y>0?accent:accent.bgr);
        c=lerp(c,float3(.55,.35,.09),step(1.09,a.y)*step(a.y,1.12)*step(a.x,.96));
    }
    if(!front)c*=.45;
    return float4(saturate(c),1);
}
""";
}
