using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using Buffer = SharpDX.Direct3D11.Buffer;
using GfxKernel = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using GfxScene = FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using GfxGui = FFXIVClientStructs.FFXIV.Component.GUI;
using GameControl = FFXIVClientStructs.FFXIV.Client.Game.Control;
using NumericsMatrix4x4 = System.Numerics.Matrix4x4;

namespace AlphaChannel.Plugin.Video;

//X/Y are uv coordinates on the screen quad (0..1, matching the VS's own uv mapping - see the
//shader below for which corner is which), not world-space or pixel positions. Radius is also in
//that same 0..1 uv space, not pixels.
internal readonly record struct ReactionParticle(
    float X,
    float Y,
    float Alpha,
    float Radius,
    float R,
    float G,
    float B,
    ReactionGlyph Glyph);

internal sealed unsafe partial class ScreenPainter : IDisposable
{
	//Base quad size before Scale is applied - rough starting guess, tune live in-game.

    //Absolute world transform for the screen quad. Unlike the old companion-relative offset, this is a
    //fixed world position/yaw set once per "spawn" (see VideoEngine.SpawnScreenInFrontOfLocalPlayer) and
    //updated live from the Settings UI - it does not track any game object's movement afterwards.
    private readonly ScreenRenderTransform worldTransform = new();


    private readonly VertexShader _vs;
    private readonly PixelShader _ps;
    private readonly PixelShader _loadingPs;
    private readonly PixelShader _screensaverPs;
    private readonly PixelShader _audioPs;
    private readonly PixelShader _glowPs;
    private readonly PixelShader _tvAudioEffectsPs;
    private readonly VertexShader _frameVs;
    private readonly PixelShader _framePs;
    private TvFrameSelection frameSelection = new();
    internal TvFrameSelection FrameSelection { get => Volatile.Read(ref frameSelection); set => Volatile.Write(ref frameSelection, value.Normalized()); }
    private volatile bool _audioEffectsPlaybackActive;
    private readonly PixelShader _titlePs;
    private readonly PixelShader _audioTextPs;
    private readonly PixelShader _reactionsPs;

    private readonly RenderResourceLifetime frameLifetime = new();
    private readonly ScreenShaderState shaderState = new();
    private (string Title, string Source)? requestedTitle;
    private AudioScreenTextSelection? requestedAudioText;
    private readonly SpotifyCoverLoader spotifyCoverLoader = new();
    private volatile bool musicSceneActive;
    private MusicSceneEffects musicEffects = MusicSceneEffects.Default;
    internal bool MusicSceneActive => musicSceneActive;
    internal MusicSceneEffects MusicEffects => System.Threading.Volatile.Read(ref musicEffects);
    internal void SetMusicSceneEffects(MusicSceneEffects effects, bool active)
    {
        System.Threading.Volatile.Write(ref musicEffects, effects.Normalized());
        musicSceneActive = active;
    }
    private string? requestedScreensaverStatus;
    private volatile bool _isLoading;
    private volatile bool _isScreensaver;
    private DateTime _screensaverStartedAtUtc = DateTime.MinValue;
    private volatile bool _isAudioOnly;
    private volatile AudioVisualizerTheme _classicBarsTheme;
    private volatile float _audioLevel;
    private float _audioAnimationTime;
    private float _audioAnimationSpeed = 0.65f;
    private DateTime _lastAudioAnimationUpdateUtc =
        DateTime.UtcNow;
    private DateTime _loadingStartedAtUtc = DateTime.MinValue;
    private readonly SamplerState _sampler;
    private readonly RasterizerState _rasterState;
    private readonly DepthStencilState _depthState;
    private readonly DepthStencilState _noDepthState;
    private readonly BlendState _alphaBlend;
    private readonly Buffer _cbuf;
    private readonly Buffer _sceneDepthParams;
    private ShaderResourceView? _sceneDepthSrv;
    private nint _sceneDepthSrvPtr;
    private bool _loggedDepthPath;
	private readonly TitleTextureRenderer _titleRenderer = new();
    private readonly AudioScreenTextRenderer _audioTextRenderer = new();
    private readonly AudioScreenTextRenderer _musicPreviewTextRenderer = new();
    private AudioScreenTextSelection? requestedMusicPreviewText;
    internal nint MusicPreviewTextTexture => _musicPreviewTextRenderer.Srv?.NativePointer ?? nint.Zero;
    internal void SetMusicPreviewText(AudioScreenTextSelection text)
    {
        lock (frameLifetime.SyncRoot)
        {
            if (frameLifetime.IsDisposed || requestedMusicPreviewText == text) return;
            requestedMusicPreviewText = text;
            DxHandler.RunOnRenderThread("AlphaChannel.MusicPreviewText", () =>
                frameLifetime.Use(() => _musicPreviewTextRenderer.SetText(text)));
        }
    }
    private readonly DateTime _audioTextClockStartedUtc = DateTime.UtcNow;
    private readonly ScreensaverTextureRenderer _screensaverRenderer = new();
    private readonly ScreensaverTextureRenderer _countdownRenderer = new();
    private AlphaChannel.Plugin.Core.PartyCountdownView? partyCountdown;
    private string? countdownTextureKey;
    internal AlphaChannel.Plugin.Core.PartyCountdownView? PartyCountdown => Volatile.Read(ref partyCountdown) is { Visible: true } view ? view : null;
    internal void SetPartyCountdown(AlphaChannel.Plugin.Core.PartyCountdownView? view)
    {
        lock (frameLifetime.SyncRoot)
        {
            if (frameLifetime.IsDisposed) return;
            Volatile.Write(ref partyCountdown, view);
            var key = view is null ? null : $"{view.State.Id}:{view.Seconds}:{view.Halloween}";
            if (key == countdownTextureKey) return;
            countdownTextureKey = key;
            if (view is not null) DxHandler.RunOnRenderThread("AlphaChannel.CountdownText", () =>
                frameLifetime.Use(() => { if (PartyCountdown is { } current && current.State.Id == view.State.Id && current.Seconds == view.Seconds) _countdownRenderer.SetCountdown(current); }));
        }
    }
    private readonly ReactionTextureRenderer _reactionRenderer;

	// A soft ambient-light quad drawn larger than and behind the screen to create a glow.
	// Its scale is fixed rather than user-adjustable.
	private const float GlowScale = 1.28f;

	//The "now playing" banner shows for a while after the title actually changes, then fades rather
	//than sitting on screen for the whole video - DateTime-based rather than an accumulated
	//per-frame float so there is no drift and nothing to reset except on a genuine title change.
	private const float TitleHoldSeconds = 7f;
	private const float TitleFadeSeconds = 1f;
	private DateTime _titleShownAtUtc = DateTime.MinValue;

    private Texture2D? _texture;
    private ShaderResourceView? _srv;

    //
    // A second image texture is bound only while a slideshow transition is
    // active. The pixel shader blends from _srv to _transitionSrv.
    //
    private Texture2D? _transitionTexture;
    private ShaderResourceView? _transitionSrv;
    private volatile float _imageTransitionBlend;

    //Wrapping the swapchain's own persistent back buffer/depth buffer views via SharpDX AddRefs/Releases them.
    //Doing that fresh every single frame (60+ times/sec) tears down the engine's own refcount on objects it
    //still needs - only re-wrap when the underlying pointer actually changes (e.g. on resize), and otherwise
    //reuse the cached wrapper for the draw.
    private nint _cachedRtvPtr;
	private nint _cachedDsvPtr;
	private RenderTargetView? _cachedRtv;
	private DepthStencilView? _cachedDsv;

	//Native game UI (inventory, chat, hotbars, ...) is already fully composited into the back buffer by the
	//time our Present hook runs - only Dalamud's own ImGui overlay draws later than us. The scene depth
	//buffer has no concept of 2D UI at all, so depth testing alone can't stop us from painting over it; the
	//pixel shader discards fragments that fall inside any currently-visible native UI window's screen rect
	//instead, using bounds read straight from FFXIVClientStructs (AtkUnitBase/AtkResNode) - no hook needed.
	private const int MaxUiRects = 64;
	internal volatile bool KeepGameUiVisibleOverTv = true;

	//Reactions are selected from a small glyph atlas rendered once at startup. Per-frame updates
	//remain a cbuffer write; the atlas texture itself is reused for every reaction.
	private const int MaxReactions = 16;

	// A slight cylindrical bow (curved-monitor look)
	//across the screen's width. There's no per-vertex buffer at all here (the flat 4-corner quad was
	//generated purely from SV_VertexID) - subdividing into a horizontal strip of quads and displacing
	//each column along local Z by a parabola (max at the edges, zero at center) is still just procedural
	//math in the vertex shader, no CPU-side geometry needed. Curvature is a fixed constant, not
	//user-adjustable - "slightly curved" was the ask, not a control to expose.
	private const int CurveSegments = 24;
	private const float Curvature = 0.12f;
	private const int VertexCount = (CurveSegments + 1) * 2;

	[StructLayout(LayoutKind.Sequential)]
	private unsafe struct ScreenParams
	{
		public NumericsMatrix4x4 WorldViewProj;
		public int UiRectCount;
		public float Curvature;
		public float GlowScale;
		public float TitleAlpha;
		public fixed float UiRects[MaxUiRects * 4]; //Each rect: screenX, screenY, width, height (pixels).

        //Kept as its own 16-byte-aligned group (float, not int, so there's no int/float3 packing
        //ambiguity to worry about across the C#/HLSL boundary) before the two reaction arrays.
        public float ReactionCount;
        public float LoadingTime;
        public float ReactionPad1;
        public float ImageTransitionBlend;
        public fixed float ReactionPos[MaxReactions * 4]; //xy = uv center (0..1), z = alpha, w = radius.
		public fixed float ReactionColor[MaxReactions * 4]; //rgb = color, a = glyph atlas index.
		public float AudioTextTime;
		public float AudioTopGradient;
		public float AudioBottomGradient;
		public float AudioTextPadding;
	}

