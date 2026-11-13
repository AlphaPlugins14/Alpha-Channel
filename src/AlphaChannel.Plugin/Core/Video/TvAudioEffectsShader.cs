namespace AlphaChannel.Plugin.Video;

// A transparent world-space pass surrounding the TV. No media filters or VFX assets.
internal static class TvAudioEffectsShader
{
    internal const float Scale = 2.6f;
    internal const string Source = """
float PumpkinBody(float2 p)
{
    // Three overlapping lobes give the fruit broad shoulders and a shallow top cleft.
    float middle = length((p - float2(0,.04)) / float2(.70,.77));
    float lobes = length((p - float2(p.x < 0 ? -.35 : .35,.05)) / float2(.61,.72));
    return 1 - smoothstep(.97,1.015,min(middle,lobes));
}
float PumpkinFace(float2 p)
{
    // Tapered triangular eyes, a small nose and a curved carved grin with two teeth.
    float ey = (p.y + .37) / .27;
    float eyes = (1-smoothstep(.13*ey,.13*ey+.018,abs(abs(p.x)-.32))) * step(0,ey) * step(ey,1);
    float ny = (p.y + .09) / .17;
    float nose = (1-smoothstep(.075*ny,.075*ny+.015,abs(p.x))) * step(0,ny) * step(ny,1);
    float top = .24 - .32*p.x*p.x;
    float bottom = .48 - .62*p.x*p.x;
    float smile = smoothstep(top-.018,top+.018,p.y) * (1-smoothstep(bottom-.018,bottom+.018,p.y)) *
        (1-smoothstep(.53,.56,abs(p.x)));
    float teeth = step(abs(p.x+.18),.045)*step(p.y,top+.10) + step(abs(p.x-.19),.045)*step(bottom-.08,p.y);
    return saturate(max(max(eyes,nose),smile*(1-saturate(teeth))));
}
float SpookyShape(float2 p, int kind, float t)
{
    if (kind == 0) // Jack-o'-lantern, stem, glowing eyes and jagged smile.
    {
        float body = PumpkinBody(p);
        float stem = (1-smoothstep(.075,.095,abs(p.x-.13*(p.y+.7)))) * step(-1.04,p.y)*step(p.y,-.60);
        return max(body, stem);
    }
    if (kind == 1) // Rounded ghost with a scalloped hem.
    {
        float head = 1 - smoothstep(.58, .66, length(p - float2(0, -.28)));
        float body = (1 - smoothstep(.59, .67, abs(p.x))) * step(-.28, p.y) *
            (1 - smoothstep(.65 + .10 * cos(p.x * 15), .73 + .10 * cos(p.x * 15), p.y));
        float eyes = max(1 - smoothstep(.07, .11, length(p - float2(-.22, -.22))),
                        1 - smoothstep(.07, .11, length(p - float2(.22, -.22))));
        return max(head, body) * (1 - eyes);
    }
    return 0;
}
float3 SpookyTint(int kind) { return kind == 0 ? float3(1, .28, .025) : kind == 1 ? float3(.75, .90, 1) : float3(.42, .13, .68); }
float4 PumpkinSprite(float2 p, float t, float turn)
{
        float shape = SpookyShape(p,0,t);
        float body = PumpkinBody(p);
        float surface = sqrt(saturate(1-p.x*p.x/1.02-p.y*p.y/.80));
        float longitude = asin(clamp(p.x,-.99,.99));
        float groove = pow(.5+.5*cos(longitude*10),10);
        float light = saturate(.20+.58*surface-.18*p.x-.13*p.y);
        float3 tint = lerp(float3(.32,.055,.008),float3(1,.46,.045),light) * (1-groove*.20);
        float highlight = exp(-pow((p.x+.34)*5,2)-pow((p.y+.26)*4,2));
        tint += float3(.20,.09,.015)*highlight;
        float stem = shape*(1-body);
        tint = lerp(tint,float3(.25,.32,.065),stem);
        float2 facePoint = float2((p.x-turn)/sqrt(1-turn*turn),p.y);
        float face = PumpkinFace(facePoint)*body;
        float flicker = .91+.06*sin(t*6.3)+.03*sin(t*13.7);
        tint = lerp(tint,float3(1,.72,.16)*flicker,face);
    return float4(tint, shape);
}
float4 SpookySprite(float2 p, int kind, float t)
{
    if (kind == 0) return PumpkinSprite(p,t,0);
    return float4(SpookyTint(kind),SpookyShape(p,kind,t));
}
float BeamIntensity(float2 coord, float2 origin, float2 direction)
{
    float2 delta = coord - origin;
    float along = dot(delta, direction);
    float across = abs(delta.x * direction.y - delta.y * direction.x);
    float fade = smoothstep(0.0, 0.12, along) * (1.0 - smoothstep(0.95, 1.35, along));
    return (exp(-across * across * 1400.0) * 0.23 + exp(-across * across * 35000.0) * 0.65) * fade;
}
float4 TvAudioEffectsPS(VOut i) : SV_TARGET
{
    ClipSceneDepth(i.pos);
    for (int r = 0; r < uiRectCount; r++)
    {
        float4 rect = uiRects[r];
        if (i.pos.x >= rect.x && i.pos.x < rect.x + rect.z &&
            i.pos.y >= rect.y && i.pos.y < rect.y + rect.w) discard;
    }
    // Coordinates relative to the original TV, with its physical aspect ratio.
    float2 coord = (i.uv * 2.0 - 1.0) * 2.6 * float2(1.0, 0.6);
    int features = (int)audioTextMeta.w;
    if (features == 0) discard;
    if (features == (128 | 16384))
    {
        float2 tiles = i.uv * float2(12, 8);
        float2 cell = floor(tiles);
        int kind = ((int)cell.x + (int)cell.y) % 2;
        float2 local = frac(tiles);
        // Match the regular floor's diagonal sweep, speed and fade depth.
        float diagonal = cell.x + cell.y * 1.7;
        float wave = .5 + .5 * sin(audioTextMeta.x * 3.0 - diagonal * .7);
        float4 sprite = SpookySprite((local - .5) * 2.6, kind, audioTextMeta.x);
        float shape = sprite.a;
        float edge = 1 - smoothstep(.015, .055, min(min(local.x, 1-local.x), min(local.y, 1-local.y)));
        float3 tint = kind == 0 ? float3(1,.30,.025) : float3(.45,.15,.8);
        float3 artwork = kind == 0 ? sprite.rgb : tint*.9;
        float fade = smoothstep(0,.04,min(min(i.uv.x,1-i.uv.x),min(i.uv.y,1-i.uv.y)));
        float brightness = .25 + wave * .65 + edge * .2;
        return float4((tint*.13+artwork*shape*.85)*brightness,
            fade * (.16 + wave * .28 + edge * .28) * (.65 + saturate(reactionMeta.z)));
    }
    if (features == 128)
    {
        float2 tiles = i.uv * float2(12.0, 8.0);
        float2 local = frac(tiles);
        float edge = min(min(local.x, 1.0 - local.x), min(local.y, 1.0 - local.y));
        float cell = floor(tiles.x) + floor(tiles.y) * 1.7;
        float wave = 0.5 + 0.5 * sin(audioTextMeta.x * 3.0 - cell * 0.7);
        float border = 1.0 - smoothstep(0.015, 0.055, edge);
        float fade = smoothstep(0.0, 0.04, min(min(i.uv.x, 1.0 - i.uv.x), min(i.uv.y, 1.0 - i.uv.y)));
        float3 rainbow = 0.5 + 0.5 * cos(cell * 0.5 + audioTextMeta.x * .6 + float3(0, 2.1, 4.2));
        float3 tint = lerp(reactionColor[7].rgb, rainbow, .35);
        return float4(tint * (.25 + wave * .65 + border * .2),
            fade * (.16 + wave * .28 + border * .28) * (.65 + saturate(reactionMeta.z)));
    }
    // Keep all decorative layers clear of the actual TV image.
    if (features != 8192 && abs(coord.x) < 1.025 && abs(coord.y) < 0.615) discard;
    float t = audioTextMeta.x;
    float level = saturate(reactionMeta.z);
    // Keep the scene present through silence and track gaps. Audio adds
    // brightness above this idle baseline rather than switching effects off.
    float energy = 0.65 + 0.35 * smoothstep(0.015, 0.18, level);
    float pulse = 0.35 + 0.65 * level;
    // RMS changes gently on compressed radio audio. Add visible cone motion
    // while audio is present; its excursion and glow still follow the level.
    float conePump = 0.5 + 0.5 * sin(t * 12.56637);
    float coneExcursion = conePump * level;
    float3 colour = 0;
    float alpha = 0;
    if ((features & 16384) != 0)
    {
        // Small translucent lanterns resting on the two speaker cabinets.
        for (int pumpkin = 0; pumpkin < 2; pumpkin++)
        {
            float2 centre = float2(pumpkin == 0 ? -1.36 : 1.36, -.648);
            float2 delta = (coord - centre) / .18;
            float4 sprite = PumpkinSprite(delta,t+ pumpkin,0);
            colour += sprite.rgb * sprite.a * .65; alpha += sprite.a * .55;
        }
        for (int ghost = 0; ghost < 2; ghost++)
        {
            float side = ghost == 0 ? -1 : 1;
            float2 centre = float2(side * (2.18 + .12 * sin(t * .4 + ghost)), -.23 + .25 * sin(t * .65 + ghost * 3));
            float4 sprite = SpookySprite((coord-centre)/.19, 1, t);
            float fade = .42 + .30 * sin(t * .6 + ghost * 2);
            colour += sprite.rgb * sprite.a * fade; alpha += sprite.a * fade;
        }
        for (int wisp = 0; wisp < 10; wisp++)
        {
            float phase = t*.24+wisp*.628;
            float2 centre = float2(cos(phase)*2.25,sin(phase)*1.31);
            float glow = exp(-dot(coord-centre,coord-centre)*430);
            float core = exp(-dot(coord-centre,coord-centre)*3400);
            float3 tint = wisp%2==0 ? float3(1,.35,.06) : float3(.65,.18,1);
            colour += tint*(glow*.45+core); alpha += glow*.5+core*.6;
        }
    }
    if ((features & 2) != 0)
    {
        float distanceToTv = length(max(abs(coord) - float2(1.0, 0.6), 0.0));
        float haloLight = exp(-distanceToTv * distanceToTv * 24.0) * pulse * (0.85 + 0.15 * sin(t * 0.3));
        float3 haloColour = reactionColor[2].rgb;
        colour += haloColour * haloLight * 0.7;
        alpha += haloLight * 0.32;
    }
    // Two speaker panels, each with two glowing cones.
    for (int sideIndex = 0; sideIndex < 2; sideIndex++)
    {
        float side = sideIndex == 0 ? -1.0 : 1.0;
        float2 speaker = coord - float2(side * 1.36, 0.0);
        float box = (1.0 - smoothstep(0.18, 0.20, abs(speaker.x))) *
                    (1.0 - smoothstep(0.49, 0.51, abs(speaker.y)));
        float3 tint = reactionColor[0].rgb;
        if ((features & 1) != 0)
        {
        colour += float3(0.025, 0.025, 0.045) * box;
        alpha += box * 0.65;
        for (int cone = 0; cone < 2; cone++)
        {
            float radius = length(speaker - float2(0.0, cone == 0 ? -0.24 : 0.24));
            float rim = 0.108 + 0.062 * coneExcursion;
            float ring = exp(-pow((radius - rim) * 85.0, 2.0));
            float halo = exp(-pow((radius - rim) * 22.0, 2.0));
            float centre = exp(-radius * radius * (480.0 - 230.0 * coneExcursion));
            float brightness = (ring * 0.8 + halo * 0.18 + centre * 0.35) * pulse * (0.55 + 0.65 * conePump);
            colour += tint * brightness;
            alpha += brightness * 0.85;
        }
        }
        if ((features & 32) != 0)
        {
        for (int beam = 0; beam < 3; beam++)
        {
            float sweep = 0.38 + 0.23 * sin(t * (0.65 + beam * 0.12) + beam * 1.7 + sideIndex);
            float2 direction = normalize(float2(side * (sweep + beam * 0.17), -1.0));
            // Emit beyond the cabinet's outer edge (1.56), sweeping outward.
            // Keep even the beam halo clear of the speaker panel.
            float outsideSpeaker = smoothstep(1.58, 1.64, side * coord.x);
            float beamLight = BeamIntensity(coord, float2(side * 1.66, 0.46), direction) * pulse * outsideSpeaker;
            float3 beamColour = reactionColor[1].rgb;
            colour += beamColour * beamLight;
            alpha += beamLight * 0.58;
        }
        }
        if ((features & 4) != 0)
        {
            float strip = (1.0 - smoothstep(0.007, 0.016, abs(abs(speaker.x) - 0.178))) *
                          (1.0 - smoothstep(0.46, 0.49, abs(speaker.y)));
            float cell = floor((speaker.y + 0.48) / 0.08);
            float gap = smoothstep(0.05, 0.2, frac((speaker.y + 0.48) / 0.08));
            float travellingLight = 0.4 + 0.6 * pow(0.5 + 0.5 * sin(t * 4.0 - cell * 0.75), 2.0);
            float ledLight = strip * gap * travellingLight * pulse;
            float3 ledColour = 0.5 + 0.5 * cos(6.28318 * (cell * 0.085 + t * 0.08 + float3(0.0, 0.33, 0.66)));
            if (reactionColor[3].a < 0.5) ledColour = reactionColor[3].rgb;
            colour += ledColour * ledLight;
            alpha += ledLight * 0.9;
        }
        if ((features & 8) != 0)
        {
            for (int cone = 0; cone < 2; cone++)
            {
                float wave = frac(t * 0.65 + cone * 0.5);
                float ringRadius = 0.16 + wave * 0.46;
                float ringDistance = length(speaker - float2(0.0, cone == 0 ? -0.24 : 0.24));
                float expandingRing = exp(-pow((ringDistance - ringRadius) * 85.0, 2.0)) *
                    smoothstep(0.0, 0.08, wave) * (1.0 - wave) * pulse;
                colour += reactionColor[4].rgb * expandingRing;
                alpha += expandingRing * 0.45;
            }
        }
    }
    if ((features & 16) != 0)
    {
        for (int particle = 0; particle < 24; particle++)
        {
            float seed = frac(sin(particle * 127.1 + 31.7) * 43758.5453);
            float wave = frac(t * (0.08 + seed * 0.05) + particle * 0.618);
            float side = particle % 2 == 0 ? -1.0 : 1.0;
            float2 centre = float2(side * (1.1 + seed * 1.0 + 0.04 * sin(t + particle)),
                                  1.35 - wave * 2.7);
            float radius = 0.012 + level * 0.008;
            float sparkle = exp(-dot(coord - centre, coord - centre) / (radius * radius));
            float fade = smoothstep(0.0, 0.12, wave) * (1.0 - smoothstep(0.85, 1.0, wave));
            float twinkle = 0.45 + 0.55 * pow(0.5 + 0.5 * sin(t * 3.0 + particle), 2.0);
            float light = sparkle * fade * twinkle * pulse;
            float3 sparkleColour = reactionColor[5].rgb;
            colour += sparkleColour * light;
            alpha += light * 0.85;
        }
    }
    if ((features & 64) != 0)
    {
        for (int lamp = 0; lamp < 2; lamp++)
        {
            float side = lamp == 0 ? -1.0 : 1.0;
            float2 delta = coord - float2(side * 1.8, .68);
            float2 direction = normalize(float2(-side * (.45 + .35 * sin(t * .6 + lamp * 2)), -1));
            float along = dot(delta, direction);
            float across = abs(delta.x * direction.y - delta.y * direction.x);
            float width = .035 + max(0, along) * .18;
            float beam = exp(-pow(across / width, 2)) * smoothstep(0, .12, along) * (1 - smoothstep(1.6, 2.4, along));
            colour += reactionColor[6].rgb * beam * (.4 + level);
            alpha += beam * .22;
        }
    }
    if ((features & 256) != 0)
    {
        float2 ball = coord - float2(0, -1.12);
        float radius = length(ball);
        float sphere = 1 - smoothstep(.17, .18, radius);
        float2 panels = floor(ball * 50 + float2(t * 4, 0));
        float glint = pow(.5 + .5 * sin(panels.x * 1.8 + panels.y * 2.7 + t * 2), 8);
        float shade = sqrt(saturate(1 - radius * radius / .0324));
        colour += reactionColor[8].rgb * sphere * (.18 + shade * .32 + glint * .7);
        alpha += sphere * .85;
        float wire = exp(-pow(coord.x * 300, 2)) * step(coord.y, -1.28);
        colour += reactionColor[8].rgb * wire * .35; alpha += wire * .3;
        for (int fleck = 0; fleck < 16; fleck++)
        {
            float angle = fleck * 2.399 + t * .22;
            float2 pos = float2(cos(angle) * (1.3 + .3 * sin(fleck)), sin(angle) * 1.05);
            float d = length(coord - pos);
            float light = exp(-d * d * 9000) * (.4 + .6 * pow(.5 + .5 * sin(t * 2 + fleck), 6));
            colour += reactionColor[8].rgb * light; alpha += light * .8;
        }
    }
    if ((features & 1024) != 0)
    {
        float2 box = abs(coord) - float2(1.045, .635);
        float distance = length(max(box, 0)) + min(max(box.x, box.y), 0);
        float glow = (exp(-pow(distance * 35, 2)) * .25 + exp(-pow(distance * 220, 2))) * (.88 + .12 * sin(t * 2.2));
        colour += reactionColor[10].rgb * glow * (.6 + level); alpha += glow * .75;
    }
    if ((features & 2048) != 0)
    {
        for (int shape = 0; shape < 8; shape++)
        {
            float angle = shape * .785398 + t * .35;
            float2 centre = float2(cos(angle) * 1.9, sin(angle) * 1.12);
            float2 delta = coord - centre;
            float rotation = t * .7 + shape;
            float2 rotated = float2(delta.x * cos(rotation) - delta.y * sin(rotation), delta.x * sin(rotation) + delta.y * cos(rotation));
            float outline = exp(-pow((abs(rotated.x) + abs(rotated.y) - .07) * 180, 2));
            colour += reactionColor[11].rgb * outline * (.65 + level); alpha += outline * .7;
        }
    }
    if ((features & 8192) != 0)
    {
        float vertical = pow(.5 + .5 * sin(coord.x * 32 + .3 * sin(t * .5)), 5);
        float glow = vertical * (.35 + .65 * pow(.5 + .5 * sin(coord.y * 3 - t * .8 + coord.x * 2), 2));
        float fade = (1 - smoothstep(2.0, 2.5, abs(coord.x))) * (1 - smoothstep(1.25, 1.55, abs(coord.y)));
        colour += reactionColor[13].rgb * glow * fade * .55;
        alpha += glow * fade * .4;
    }
    alpha = saturate(alpha) * energy;
    if (alpha < 0.002) discard;
    return float4(saturate(colour), alpha);
}
""";
}