	internal ScreenPainter()
	{
        try
        {
        _reactionRenderer = new ReactionTextureRenderer();
		var hlsl = @"
			#define MAX_UI_RECTS 64
			#define CURVE_SEGMENTS 24
			#define MAX_REACTIONS 16
				#define SCREEN_ASPECT (1.0 / 0.6)
			cbuffer Params : register(b0)
			{
				row_major float4x4 worldViewProj;
				int uiRectCount;
				float curvature;
				float glowScale;
				float titleAlpha;
				float4 uiRects[MAX_UI_RECTS]; //xy = screen pos, zw = size, in pixels
				float4 reactionMeta;
// x = reaction count
// y = loading animation time in seconds
				float4 reactionPos[MAX_REACTIONS]; //xy = uv center, z = alpha, w = radius
				float4 reactionColor[MAX_REACTIONS]; //rgb = color
				float4 audioTextMeta; //x = time, y = top gradient, z = bottom gradient
			};
			struct VOut { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

			// Procedural horizontal strip: column = id/2 (0..CURVE_SEGMENTS), row = id%2 (0 = bottom,
			// 1 = top). Same flat quad as before when curvature is 0 - this just adds columns between
			// the two original edges instead of replacing them.
			VOut VS(uint id : SV_VertexID)
			{
				uint col = id / 2;
				uint row = id % 2;
				float x = -1.0 + 2.0 * (float)col / (float)CURVE_SEGMENTS;
				float y = row == 0 ? -1.0 : 1.0;
				float z = curvature * x * x; //Parabola: 0 at center, max at the edges.

				VOut o;
				o.pos = mul(float4(x, y, z, 1), worldViewProj);
				o.uv = float2((x + 1.0) * 0.5, row == 0 ? 1.0 : 0.0);
				return o;
			}

Texture2D tex : register(t0);
Texture2D transitionTex : register(t2);
Texture2D screensaverTex : register(t3);
Texture2D reactionTex : register(t4);
// The scene may render below the output resolution. In that case a DSV
// cannot be attached to the output RTV: compare against its existing SRV.
Texture2D<float> sceneDepthTex : register(t5);
cbuffer SceneDepthParams : register(b1)
{
    float2 inverseOutputSize;
    float2 activeDepthSize;
};
void ClipSceneDepth(float4 position)
{
    if (inverseOutputSize.x > 0.0)
    {
        int2 pixel = min(int2(position.xy * inverseOutputSize * activeDepthSize), int2(activeDepthSize) - 1);
        float sceneDepth = sceneDepthTex.Load(int3(max(pixel, int2(0, 0)), 0));
        // Reversed Z: the larger value is closer to the camera.
        if (position.z < sceneDepth) discard;
    }
}
SamplerState smp : register(s0);

float4 ScreensaverPS(VOut i, bool isFrontFace : SV_IsFrontFace) : SV_TARGET
{
    ClipSceneDepth(i.pos);
    for (int r = 0; r < uiRectCount; r++)
    {
        float4 rect = uiRects[r];
        if (i.pos.x >= rect.x && i.pos.x < rect.x + rect.z &&
            i.pos.y >= rect.y && i.pos.y < rect.y + rect.w) discard;
    }
    if (!isFrontFace) return float4(0.015, 0.015, 0.02, 1.0);

    float2 uv = i.uv;
    float3 color = lerp(float3(0.018, 0.012, 0.050), float3(0.004, 0.006, 0.018), uv.y);
    float2 logoSize = float2(0.28, 0.235);
    float t = reactionMeta.y;
    float2 travel = 1.0 - logoSize;
    float2 phase = float2(t * 0.075, t * 0.103 + 0.31);
    float2 bounce = 1.0 - abs(frac(phase) * 2.0 - 1.0);
    float2 origin = bounce * travel;
    float2 localUv = (uv - origin) / logoSize;
    if (all(localUv >= 0.0) && all(localUv <= 1.0))
    {
        float4 mark = screensaverTex.Sample(smp, localUv);
        color = lerp(color, mark.rgb, mark.a);
    }
    return float4(saturate(color), 1.0);
}

Texture2D loadingMessageTex : register(t1);
float4 LoadingPS(VOut i, bool isFrontFace : SV_IsFrontFace) : SV_TARGET
{
    ClipSceneDepth(i.pos);
    for (int r = 0; r < uiRectCount; r++)
    {
        float4 rect = uiRects[r];

        if (i.pos.x >= rect.x && i.pos.x < rect.x + rect.z &&
            i.pos.y >= rect.y && i.pos.y < rect.y + rect.w)
        {
            discard;
        }
    }
    // Keep the physical back of the screen behaving exactly like the normal
    // video shader instead of mirroring the loading artwork.
    if (!isFrontFace)
    {
        return float4(0.015, 0.015, 0.02, 1.0); // subtle black plastic back
    }


    // Deep blue/purple background.
    float2 uv = i.uv;
    float3 bgTop    = float3(0.025, 0.020, 0.075);
    float3 bgBottom = float3(0.008, 0.010, 0.030);
    float3 color = lerp(bgTop, bgBottom, uv.y);


    // Very subtle purple illumination behind the spinner.
    float2 center = float2(0.5, 0.43);
    float2 p = uv - center;
    p.x *= SCREEN_ASPECT;


    float d = length(p);
    float halo = 1.0 - smoothstep(0.05, 0.36, d);
    color += float3(0.15, 0.035, 0.32) * halo * 0.32;


    // Animated circular spinner.
    float radius = 0.095;
    float thickness = 0.012;


    float ring =
        smoothstep(radius + thickness, radius, d) *
        smoothstep(radius - thickness, radius, d);


    float angle = atan2(p.y, p.x);
    float phase = angle - reactionMeta.y * 3.0;


    // Bright head with a fading tail around the ring.
    float wrapped = frac(phase / 6.2831853 + 1.0);
    float spinnerAlpha = pow(wrapped, 2.4);


    float3 dimPurple = float3(0.20, 0.07, 0.38);
    float3 hotPurple = float3(0.72, 0.28, 1.00);
    float3 spinner = lerp(dimPurple, hotPurple, spinnerAlpha);


    color = lerp(color, spinner, ring * (0.25 + spinnerAlpha * 0.75));


    // Small bright spinner head.
    float headAngle = reactionMeta.y * 3.0;
    float2 head =
        center +
        float2(cos(headAngle), sin(headAngle)) *
        float2(radius / SCREEN_ASPECT, radius);


    float2 hp = uv - head;
    hp.x *= SCREEN_ASPECT;


    float headGlow =
        1.0 - smoothstep(0.0, 0.022, length(hp));


    color += float3(0.55, 0.20, 1.0) * headGlow;

    // Fixed two-line status under the spinner, only while provider startup is pending.
    if (titleAlpha > 0.0 && uv.y >= 0.57 && uv.y <= 0.81)
    {
        float4 text = loadingMessageTex.Sample(smp, float2(uv.x, (uv.y - 0.57) / 0.24));
        color = lerp(color, text.rgb, text.a);
    }
    return float4(saturate(color), 1.0);
}

float4 AudioPS(VOut i, bool isFrontFace : SV_IsFrontFace) : SV_TARGET
{
    ClipSceneDepth(i.pos);
    for (int r = 0; r < uiRectCount; r++)
    {
        float4 rect = uiRects[r];

        if (i.pos.x >= rect.x &&
            i.pos.x < rect.x + rect.z &&
            i.pos.y >= rect.y &&
            i.pos.y < rect.y + rect.w)
        {
            discard;
        }
    }

    if (!isFrontFace)
    {
        return float4(0.015, 0.015, 0.02, 1.0);
    }

    float2 uv = i.uv;

    // Dark blue-purple background.
    float3 top =
        float3(0.030, 0.018, 0.085);

    float3 bottom =
        float3(0.006, 0.008, 0.025);

    float3 color =
        lerp(top, bottom, uv.y);

    //
    // Soft central purple illumination.
    //
    float2 center =
        float2(0.5, 0.46);

    float2 p =
        uv - center;

    p.x *= SCREEN_ASPECT;

    float dist =
        length(p);

    float halo =
        1.0 -
        smoothstep(0.05, 0.55, dist);

    color +=
        float3(0.16, 0.035, 0.34) *
        halo *
        0.55;

    //
    // Subtle animated background pulse.
    //
    float pulse =
        0.5 +
        0.5 *
        sin(reactionMeta.y * 1.8);

    color +=
        float3(0.025, 0.008, 0.055) *
        pulse *
        halo;

    float3 accent =
    float3(0.72, 0.30, 1.0);

    //
    // Animated equalizer.
    //
    const int BAR_COUNT = 13;

    float eq = 0.0;

    for (int bar = 0;
         bar < BAR_COUNT;
         bar++)
    {
        float barX =
            0.32 +
            ((float)bar /
             (BAR_COUNT - 1)) *
            0.36;

      float audioLevel =
    saturate(reactionMeta.z);

// Boost the real volume reading so normal music
// uses more of the available visual range.
float boostedLevel =
    saturate(
        pow(audioLevel, 0.65) *
        1.45);

// reactionMeta.y is already a continuously accumulated,
// volume-controlled animation phase from ScreenPainter.
float phase =
    reactionMeta.y +
    (float)bar * 1.37;

float secondaryPhase =
    reactionMeta.y * 0.62 +
    (float)bar * 0.73;

float animatedShape =
    0.5 +
    0.5 *
    sin(phase);

animatedShape =
    animatedShape * 0.70 +
    (0.5 +
     0.5 *
     sin(secondaryPhase)) * 0.30;

// Give the individual bars slightly different
// natural heights.
float barSensitivity =
    0.70 +
    0.30 *
    (0.5 +
     0.5 *
     sin(
         (float)bar *
         1.19));

// Even quiet audio gets a tiny amount of movement.
float minimumMotion =
    0.012 +
    animatedShape * 0.012;

// The REAL audio volume controls how energetic
// the animated movement becomes.
float reactiveMotion =
    boostedLevel *
    (
        0.060 +
        animatedShape * 0.090
    ) *
    barSensitivity;

float height =
    minimumMotion +
    reactiveMotion;

        float dx =
            abs(uv.x - barX);

        float dy =
            abs(uv.y - 0.63);

        float barShape =
            (1.0 -
             smoothstep(
                 0.007,
                 0.011,
                 dx)) *

            (1.0 -
             smoothstep(
                 height,
                 height + 0.008,
                 dy));

        eq =
            max(eq, barShape);
    }

    if (audioTextMeta.w > 0)
    {
        uint rgb = (uint)(audioTextMeta.w - 1);
        color = float3((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255) / 255.0;
    }
    // The enum value is supplied through the transition slot only during Classic Bars.
    int theme = (int)reactionMeta.w;
    float t = saturate((uv.x - 0.32) / 0.36);
    float3 eqColor = float3(0.678, 0.478, 1.0);
    if (theme == 1) eqColor = float3(0.208, 0.482, 1.0);
    else if (theme == 2) eqColor = float3(0.0, 0.867, 1.0);
    else if (theme == 3) eqColor = float3(0.0, 0.910, 0.612);
    else if (theme == 4) eqColor = float3(1.0, 0.200, 0.616);
    else if (theme == 5) eqColor = float3(1.0, 0.545, 0.141);
    else if (theme == 6) eqColor = float3(0.922, 0.153, 0.314);
    else if (theme == 7) eqColor = float3(1.0, 1.0, 1.0);
    else if (theme == 8) eqColor = lerp(float3(0.624, 0.224, 1.0), float3(1.0, 0.224, 0.624), t);
    else if (theme == 9) eqColor = lerp(float3(0.208, 0.482, 1.0), float3(0.0, 0.867, 1.0), t);
    else if (theme == 10) eqColor = lerp(float3(1.0, 0.545, 0.141), float3(1.0, 0.224, 0.624), t);
    else if (theme == 11) eqColor = saturate(abs(frac(t + float3(0.0, 0.666667, 0.333333)) * 6.0 - 3.0) - 1.0);
    else if (theme == 12) eqColor = float3(255, 220, 40) / 255.0;
    else if (theme == 13) eqColor = float3(160, 255, 48) / 255.0;
    else if (theme == 14) eqColor = float3(196, 160, 255) / 255.0;
    else if (theme == 15) eqColor = lerp(float3(53, 123, 255), float3(159, 57, 255), t) / 255.0;
    else if (theme == 16) eqColor = lerp(float3(255, 51, 157), float3(0, 221, 255), t) / 255.0;
    else if (theme == 17) eqColor = lerp(float3(0, 232, 156), float3(255, 220, 40), t) / 255.0;
    else if (theme == 18) eqColor = lerp(float3(0, 221, 255), float3(159, 57, 255), 1.0 - abs(2.0 * t - 1.0)) / 255.0;
    else if (theme == 19) eqColor = lerp(float3(255, 51, 157), float3(0, 221, 255), 0.5 - 0.5 * cos(4.0 * 3.14159265 * t)) / 255.0;
    else if (theme == 20) eqColor = lerp(float3(255, 119, 15), float3(159, 57, 255), t) / 255.0;

    color =
        lerp(
            color,
            eqColor,
            eq);

 

    return float4(
        saturate(color),
        1.0);
}

			float4 PS(VOut i, bool isFrontFace : SV_IsFrontFace) : SV_TARGET
			{
                ClipSceneDepth(i.pos);
				for (int r = 0; r < uiRectCount; r++)
				{
					float4 rect = uiRects[r];
					if (i.pos.x >= rect.x && i.pos.x < rect.x + rect.z &&
						i.pos.y >= rect.y && i.pos.y < rect.y + rect.w)
					{
						discard;
					}
				}

if (!isFrontFace)
{
    return float4(0.015, 0.015, 0.02, 1.0); // subtle black plastic back
}

float4 primaryColor = tex.Sample(smp, i.uv);
if (audioTextMeta.w > 0)
{
    uint rgb = (uint)(audioTextMeta.w - 1);
    float3 background = float3((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255) / 255.0;
    float light = max(primaryColor.r, max(primaryColor.g, primaryColor.b));
    primaryColor.rgb = lerp(background, primaryColor.rgb, smoothstep(0.015, 0.12, light));
}

float transitionBlend =
    saturate(reactionMeta.w);

if (transitionBlend <= 0.0)
{
    return primaryColor;
}

float4 transitionColor =
    transitionTex.Sample(smp, i.uv);

return lerp(
    primaryColor,
    transitionColor,
    transitionBlend);
			}

			// Same procedural geometry as VS above, drawn scaled up (via a separate, larger worldViewProj
			// computed on the C# side) - glowScale tells this shader how much bigger than the real screen
			// the current quad is, so it can remap its own 0..1 uv back down to the real screen's uv space
			// before sampling. A cheap 8-tap box blur plus a smoothstep fade toward this quad's own edges
			// turns that into a soft, edge-faded halo instead of just a bigger sharp copy of the video.
			float4 GlowPS(VOut i) : SV_TARGET
			{
                ClipSceneDepth(i.pos);
				for (int r = 0; r < uiRectCount; r++)
				{
					float4 rect = uiRects[r];
					if (i.pos.x >= rect.x && i.pos.x < rect.x + rect.z &&
						i.pos.y >= rect.y && i.pos.y < rect.y + rect.w)
					{
						discard;
					}
				}

				float2 centered = i.uv * 2.0 - 1.0;
				float edgeDist = max(abs(centered.x), abs(centered.y));
				float fade = 1.0 - smoothstep(0.55, 1.0, edgeDist);
				if (fade <= 0.0)
				{
					discard;
				}

				float2 sampleUv = centered * glowScale * 0.5 + 0.5;
				float2 texel = float2(0.01, 0.01) * glowScale;
				float4 sum = tex.Sample(smp, sampleUv);
				sum += tex.Sample(smp, sampleUv + float2(texel.x, 0));
				sum += tex.Sample(smp, sampleUv - float2(texel.x, 0));
				sum += tex.Sample(smp, sampleUv + float2(0, texel.y));
				sum += tex.Sample(smp, sampleUv - float2(0, texel.y));
				sum += tex.Sample(smp, sampleUv + texel);
				sum += tex.Sample(smp, sampleUv - texel);
				sum += tex.Sample(smp, sampleUv + float2(texel.x, -texel.y));
				sum /= 8.0;

				return float4(sum.rgb, sum.a * fade * 0.45);
			}

			// The ""now playing"" banner texture, composited on top of the sharp screen quad using the exact
			// same worldViewProj/geometry as the main draw - it is the same physical rectangle, just with a
			// different pixel shader and texture bound. Only the bottom band of local uv (v close to 1, see
			// the VS above: row 0/bottom maps to uv.y = 1) shows the banner texture; everywhere else this
			// pass discards outright so the sharp video drawn underneath is left untouched.
			#define TITLE_BAND_START 0.82
			Texture2D titleTex : register(t1);

			float4 TitlePS(VOut i) : SV_TARGET
			{
                ClipSceneDepth(i.pos);
				for (int r = 0; r < uiRectCount; r++)
				{
					float4 rect = uiRects[r];
					if (i.pos.x >= rect.x && i.pos.x < rect.x + rect.z &&
						i.pos.y >= rect.y && i.pos.y < rect.y + rect.w)
					{
						discard;
					}
				}

				if (i.uv.y < TITLE_BAND_START)
				{
					discard;
				}

				float2 titleUv = float2(i.uv.x, (i.uv.y - TITLE_BAND_START) / (1.0 - TITLE_BAND_START));
				float4 color = titleTex.Sample(smp, titleUv);
				float finalAlpha = color.a * titleAlpha;
				if (finalAlpha <= 0.01)
				{
					discard;
				}
				return float4(color.rgb, finalAlpha);
			}

            float3 AudioGradientColor(int mode, float x, float time)
            {
                if (mode == 1) // Rainbow
                {
                    float phase = frac(x * 0.9 - time * 0.08);
                    return saturate(abs(frac(phase + float3(0.0, 0.666667, 0.333333))
                        * 6.0 - 3.0) - 1.0);
                }

                float wave = 0.5 + 0.5 * sin(6.2831853 * (x * 0.85 - time * 0.12));
                if (mode == 2) // Aurora
                    return lerp(float3(0.20, 0.96, 0.68), float3(0.72, 0.40, 1.0), wave);
                if (mode == 3) // Sunset
                    return lerp(float3(1.0, 0.68, 0.22), float3(1.0, 0.28, 0.66), wave);
                // Ocean
                return lerp(float3(0.18, 0.82, 1.0), float3(0.24, 0.38, 1.0), wave);
            }

            float4 AudioTextPS(VOut i, bool isFrontFace : SV_IsFrontFace) : SV_TARGET
            {
                ClipSceneDepth(i.pos);
                if (!isFrontFace) discard;
                for (int r = 0; r < uiRectCount; r++)
                {
                    float4 rect = uiRects[r];
                    if (i.pos.x >= rect.x && i.pos.x < rect.x + rect.z &&
                        i.pos.y >= rect.y && i.pos.y < rect.y + rect.w)
                        discard;
                }
                float4 color = titleTex.Sample(smp, i.uv);
                if (color.a <= 0.01) discard;
                // Top and middle gradients share a packed integer, preserving cbuffer layout.
                int packed = (int)audioTextMeta.y;
                int gradient = i.uv.y < (1.0 / 3.0) ? packed % 8 :
                    i.uv.y < (2.0 / 3.0) ? packed / 8 : (int)audioTextMeta.z;
                if (gradient > 0)
                {
                    float3 animated = AudioGradientColor(gradient, i.uv.x, audioTextMeta.x);
                    // Keep the dark text shadow dark; only colour the light glyphs.
                    float glyph = smoothstep(0.08, 0.45,
                        max(color.r, max(color.g, color.b)));
                    color.rgb = lerp(color.rgb, animated, glyph);
                }
                return color;
            }

			// Reactions use a Font Awesome atlas inside high-contrast colored badges. The
			// border and shadow preserve visibility over both very bright and very dark video frames.
			float4 ReactionsPS(VOut i, bool isFrontFace : SV_IsFrontFace) : SV_TARGET
			{
                ClipSceneDepth(i.pos);
				if (!isFrontFace)
				{
					discard;
				}

				for (int r = 0; r < uiRectCount; r++)
				{
					float4 rect = uiRects[r];
					if (i.pos.x >= rect.x && i.pos.x < rect.x + rect.z &&
						i.pos.y >= rect.y && i.pos.y < rect.y + rect.w)
					{
						discard;
					}
				}

				float4 accum = float4(0, 0, 0, 0);
				int count = (int)reactionMeta.x;
				for (int k = 0; k < count; k++)
				{
					float2 pos = reactionPos[k].xy;
					float alpha = reactionPos[k].z;
					float radius = reactionPos[k].w;
					float2 local = float2(
						(i.uv.x - pos.x) * SCREEN_ASPECT,
						i.uv.y - pos.y) / radius;
					if (any(abs(local) > 1.0))
					{
						continue;
					}

					float distanceFromCenter = length(local);
					float2 shadowLocal = local - float2(0.10, 0.12);
					float outerShape =
						1.0 - smoothstep(0.91, 1.0, distanceFromCenter);
					float badgeShape =
						1.0 - smoothstep(0.79, 0.87, distanceFromCenter);
					float shadow =
						max(
							1.0 - smoothstep(0.88, 1.08, length(shadowLocal)) -
							outerShape,
							0.0) *
						alpha *
						0.52;
					float border =
						max(outerShape - badgeShape, 0.0) *
						alpha;
					float badge =
						badgeShape *
						alpha *
						0.50;

					// Shadow first, followed by the dark rim and saturated badge face.
					accum.rgb = accum.rgb * (1.0 - shadow);
					accum.a = accum.a + shadow * (1.0 - accum.a);

					float3 borderColor = float3(0.018, 0.025, 0.055);
					accum.rgb = accum.rgb * (1.0 - border) + borderColor * border;
					accum.a = accum.a + border * (1.0 - accum.a);

					accum.rgb =
						accum.rgb * (1.0 - badge) +
						reactionColor[k].rgb * badge;
					accum.a = accum.a + badge * (1.0 - accum.a);

					float glyphIndex = reactionColor[k].a;
					float2 glyphUv = local * 0.5 + 0.5;
					glyphUv.x = (glyphIndex + glyphUv.x) / 30.0;
					float glyphAlpha = reactionTex.SampleLevel(smp, glyphUv, 0).a;
					float glyph = glyphAlpha * alpha;
					accum.rgb = accum.rgb * (1.0 - glyph) + float3(1, 1, 1) * glyph;
					accum.a = accum.a + glyph * (1.0 - accum.a);
				}

				if (accum.a <= 0.01)
				{
					discard;
				}
				// accum.rgb is premultiplied while the layers are composed above. The render target uses
				// straight-alpha blending, so restore straight color before returning the pixel.
				return float4(accum.rgb / max(accum.a, 0.001), accum.a);
			}" + TvAudioEffectsShader.Source;
        hlsl += TvFrameShader.Source;

        using (var vsb =
     ShaderBytecode.Compile(
         hlsl,
         "VS",
         "vs_4_0"))
        using (var psb =
            ShaderBytecode.Compile(
                hlsl,
                "PS",
                "ps_4_0"))
        using (var loadingPsb =
            ShaderBytecode.Compile(
                hlsl,
                "LoadingPS",
                "ps_4_0"))
        using (var screensaverPsb =
            ShaderBytecode.Compile(hlsl, "ScreensaverPS", "ps_4_0"))
        using (var audioPsb =
            ShaderBytecode.Compile(
                hlsl,
                "AudioPS",
                "ps_4_0"))
        using (var glowPsb =
            ShaderBytecode.Compile(
                hlsl,
                "GlowPS",
                "ps_4_0"))
        using (var tvAudioEffectsPsb = ShaderBytecode.Compile(hlsl, "TvAudioEffectsPS", "ps_4_0"))
        using (var frameVsb = ShaderBytecode.Compile(hlsl, "FrameVS", "vs_4_0"))
        using (var framePsb = ShaderBytecode.Compile(hlsl, "FramePS", "ps_4_0"))
        using (var titlePsb =
            ShaderBytecode.Compile(
                hlsl,
                "TitlePS",
                "ps_4_0"))
        using (var audioTextPsb =
            ShaderBytecode.Compile(hlsl, "AudioTextPS", "ps_4_0"))
        using (var reactionsPsb =
            ShaderBytecode.Compile(
                hlsl,
                "ReactionsPS",
                "ps_4_0"))
        {
            _vs =
                new VertexShader(
                    DxHandler.Device,
                    vsb);

            _ps =
                new PixelShader(
                    DxHandler.Device,
                    psb);

            _loadingPs =
                new PixelShader(
                    DxHandler.Device,
                    loadingPsb);

            _screensaverPs = new PixelShader(DxHandler.Device, screensaverPsb);

            _audioPs =
                new PixelShader(
                    DxHandler.Device,
                    audioPsb);

            _glowPs =
                new PixelShader(
                    DxHandler.Device,
                    glowPsb);

            _tvAudioEffectsPs = new PixelShader(DxHandler.Device, tvAudioEffectsPsb);
            _frameVs = new VertexShader(DxHandler.Device, frameVsb);
            _framePs = new PixelShader(DxHandler.Device, framePsb);

            _titlePs =
                new PixelShader(
                    DxHandler.Device,
                    titlePsb);

            _audioTextPs = new PixelShader(DxHandler.Device, audioTextPsb);

            _reactionsPs =
                new PixelShader(
                    DxHandler.Device,
                    reactionsPsb);
        }


        _sampler = new SamplerState(DxHandler.Device, new SamplerStateDescription
		{
			Filter = Filter.MinMagMipLinear,
			AddressU = TextureAddressMode.Clamp,
			AddressV = TextureAddressMode.Clamp,
			AddressW = TextureAddressMode.Clamp,
			ComparisonFunction = Comparison.Never,
			MinimumLod = 0,
			MaximumLod = float.MaxValue
		});

		_rasterState = new RasterizerState(DxHandler.Device, new RasterizerStateDescription
		{
			FillMode = FillMode.Solid,
			CullMode = SharpDX.Direct3D11.CullMode.None
		});

		//Reversed-Z (near=1, far=0) - the common convention for modern D3D11 engines, matched with a
		//reversed-Z projection matrix below so our own computed depth is on the same scale as the real buffer.
		_depthState = new DepthStencilState(DxHandler.Device, new DepthStencilStateDescription
		{
			IsDepthEnabled = true,
			DepthWriteMask = DepthWriteMask.Zero,
			DepthComparison = Comparison.GreaterEqual
		});

        _noDepthState = new DepthStencilState(
    DxHandler.Device,
    new DepthStencilStateDescription
    {
        IsDepthEnabled = false,
        DepthWriteMask = DepthWriteMask.Zero,
        DepthComparison = Comparison.Always
    });

        //Standard straight-alpha blending for the glow and title-banner passes - the main quad stays fully
        //opaque (BlendState = null further down), only these two extra passes need to blend with whatever
        //is already in the back buffer.
        var alphaBlendDescription = new BlendStateDescription();
		alphaBlendDescription.RenderTarget[0] = new RenderTargetBlendDescription
		{
			IsBlendEnabled = true,
			SourceBlend = BlendOption.SourceAlpha,
			DestinationBlend = BlendOption.InverseSourceAlpha,
			BlendOperation = BlendOperation.Add,
			SourceAlphaBlend = BlendOption.One,
			DestinationAlphaBlend = BlendOption.Zero,
			AlphaBlendOperation = BlendOperation.Add,
			RenderTargetWriteMask = ColorWriteMaskFlags.All,
		};
		_alphaBlend = new BlendState(DxHandler.Device, alphaBlendDescription);

		_cbuf = new Buffer(DxHandler.Device, sizeof(ScreenParams), ResourceUsage.Default,
			BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        _sceneDepthParams = new Buffer(DxHandler.Device, 16, ResourceUsage.Default,
            BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);

		DxHandler.OnPresent += DrawIfReady;
        TvUiRenderer.Attach(DrawAtUiBoundary, BoundaryAvailable, () => KeepGameUiVisibleOverTv, ReleaseUiBoundaryTargets);
        }
        catch { Dispose(); throw; }
	}

    internal void SetAudioOnly(bool audioOnly)
    {
        if (audioOnly && !_isAudioOnly)
        {
            _audioAnimationTime = 0f;
            _audioAnimationSpeed = 0.65f;
            _lastAudioAnimationUpdateUtc =
                DateTime.UtcNow;
        }

        _isAudioOnly =
            audioOnly;

        
    }

    internal void SetClassicBarsTheme(AudioVisualizerTheme theme) => _classicBarsTheme = theme;

    internal void SetAudioLevel(float level)
    {
        _audioLevel =
            Math.Clamp(
                level,
                0f,
                1f);
    }

    internal void SetAudioEffectsPlaybackActive(bool active) => _audioEffectsPlaybackActive = active;

    internal bool ClassicBarsActive => _isAudioOnly;
    internal float AudioLevel => _audioLevel;

    private readonly LoadingConnectionState loadingConnection = new();
    private readonly TitleTextureRenderer loadingConnectionRenderer = new(centered: true);
    private long loadingConnectionTextVersion = -1;
    internal long LoadingConnectionVersion => loadingConnection.Version;
    internal void ResetLoadingConnection() => loadingConnection.Reset();
    internal void SetYouTubeConnectionWaiting(long version, bool waiting)
        => SetConnectionWaiting(version, waiting, LoadingConnectionState.YouTubeMessage);
    internal void SetLiveBroadcastConnectionWaiting(long version, bool waiting)
        => SetConnectionWaiting(version, waiting, LiveBroadcastStream.ConnectingMessage);
    private void SetConnectionWaiting(long version, bool waiting, string message)
    {
        lock (frameLifetime.SyncRoot)
        {
            if (frameLifetime.IsDisposed || !loadingConnection.Set(version, waiting, message) || !waiting) return;
            DxHandler.RunOnRenderThread("AlphaChannel.ConnectionLoadingText", () => frameLifetime.Use(() =>
            {
                if (loadingConnection.Waiting && loadingConnection.Version == version && loadingConnection.Message == message)
                {
                    loadingConnectionRenderer.SetText("Please wait..", message);
                    loadingConnectionTextVersion = version;
                }
            }));
        }
    }

    internal void SetLoading(bool loading)
    {
        if (!loading) loadingConnection.Reset();
        _isLoading = loading;

        if (loading)
        {
            _loadingStartedAtUtc = DateTime.UtcNow;
        }
    }

    internal bool IsLoading => _isLoading;
    internal bool IsScreensaver => _isScreensaver;
    internal nint ScreensaverTextureHandle => _screensaverRenderer.Srv?.NativePointer ?? nint.Zero;
    internal float ScreensaverAnimationTime => (float)Math.Max(0d, (DateTime.UtcNow - _screensaverStartedAtUtc).TotalSeconds);
    internal float LoadingAnimationTime => _loadingStartedAtUtc == DateTime.MinValue
        ? 0f : (float)Math.Max(0d, (DateTime.UtcNow - _loadingStartedAtUtc).TotalSeconds);

    internal void SetScreensaver(string? status)
    {
        lock (frameLifetime.SyncRoot)
        {
        if (frameLifetime.IsDisposed) return;
        var enabled = !string.IsNullOrWhiteSpace(status);
        if (enabled)
        {
            if (requestedScreensaverStatus != status)
            {
                requestedScreensaverStatus = status;
                DxHandler.RunOnRenderThread("AlphaChannel.ScreensaverText", () =>
                    frameLifetime.Use(() => _screensaverRenderer.SetStatus(status!)));
            }
            if (!_isScreensaver)
                _screensaverStartedAtUtc = DateTime.UtcNow;
        }
        _isScreensaver = enabled;
        }
    }

    //Called from VideoEngine whenever the active video texture changes. Pass null to stop painting.
    internal void SetTarget(Texture2D? texture)
	{
        lock (frameLifetime.SyncRoot)
        {
        if (frameLifetime.IsDisposed) return;
		if (ReferenceEquals(texture, _texture))
		{
			return; //Nothing changed.
		}

		_srv?.Dispose();
		_srv = null;
		_texture = texture;

		if (texture != null)
		{
			_srv = new ShaderResourceView(DxHandler.Device, texture, new ShaderResourceViewDescription
			{
				Format = texture.Description.Format,
				Dimension = ShaderResourceViewDimension.Texture2D,
				Texture2D = { MipLevels = texture.Description.MipLevels }
			});
		}
	        }
    }

    internal void SetImageTransitionTarget(
    Texture2D? texture)
    {
        lock (frameLifetime.SyncRoot)
        {
        if (frameLifetime.IsDisposed) return;
        if (ReferenceEquals(
                texture,
                _transitionTexture))
        {
            return;
        }

        _transitionSrv?.Dispose();
        _transitionSrv =
            null;

        _transitionTexture =
            texture;

        _imageTransitionBlend =
            0f;

        if (texture is not null)
        {
            _transitionSrv =
                new ShaderResourceView(
                    DxHandler.Device,
                    texture,
                    new ShaderResourceViewDescription
                    {
                        Format =
                            texture.Description.Format,

                        Dimension =
                            ShaderResourceViewDimension.Texture2D,

                        Texture2D =
                        {
                        MipLevels =
                            texture.Description.MipLevels
                        }
                    });
        }
            }
    }

    internal void SetImageTransitionBlend(
        float blend)
    {
        _imageTransitionBlend =
            _transitionSrv is null
                ? 0f
                : Math.Clamp(
                    blend,
                    0f,
                    1f);
    }

    internal void ClearImageTransition()
    {
        //
        // Hide the transition texture without disposing its SRV while the
        // Present hook may be drawing. The same transition texture is reused
        // for every slide and is released when ScreenPainter is disposed.
        //
        _imageTransitionBlend =
            0f;
    }

    //Cheap per-tick update of where/how big to draw the quad - called continuously so live Settings edits
    //are reflected immediately, without touching the SRV.
    internal void SetTransform(
        Vector3 worldPosition,
        float worldYaw,
        float widthScale,
        float heightScale)
    {
        worldTransform.Update(worldPosition, worldYaw, widthScale, heightScale);
    }

    //Called whenever the queue advances, watch-along metadata resolves, or a viewer's ViewingEntry
    //changes - a no-op on the renderer side unless the title/source pair actually changed. Only a
    //genuine change (not a repeat call with the same text, e.g. WatchAlongSession's per-tick push)
    //restarts the show-then-fade timer.
	internal void SetTitle(string title, string source)
	{
        lock (frameLifetime.SyncRoot)
        {
            if (frameLifetime.IsDisposed || requestedTitle == (title, source)) return;
            requestedTitle = (title, source);
            // Texture uploads must use the game's immediate context only from
            // Present, not from framework/UI or metadata completion threads.
            DxHandler.RunOnRenderThread("AlphaChannel.ScreenTitle", () => frameLifetime.Use(() =>
            {
                if (_titleRenderer.SetText(title, source)) _titleShownAtUtc = DateTime.UtcNow;
            }));
        }
	}

    internal void SetAudioScreenText(AudioScreenTextSelection selection)
    {
        lock (frameLifetime.SyncRoot)
        {
            if (frameLifetime.IsDisposed || requestedAudioText == selection && string.IsNullOrEmpty(selection.CoverId)) return;
            requestedAudioText = selection;
            void Render()
            {
                lock (frameLifetime.SyncRoot)
                {
                    if (frameLifetime.IsDisposed || requestedAudioText is not { } current) return;
                    var cover = current.CoverBase64 ?? spotifyCoverLoader.Get(current.CoverId, Render);
                    var prepared = current with { CoverBase64 = cover };
                    DxHandler.RunOnRenderThread("AlphaChannel.AudioScreenText", () =>
                        frameLifetime.Use(() => _audioTextRenderer.SetText(prepared)));
                }
            }
            Render();
        }
    }

    internal nint AudioScreenTextTextureHandle =>
        _audioTextRenderer.Srv?.NativePointer ?? nint.Zero;

    internal AudioScreenTextSelection AudioScreenTextSelection =>
        _audioTextRenderer.Selection;

    internal float AudioScreenTextAnimationTime =>
        (float)((DateTime.UtcNow - _audioTextClockStartedUtc).TotalSeconds % 1000.0);

	private IReadOnlyList<ReactionParticle> _reactions = System.Array.Empty<ReactionParticle>();

	//Called every tick from Plugin.cs with the current set of active reaction particles - drawn as
	//circles by ReactionsPS. An empty list is the normal idle state, not an error; DrawIfReady just
	//sends a ReactionCount of 0 and the shader discards immediately.
	internal void SetReactions(IReadOnlyList<ReactionParticle> reactions)
	{
		_reactions = reactions;
	}

	private void CollectReactions(ref ScreenParams p)
	{
		int count = Math.Min(_reactions.Count, MaxReactions);
		p.ReactionCount = count;
		for (int i = 0; i < count; i++)
		{
			ReactionParticle r = _reactions[i];
			p.ReactionPos[i * 4 + 0] = r.X;
			p.ReactionPos[i * 4 + 1] = r.Y;
			p.ReactionPos[i * 4 + 2] = r.Alpha;
			p.ReactionPos[i * 4 + 3] = r.Radius;
			p.ReactionColor[i * 4 + 0] = r.R;
			p.ReactionColor[i * 4 + 1] = r.G;
			p.ReactionColor[i * 4 + 2] = r.B;
			p.ReactionColor[i * 4 + 3] = (float)r.Glyph;
		}
	}

	//1 for the first TitleHoldSeconds after a genuine title change, then ramps down to 0 over the
	//following TitleFadeSeconds instead of just vanishing.
	private float ComputeTitleAlpha()
	{
		double elapsed = (DateTime.UtcNow - _titleShownAtUtc).TotalSeconds;
		if (elapsed < TitleHoldSeconds)
		{
			return 1f;
		}

		double fadeProgress = (elapsed - TitleHoldSeconds) / TitleFadeSeconds;
		return (float)Math.Clamp(1.0 - fadeProgress, 0.0, 1.0);
	}

	//Runs every frame from DxHandler's Present hook. Reads the swapchain's own current back buffer and
	//depth buffer straight out of FFXIVClientStructs (no context hook needed) and draws into them.
    internal Func<bool>? WorldScreenVisible { get; set; }
    internal Func<bool>? VisualsSuspended { get; set; }
	private void DrawIfReady()
	{
        if (TvUiRenderer.DrawAtPresent()) return;
        DrawTvFrame();
    }

    private void DrawTvFrame()
	{
        if (!DxHandler.CanUploadVideoFrames) return;
        if (VisualsSuspended?.Invoke() == true) return;
        if (_srv is null
            && !false

            ) return;
        var drawStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (frameLifetime.SyncRoot)
        {
        if (frameLifetime.IsDisposed) return;
    
	    // An idle plugin must not inspect native scene targets at the title
	    // screen or while the character's scene is being torn down.
	    var drawWorldScreen = _srv is not null && WorldScreenVisible?.Invoke() != false;
        var drawDebugMapping = false;
        drawDebugMapping = false;

        if ((!drawWorldScreen && !drawDebugMapping) || !Plugin.ClientState.IsLoggedIn ||
	        !TryGetSceneTargets(out nint rtvPtr, out nint dsvPtr, out nint depthSrvPtr,
                out uint targetWidth, out uint targetHeight, out uint depthWidth, out uint depthHeight))
		{
			return;
		}

        // The UI boundary may target the colour texture consumed by the final
        // composite. Its full output dimensions were checked by the probe.
        if (boundaryDraw && boundaryTargetView != 0) rtvPtr = boundaryTargetView;
        if (restoredScene != null && !TvUiRenderer.CaptureMatches(targetWidth, targetHeight)) return;
		NumericsMatrix4x4? viewProjection = ReadCameraViewProjection();
		if (viewProjection == null)
		{
			return;
		}
        if (!_loggedDepthPath)
        {
            _loggedDepthPath = true;
            
        }

        // Share one camera and placement snapshot across every pass this frame.
        var transform = worldTransform.Read();
        var audioEffectsTime = (float)((DateTime.UtcNow - _audioTextClockStartedUtc).TotalSeconds % 1000.0);
        var countdownActive = PartyCountdown is not null && _countdownRenderer.Srv is not null;
        var audioEffectsActive = !countdownActive && MusicEffects.HasRenderedEffects && _audioEffectsPlaybackActive && !_isLoading && !_isScreensaver;
        NumericsMatrix4x4? worldViewProj = transform.Project(viewProjection.Value);
        NumericsMatrix4x4? glowWorldViewProj = transform.Project(viewProjection.Value, GlowScale);
		ShaderResourceView? titleSrv = _titleRenderer.Srv;
		ShaderResourceView? audioTextSrv = _audioTextRenderer.Srv;
        var connectionText = _isLoading && loadingConnection.Waiting && loadingConnectionTextVersion == loadingConnection.Version
            ? loadingConnectionRenderer.Srv : null;
		float titleAlpha = _isLoading ? connectionText is not null ? 1f : 0f : ComputeTitleAlpha();

		//SharpDX's raw-pointer ComObject constructor does NOT AddRef, but its Dispose() unconditionally
		//Release()s - wrapping a borrowed pointer and disposing it later would silently over-release the
		//engine's own view. AddRef right after construction so our eventual Dispose() is actually balanced.
		if (rtvPtr != _cachedRtvPtr || _cachedRtv == null)
		{
			_cachedRtv?.Dispose();
			_cachedRtv = new RenderTargetView(rtvPtr);
			Marshal.AddRef(rtvPtr);
			_cachedRtvPtr = rtvPtr;
		}
        DepthStencilView? dsv = null;
        if (depthSrvPtr != _sceneDepthSrvPtr)
        {
            _sceneDepthSrv?.Dispose();
            _sceneDepthSrv = null;
            _sceneDepthSrvPtr = 0;
            if (depthSrvPtr != 0)
            {
                Marshal.AddRef(depthSrvPtr);
                try { _sceneDepthSrv = new ShaderResourceView(depthSrvPtr); }
                catch { Marshal.Release(depthSrvPtr); throw; }
                _sceneDepthSrvPtr = depthSrvPtr;
            }
        }

        if (dsvPtr != 0)
        {
            if (dsvPtr != _cachedDsvPtr ||
                _cachedDsv == null)
            {
                _cachedDsv?.Dispose();

                _cachedDsv =
                    new DepthStencilView(dsvPtr);

                Marshal.AddRef(dsvPtr);

                _cachedDsvPtr =
                    dsvPtr;
            }

            dsv =
                _cachedDsv;
        }

        RenderTargetView rtv =
            _cachedRtv;

        DeviceContext ctx = DxHandler.Device!.ImmediateContext;

		// Binding our single target clears the other slots too. Restore all game targets.
		RenderTargetView[] prevRtvs = ctx.OutputMerger.GetRenderTargets(8, out DepthStencilView? prevDsv);
		VertexShader? prevVs = ctx.VertexShader.Get();
		PixelShader? prevPs = ctx.PixelShader.Get();
		InputLayout? prevIl = ctx.InputAssembler.InputLayout;
		PrimitiveTopology prevTopo = ctx.InputAssembler.PrimitiveTopology;
		BlendState? prevBlend = ctx.OutputMerger.BlendState;
		DepthStencilState? prevDss = ctx.OutputMerger.DepthStencilState;
		RasterizerState? prevRs = ctx.Rasterizer.State;

		try
		{
            shaderState.Capture(ctx);
            if (_isAudioOnly)
            {
                DateTime now =
                    DateTime.UtcNow;

                float deltaSeconds =
                    (float)(now -
                        _lastAudioAnimationUpdateUtc)
                        .TotalSeconds;

                _lastAudioAnimationUpdateUtc =
                    now;

                // Protect against a huge jump after a breakpoint,
                // plugin hitch, loading screen, etc.
                deltaSeconds =
                    Math.Clamp(
                        deltaSeconds,
                        0f,
                        0.1f);

                // Same basic range we wanted before:
                // quiet = slow, loud = energetic.
                float targetSpeed =
                    0.65f +
                    (3.40f - 0.65f) *
                    _audioLevel;

                // Smooth changes in speed rather than snapping directly
                // to the new volume-derived speed.
                float speedBlend =
                    1f -
                    MathF.Exp(
                        -deltaSeconds * 7f);

                _audioAnimationSpeed +=
                    (targetSpeed -
                     _audioAnimationSpeed) *
                    speedBlend;

                // Crucially, accumulate phase instead of doing
                // elapsedTime * changingSpeed.
                _audioAnimationTime +=
                    deltaSeconds *
                    _audioAnimationSpeed;
            }

            var p = new ScreenParams
            {
                WorldViewProj =
          worldViewProj.Value,

                Curvature =
          Curvature,

                TitleAlpha =
          titleAlpha,

                LoadingTime =
    _isScreensaver
        ? (float)(DateTime.UtcNow - _screensaverStartedAtUtc).TotalSeconds
        : _isAudioOnly
        ? _audioAnimationTime
        : _isLoading
            ? (float)(DateTime.UtcNow -
                _loadingStartedAtUtc).TotalSeconds
            : 0f,

                // HLSL sees these as reactionMeta.z and reactionMeta.w.
                ReactionPad1 =
    _audioLevel,

                // AudioPS uses this otherwise unused transition slot for its colour theme.
                ImageTransitionBlend =
    _isAudioOnly ? (float)_classicBarsTheme : _imageTransitionBlend,

                AudioTextTime =
                    audioEffectsTime,
                AudioTopGradient = (float)((int)_audioTextRenderer.Selection.TopGradient +
                    8 * (string.IsNullOrEmpty(_audioTextRenderer.Selection.CoverBase64) ? (int)_audioTextRenderer.Selection.MiddleGradient : 0)),
                AudioBottomGradient = (float)_audioTextRenderer.Selection.BottomGradient
            };

            p.UiRectCount = 0;
            if (KeepGameUiVisibleOverTv
                && !boundaryDraw
                )
            {
                var ui = System.Threading.Volatile.Read(ref uiRectangleSnapshot);
                p.UiRectCount = ui.Length / 4;
                for (var i = 0; i < ui.Length; i++) p.UiRects[i] = ui[i];
            }
            p.AudioTextPadding = musicSceneActive && !_isLoading && !_isScreensaver ? MusicEffects.Background + 1 : 0;
            TvUiRenderer.NoteRender(targetWidth, targetHeight, depthWidth, depthHeight, depthSrvPtr != 0, p.UiRectCount);
            CollectReactions(ref p);

            ctx.OutputMerger.SetRenderTargets(dsv, rtv);
            // Binding the output without the scene DSV permits its existing
            // read-only SRV to be sampled. No texture copy or CPU readback.
            var depthParameters = depthSrvPtr != 0
                ? new Vector4(1f / targetWidth, 1f / targetHeight, depthWidth, depthHeight)
                : Vector4.Zero;
            ctx.UpdateSubresource(ref depthParameters, _sceneDepthParams);
            ctx.PixelShader.SetConstantBuffer(1, _sceneDepthParams);
            ctx.PixelShader.SetShaderResource(5, _sceneDepthSrv);
			//Explicit full-target viewport - we never set this before, so whatever viewport the game's last
			//draw call before Present left bound (could be a sub-region: UI element, shadow pass, anything)
			//stayed active, meaning our correctly-computed NDC(0,0) landed at that viewport's center instead
			//of the actual screen center. That's the "math is right but it's drawn in the wrong place" gap.
			ctx.Rasterizer.SetViewport(0, 0, targetWidth, targetHeight, 0, 1);
			ctx.InputAssembler.InputLayout = null;
			ctx.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleStrip;
			ctx.Rasterizer.State = _rasterState;
            ctx.OutputMerger.DepthStencilState =
    dsv is not null
        ? _depthState
        : _noDepthState;
            ctx.VertexShader.Set(_vs);
			ctx.VertexShader.SetConstantBuffer(0, _cbuf);
			ctx.PixelShader.SetConstantBuffer(0, _cbuf);
            ctx.PixelShader.SetShaderResource(
                0,
                countdownActive ? _countdownRenderer.Srv : _srv);

            ctx.PixelShader.SetShaderResource(
                2,
                _transitionSrv);

            ctx.PixelShader.SetShaderResource(3, _screensaverRenderer.Srv);
            ctx.PixelShader.SetShaderResource(4, _reactionRenderer.Srv);

            ctx.PixelShader.SetSampler(
                0,
                _sampler);


            if (!drawWorldScreen) return;

            if (restoredScene != null)
            {
                // Remove native UI under the whole surface, not merely pixels
                // where TV depth passes. The next passes retain world occlusion.
                ScreenParams* restoreParams = &p;
                ctx.UpdateSubresource(new SharpDX.DataBox((nint)restoreParams), _cbuf);
                ctx.OutputMerger.BlendState = null;
                ctx.OutputMerger.DepthStencilState = _noDepthState;
                ctx.PixelShader.Set(sceneRestoreShader);
                ctx.PixelShader.SetShaderResource(0, restoredScene);
                ctx.Draw(VertexCount, 0);
                ctx.OutputMerger.DepthStencilState = dsv is not null ? _depthState : _noDepthState;
                ctx.PixelShader.SetShaderResource(0, countdownActive ? _countdownRenderer.Srv : _srv);
            }

            if (audioEffectsActive)
            {
                ScreenParams effectsParams = p;
                effectsParams.WorldViewProj = transform.Project(viewProjection.Value, TvAudioEffectsShader.Scale);
                effectsParams.Curvature = 0f;
                // audioTextMeta.w is unused by the text pass; only this effects
                // pass receives the host-controlled scene feature mask.
                var scene = MusicEffects;
                effectsParams.AudioTextPadding = scene.EffectiveMask & ~(128 | 8192 | MusicSceneEffects.Spotlight);
                // Reuse reaction colours only in the separate effects pass.
                // The main/text/reaction passes retain their original parameters.
                ReadOnlySpan<uint> effectColours = [scene.Speakers, scene.Lasers, scene.Halo, scene.Leds, scene.Rings, scene.Sparkles, scene.Spotlights, scene.DanceFloor, scene.DiscoBall, 0, scene.NeonFrame, scene.Orbits, 0, scene.Curtain];
                for (var c = 0; c < effectColours.Length; c++)
                {
                    var rgb = effectColours[c];
                    effectsParams.ReactionColor[c * 4] = ((rgb >> 16) & 255) / 255f;
                    effectsParams.ReactionColor[c * 4 + 1] = ((rgb >> 8) & 255) / 255f;
                    effectsParams.ReactionColor[c * 4 + 2] = (rgb & 255) / 255f;
                    effectsParams.ReactionColor[c * 4 + 3] = 1f;
                }
                effectsParams.ReactionColor[3 * 4 + 3] = scene.RainbowLeds ? 1f : 0f;
                ScreenParams* effectsPp = &effectsParams;
                ctx.OutputMerger.BlendState = _alphaBlend;
                ctx.PixelShader.Set(_tvAudioEffectsPs);
                if ((scene.Mask & 8192) != 0)
                {
                    var mainEffects = effectsParams;
                    effectsParams.WorldViewProj = transform.ProjectBackdrop(viewProjection.Value, TvAudioEffectsShader.Scale);
                    effectsParams.AudioTextPadding = 8192;
                    ctx.UpdateSubresource(new SharpDX.DataBox((nint)effectsPp), _cbuf);
                    ctx.Draw(VertexCount, 0);
                    effectsParams = mainEffects;
                }
                ctx.UpdateSubresource(new SharpDX.DataBox((nint)effectsPp), _cbuf);
                ctx.Draw(VertexCount, 0);
                if ((scene.EffectiveMask & 128) != 0 || scene.IsSpooky)
                {
                    // A separate horizontal quad; depth clipping keeps it behind furniture.
                    var y = transform.Position.Y - .6f * transform.HeightScale + scene.FloorOffset + .025f;
                    effectsParams.WorldViewProj = transform.ProjectFloor(viewProjection.Value, y);
                    effectsParams.AudioTextPadding = scene.IsSpooky ? 128 | MusicSceneEffects.SpookyTime : 128;
                    ctx.UpdateSubresource(new SharpDX.DataBox((nint)effectsPp), _cbuf);
                    ctx.Draw(VertexCount, 0);
                }
            }

            //Glow first, so the sharp screen quad drawn right after composites on top of it.
            if (!_isLoading && glowWorldViewProj != null)
        {
				ScreenParams glowParams = p;
				glowParams.WorldViewProj = glowWorldViewProj.Value;
				glowParams.GlowScale = GlowScale;
				ScreenParams* glowPp = &glowParams;
				ctx.UpdateSubresource(new SharpDX.DataBox((nint)glowPp), _cbuf);

				ctx.OutputMerger.BlendState = _alphaBlend;
				ctx.PixelShader.Set(_glowPs);
				ctx.Draw(VertexCount, 0);
			}

            var frame = FrameSelection;
            if (frame.Style != TvFrameStyle.None && TvFrameAccess.CanRender(_isAudioOnly, musicSceneActive && _audioEffectsPlaybackActive)
                && PreparePremiumFrame(frame))
            {
                var frameParams = p;
                frameParams.AudioTextPadding = (float)frame.Style;
                frameParams.AudioBottomGradient = (float)frame.Glow;
                frameParams.ReactionColor[0] = ((frame.Colour >> 16) & 255) / 255f;
                frameParams.ReactionColor[1] = ((frame.Colour >> 8) & 255) / 255f;
                frameParams.ReactionColor[2] = (frame.Colour & 255) / 255f;
                ScreenParams* framePp = &frameParams;
                ctx.UpdateSubresource(new SharpDX.DataBox((nint)framePp), _cbuf);
                ctx.OutputMerger.BlendState = _alphaBlend;
                ctx.VertexShader.Set(_frameVs);
                ctx.PixelShader.Set(_framePs);
                if (PremiumTvFrameArt.IsPremium(frame.Style)) ctx.PixelShader.SetShaderResource(1, premiumFrameView);
                ctx.Draw(VertexCount, 0);
                ctx.PixelShader.SetShaderResource(1, null);
                ctx.VertexShader.Set(_vs);
            }
			//p is a stack local, its address is already stable, no `fixed` needed to take it.
			ScreenParams* pp = &p;
			ctx.UpdateSubresource(new SharpDX.DataBox((nint)pp), _cbuf);

            ctx.OutputMerger.BlendState =
                null;

            if (countdownActive)
            {
                ctx.PixelShader.Set(_ps);
            }
            else if (_isScreensaver)
            {
                ctx.PixelShader.Set(_screensaverPs);
            }
            else if (_isLoading)
            {
                ctx.PixelShader.SetShaderResource(1, connectionText);
                ctx.PixelShader.Set(
                    _loadingPs);
            }
            else if (_isAudioOnly)
            {
                ctx.PixelShader.Set(
                    _audioPs);
            }
            else
            {
                ctx.PixelShader.Set(
                    _ps);
            }

            ctx.Draw(
                VertexCount,
                0);
            boundaryDrawSerial++;

            if (_isLoading) ctx.PixelShader.SetShaderResource(1, null);
            if (!countdownActive && !_isLoading && audioTextSrv != null)
            {
                ctx.OutputMerger.BlendState = _alphaBlend;
                ctx.PixelShader.Set(_audioTextPs);
                ctx.PixelShader.SetShaderResource(1, audioTextSrv);
                ctx.Draw(VertexCount, 0);
                ctx.PixelShader.SetShaderResource(1, null);
            }

            //Now-playing banner, composited on top of the sharp quad - same worldViewProj/cbuffer already
            //bound from the main draw above, just a different pixel shader and a second texture.
            if (!countdownActive && !_isLoading && titleSrv != null && titleAlpha > 0f && audioTextSrv == null)
        {
				ctx.OutputMerger.BlendState = _alphaBlend;
				ctx.PixelShader.Set(_titlePs);
				ctx.PixelShader.SetShaderResource(1, titleSrv);
				ctx.Draw(VertexCount, 0);
				ctx.PixelShader.SetShaderResource(1, null);
			}

        //Reactions, drawn on top of everything - same worldViewProj/cbuffer already bound and
        //uploaded from the main draw above (CollectReactions filled p's reaction fields before
        //that single UpdateSubresource call), no texture involved so no second shader resource
        //slot to bind/unbind either.
        if (!_isLoading && p.ReactionCount > 0)
        {
				ctx.OutputMerger.BlendState = _alphaBlend;
				ctx.PixelShader.Set(_reactionsPs);
				ctx.Draw(VertexCount, 0);
			}

            ctx.PixelShader.SetShaderResource(
    2,
    null);
            ctx.PixelShader.SetShaderResource(3, null);
            ctx.PixelShader.SetShaderResource(4, null);
            ctx.PixelShader.SetShaderResource(5, null);

            ctx.PixelShader.SetShaderResource(
                0,
                null);
        }
		finally
		{
            try
            {
            // Also unbind on exceptional exits before restoring a DSV which
            // may reference this same scene depth resource.
            ctx.PixelShader.SetShaderResource(5, null);
			ctx.OutputMerger.SetRenderTargets(prevDsv, prevRtvs);
			ctx.VertexShader.Set(prevVs);
			ctx.PixelShader.Set(prevPs);
			ctx.InputAssembler.InputLayout = prevIl;
			ctx.InputAssembler.PrimitiveTopology = prevTopo;
			ctx.OutputMerger.BlendState = prevBlend;
			ctx.OutputMerger.DepthStencilState = prevDss;
			ctx.Rasterizer.State = prevRs;
            }
            finally
            {
                try { shaderState.Restore(ctx); }
                finally
                {
                    // State restoration can fail after a device/driver error;
                    // still release every reference acquired for this frame.
                    ResourceCleanup.DisposeAll(prevRtvs);
                    ResourceCleanup.DisposeAll(prevDsv, prevVs, prevPs, prevIl,
                        prevBlend, prevDss, prevRs);
                }
            }
            PlaybackRenderDiagnostics.RecordTvDraw(System.Diagnostics.Stopwatch.GetElapsedTime(drawStarted).TotalMilliseconds);

		}
	        }
    }

    private float[] uiRectangleSnapshot = [];

    // Framework thread only: Present must never dereference live addon nodes
    // or call GetBounds while the game can destroy those nodes.
    internal void CaptureUiRectangles()
    {
        var snapshot = Array.Empty<float>();
        if (Plugin.ClientState.IsLoggedIn && KeepGameUiVisibleOverTv)
        {
            ScreenParams parameters = default;
            var count = CollectUiRects(ref parameters);
            snapshot = new float[count * 4];
            for (var i = 0; i < snapshot.Length; i++) snapshot[i] = parameters.UiRects[i];
        }
        System.Threading.Volatile.Write(ref uiRectangleSnapshot, snapshot);
    }

	private static int CollectUiRects(ref ScreenParams p)
	{
		GfxGui.AtkStage* stage = GfxGui.AtkStage.Instance();
		if (stage == null || stage->RaptureAtkUnitManager == null)
		{
			return 0;
		}

		//Some loaded addons are invisible-but-"IsVisible" full-screen backdrops/overlays (loading-screen
		//fades, click-blockers, etc.), not actual windows - a single one of those among AllLoadedUnitsList
		//would otherwise mask out the entire draw. Real windows never span nearly the whole screen.
		float maxWidth = stage->ScreenSize.Width * 0.9f;
		float maxHeight = stage->ScreenSize.Height * 0.9f;

		System.Span<FFXIVClientStructs.Interop.Pointer<GfxGui.AtkUnitBase>> entries = stage->RaptureAtkUnitManager->AllLoadedUnitsList.Entries;
		int count = 0;

		for (int i = 0; i < entries.Length && count < MaxUiRects; i++)
		{
			GfxGui.AtkUnitBase* unit = entries[i].Value;
			if (unit == null || unit->WindowNode == null)
				continue;
			TryAppendUiRect(unit, ref p, ref count, maxWidth, maxHeight, 0f);
		}

		return count;
	}

	private static void TryAppendUiRect(
		GfxGui.AtkUnitBase* unit,
		ref ScreenParams p,
		ref int count,
		float maxWidth,
		float maxHeight,
		float padding)
	{
		if (count >= MaxUiRects || unit == null || !unit->IsVisible || unit->Alpha == 0 ||
			unit->RootNode == null || unit->RootNode->Color.A == 0)
		{
			return;
		}

		// GetBounds is the same engine-computed bounding box FFXIV itself uses for mouse collision.
		FFXIVClientStructs.FFXIV.Common.Math.Bounds bounds;
		unit->RootNode->GetBounds(&bounds);

		float width = bounds.Pos2.X - bounds.Pos1.X;
		float height = bounds.Pos2.Y - bounds.Pos1.Y;
		if (width <= 0 || height <= 0 || width >= maxWidth || height >= maxHeight)
			return;

		int baseIndex = count * 4;
		p.UiRects[baseIndex + 0] = bounds.Pos1.X - padding;
		p.UiRects[baseIndex + 1] = bounds.Pos1.Y - padding;
		p.UiRects[baseIndex + 2] = width + padding * 2f;
		p.UiRects[baseIndex + 3] = height + padding * 2f;
		count++;
	}

    //Pure memory reads - no hooking, no calling into the game. Colour comes from the swapchain's own back
    //buffer (that's what's actually presented), but depth comes from RenderTargetManager's own DepthStencil
    //field - the real opaque-scene depth buffer, as opposed to the swapchain's own DepthStencil (which only
    //ever holds a stale clear value by this point in the frame).
    private static bool TryGetSceneTargets(
    out nint rtvPtr,
    out nint dsvPtr,
    out nint depthSrvPtr,
    out uint width,
    out uint height,
    out uint depthWidth,
    out uint depthHeight)
    {
        rtvPtr = 0;
        dsvPtr = 0;
        depthSrvPtr = 0;
        width = 0;
        height = 0;
        depthWidth = depthHeight = 0;

        GfxKernel.Device* device =
            GfxKernel.Device.Instance();

        if (device == null ||
            device->SwapChain == null)
        {
            return false;
        }

        GfxKernel.Texture* backBuffer =
            device->SwapChain->BackBuffer;

        if (backBuffer == null ||
            backBuffer->MipRenderTargets == null)
        {
            return false;
        }

        // Every world-space pass requires scene depth. Rendering without it
        // makes the TV and effects incorrectly cover foreground characters.
        rtvPtr =
            (nint)backBuffer
                ->MipRenderTargets[0]
                .D3D11RenderTargetViewOrDepthStencilView;

        width =
            device->SwapChain->Width;

        height =
            device->SwapChain->Height;

        // Window/output metadata can change before all scene targets have
        // caught up. Skip the transition rather than stretching an old target.
        if (width == 0 || height == 0 || backBuffer->ActualWidth != width || backBuffer->ActualHeight != height)
            return false;

        if (rtvPtr == 0)
        {
            return false;
        }

        var rtm =
            FFXIVClientStructs.FFXIV.Client.Graphics.Render
                .RenderTargetManager.Instance();

        if (rtm == null)
        {
            return false;
        }

        GfxKernel.Texture* sceneDepth =
            rtm->DepthStencil;

        if (sceneDepth == null ||
            sceneDepth->MipRenderTargets == null)
        {
            return false;
        }

        // The manager's logical resolution is not the D3D allocation size.
        // A scaled viewport can live inside a full-sized depth allocation.
        depthWidth = sceneDepth->ActualWidth;
        depthHeight = sceneDepth->ActualHeight;
        if (depthWidth == 0 || depthHeight == 0 ||
            depthWidth > sceneDepth->AllocatedWidth || depthHeight > sceneDepth->AllocatedHeight)
            return false;
        if (depthWidth == width && depthHeight == height &&
            sceneDepth->AllocatedWidth == width && sceneDepth->AllocatedHeight == height)
        {
            dsvPtr =
                (nint)sceneDepth
                    ->MipRenderTargets[0]
                    .D3D11RenderTargetViewOrDepthStencilView;
            return dsvPtr != 0;
        }
        // Scale output pixel coordinates into the active scene-depth viewport
        // instead of disabling depth testing at a different render resolution.
        depthSrvPtr = (nint)sceneDepth->D3D11ShaderResourceView;
        return depthSrvPtr != 0;
    }

    private NumericsMatrix4x4? ReadCameraViewProjection()
	{
		//The "active game camera" (Client.Game.Control.CameraManager) is a different object from the plain
		//scene-graph camera (Client.Graphics.Scene.CameraManager) - go through the game-level camera and
		//down into its embedded scene camera instead of grabbing the scene one directly.
		GameControl.CameraManager* gameCameraManager = GameControl.CameraManager.Instance();
		if (gameCameraManager == null)
		{
			return null;
		}

		FFXIVClientStructs.FFXIV.Client.Game.Camera* gameCamera = gameCameraManager->GetActiveCamera();
		if (gameCamera == null)
		{
			return null;
		}

		GfxScene.Camera* camera = &gameCamera->CameraBase.SceneCamera;
		FFXIVClientStructs.FFXIV.Client.Graphics.Render.Camera* renderCamera = camera->RenderCamera;
		if (renderCamera == null)
		{
			return null;
		}

        // Keep the established world-space view and reversed-Z projection.
        Vector3 position = camera->Position;
        Vector3 lookAt = camera->LookAtVector;
        return ScreenRenderTransform.BuildCameraViewProjection(position, lookAt,
            renderCamera->FoV, renderCamera->AspectRatio, renderCamera->NearPlane, renderCamera->FarPlane);
	}

	public void Dispose()
	{
        spotifyCoverLoader.Dispose();
        frameLifetime.Dispose(() =>
        {
		DxHandler.CancelRenderThreadWork("AlphaChannel.ScreenTitle");
		DxHandler.CancelRenderThreadWork("AlphaChannel.AudioScreenText");
		DxHandler.CancelRenderThreadWork("AlphaChannel.ScreensaverText");
        DxHandler.CancelRenderThreadWork("AlphaChannel.MusicPreviewText");
		DxHandler.OnPresent -= DrawIfReady;

        sceneRestoreShader?.Dispose();

        DxHandler.CancelRenderThreadWork("AlphaChannel.ConnectionLoadingText");
        ResourceCleanup.DisposeAll(loadingConnectionRenderer, _titleRenderer, _audioTextRenderer, _musicPreviewTextRenderer,
            _reactionRenderer, _srv, _transitionSrv, _cachedRtv, _cachedDsv, _cbuf, _alphaBlend,
            _sceneDepthSrv, _sceneDepthParams,
            _noDepthState, _depthState, _rasterState, _sampler, _titlePs, _audioTextPs,
            _reactionsPs, _frameVs, _framePs, premiumFrameView, premiumFrameTexture, _tvAudioEffectsPs, _glowPs, _audioPs, _loadingPs, _screensaverPs,
            _screensaverRenderer, _countdownRenderer, _ps, _vs);
            });
    }
}
