using Dalamud.Bindings.ImGui;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private static readonly string[] SlotSymbolNames =
    [
        "Moogle · face", "Moogle · winged",
        "Fat Cat · cream", "Fat Cat · calico",
        "Cactuar · classic", "Cactuar · golden",
        "Chocobo · yellow", "Chocobo · black",
        "Carbuncle · sapphire", "Carbuncle · emerald",
        "Aetheryte · blue", "Aetheryte · violet",
        "Gil · single", "Gil · jackpot",
        "Bomb · ember", "Bomb · blue flame",
        "Tonberry · lantern", "Tonberry · knife",
        "Mandragora · sprout", "Mandragora · crowned",
        "Namazu · blue", "Namazu · golden",
        "Paissa · bright", "Paissa · sleepy",
        "Gold Saucer · star", "Gold Saucer · wheel",
        "Triple Triad · card", "Triple Triad · rare",
        "Phoenix · feather", "Phoenix · flame",
        "Spriggan · crystal", "Spriggan · gold",
        "Morbol · green", "Morbol · violet",
        "Dodo · brown", "Dodo · blue",
        "Goobbue · mossy", "Goobbue · flower",
        "Korpokkur · sprout", "Korpokkur · blossom",
        "Tomestone · red", "Tomestone · purple",
        "Materia · fire", "Materia · ice",
        "Coffer · bronze", "Coffer · ornate",
        "Job Stone · paladin", "Job Stone · mage",
        "Glamour Prism · pink", "Glamour Prism · rainbow",
        "Behemoth · violet", "Behemoth · obsidian",
        "Coeurl · tawny", "Coeurl · silver",
        "Fat Chocobo · gold", "Fat Chocobo · white",
        "Apkallu · teal", "Apkallu · royal",
        "Chocobo Egg · blue", "Chocobo Egg · golden",
        "Aether Potion · blue", "Aether Potion · red",
        "Bard Harp · bronze", "Bard Harp · gilded",
        "Fisher's Catch · silver", "Fisher's Catch · coral",
        "Airship · brass", "Airship · sky blue",
        "Dalamud · red moon", "Dalamud · eclipsed",
    ];

    private static void DrawSlotSymbol(ImDrawListPtr draw, int index, Vector2 center, float r)
    {
        var variant = index % 2;
        var kind = index / 2;
        Vector2 P(float x, float y) => center + new Vector2(x * r, y * r);
        static uint Col(byte red, byte green, byte blue, byte alpha = 255) =>
            (uint)(alpha << 24 | blue << 16 | green << 8 | red);
        void Disk(float x, float y, float radius, uint color) => draw.AddCircleFilled(P(x, y), radius * r, color, 28);
        void Ring(float x, float y, float radius, uint color, float thickness = .055f) =>
            draw.AddCircle(P(x, y), radius * r, color, 32, Math.Max(1f, thickness * r));
        void Line(float x1, float y1, float x2, float y2, uint color, float thickness = .07f) =>
            draw.AddLine(P(x1, y1), P(x2, y2), color, Math.Max(1f, thickness * r));
        void Tri(float x1, float y1, float x2, float y2, float x3, float y3, uint color) =>
            draw.AddTriangleFilled(P(x1, y1), P(x2, y2), P(x3, y3), color);
        void Box(float x1, float y1, float x2, float y2, uint color, float rounding = .08f) =>
            draw.AddRectFilled(P(x1, y1), P(x2, y2), color, rounding * r);
        void Diamond(float x, float y, float w, float h, uint color)
        {
            Tri(x, y - h, x - w, y, x + w, y, color);
            Tri(x - w, y, x, y + h, x + w, y, color);
        }

        var ink = Col(29, 34, 57);
        var white = Col(248, 244, 236);
        var gold = Col(255, 203, 81);
        var red = Col(242, 94, 93);
        var aqua = Col(112, 225, 244);
        var green = Col(114, 213, 105);
        var violet = Col(192, 129, 239);
        var eye = Col(22, 32, 52);

        // Every icon is made from the same vector primitives used by the plugin UI,
        // so the gallery is a true sample of what the eventual reels can display.
        switch (kind)
        {
            case 0: // Moogle
                if (variant == 1)
                {
                    Tri(-.50f, .20f, -.98f, -.18f, -.80f, .48f, Col(119, 104, 154));
                    Tri(.50f, .20f, .98f, -.18f, .80f, .48f, Col(119, 104, 154));
                    Disk(0, .42f, .42f, white);
                }
                Line(0, -.52f, .10f, -.90f, ink, .07f);
                Disk(.11f, -.89f, .17f, gold);
                Tri(-.66f, -.36f, -.70f, -.82f, -.23f, -.53f, white);
                Tri(.66f, -.36f, .70f, -.82f, .23f, -.53f, white);
                Disk(0, -.10f, .63f, white);
                Disk(-.23f, -.13f, .055f, eye);
                Disk(.23f, -.13f, .055f, eye);
                Disk(-.34f, .09f, .09f, Col(248, 180, 190));
                Disk(.34f, .09f, .09f, Col(248, 180, 190));
                Disk(0, .14f, .08f, Col(240, 160, 169));
                break;
            case 1: // Fat Cat
                var fur = variant == 0 ? Col(242, 233, 215) : Col(235, 225, 210);
                Disk(0, .38f, .54f, fur);
                Disk(-.40f, .68f, .18f, fur);
                Disk(.40f, .68f, .18f, fur);
                Tri(-.64f, -.27f, -.74f, -.82f, -.25f, -.51f, fur);
                Tri(.64f, -.27f, .74f, -.82f, .25f, -.51f, fur);
                Disk(0, -.12f, .61f, fur);
                if (variant == 1)
                {
                    Disk(-.39f, -.39f, .20f, Col(221, 145, 98));
                    Disk(.35f, -.34f, .19f, Col(112, 125, 138));
                }
                Line(-.32f, -.17f, -.16f, -.12f, ink, .052f);
                Line(.16f, -.12f, .32f, -.17f, ink, .052f);
                Disk(0, .04f, .065f, Col(232, 145, 150));
                Line(-.05f, .12f, 0, .17f, ink, .03f);
                Line(0, .17f, .05f, .12f, ink, .03f);
                break;
            case 2: // Cactuar
                var cactus = variant == 0 ? Col(94, 211, 81) : Col(216, 200, 72);
                Box(-.29f, -.63f, .29f, .51f, cactus, .24f);
                Line(-.25f, -.08f, -.69f, .21f, cactus, .24f);
                Line(-.69f, .21f, -.69f, -.05f, cactus, .22f);
                Line(.25f, -.05f, .70f, -.25f, cactus, .24f);
                Line(.70f, -.25f, .70f, -.49f, cactus, .22f);
                Line(-.21f, .39f, -.45f, .95f, cactus, .23f);
                Line(.21f, .39f, .45f, .95f, cactus, .23f);
                Disk(-.13f, -.22f, .055f, ink);
                Disk(.13f, -.22f, .055f, ink);
                Box(-.065f, -.06f, .065f, .16f, ink, .05f);
                if (variant == 1)
                    Diamond(0, -.83f, .18f, .15f, gold);
                break;
            case 3: // Chocobo
                var feather = variant == 0 ? gold : Col(79, 91, 116);
                Tri(-.42f, -.48f, -.60f, -.96f, -.05f, -.62f, feather);
                Tri(-.08f, -.55f, -.06f, -.99f, .28f, -.64f, feather);
                Tri(.22f, -.47f, .52f, -.81f, .42f, -.28f, feather);
                Disk(-.04f, .11f, .59f, feather);
                Tri(.34f, .08f, .97f, .32f, .31f, .43f, Col(244, 158, 70));
                Disk(.18f, -.08f, .10f, white);
                Disk(.20f, -.08f, .05f, ink);
                if (variant == 1)
                    Disk(-.37f, .34f, .13f, Col(150, 162, 188));
                break;
            case 4: // Carbuncle
                var carb = variant == 0 ? aqua : Col(95, 215, 164);
                Tri(-.32f, -.30f, -.90f, -.89f, -.69f, .14f, carb);
                Tri(.32f, -.30f, .90f, -.89f, .69f, .14f, carb);
                Tri(-.34f, -.42f, -.72f, -.71f, -.65f, -.04f, white);
                Tri(.34f, -.42f, .72f, -.71f, .65f, -.04f, white);
                Disk(0, .17f, .57f, carb);
                Tri(-.43f, .36f, -.82f, .50f, -.27f, .66f, carb);
                Tri(.43f, .36f, .82f, .50f, .27f, .66f, carb);
                Diamond(0, -.19f, .14f, .21f, variant == 0 ? Col(42, 128, 244) : Col(56, 170, 87));
                Disk(-.23f, .17f, .07f, eye);
                Disk(.23f, .17f, .07f, eye);
                Disk(0, .39f, .065f, white);
                break;
            case 5: // Aetheryte
                var crystal = variant == 0 ? Col(88, 207, 255) : Col(175, 134, 255);
                Diamond(0, 0, .47f, .84f, crystal);
                Tri(0, -.84f, -.47f, 0, -.04f, .19f, Col(211, 247, 255));
                Tri(0, -.84f, .47f, 0, -.04f, .19f, variant == 0 ? Col(53, 137, 235) : Col(107, 86, 219));
                Tri(-.47f, 0, 0, .84f, -.04f, .19f, Col(91, 156, 225));
                Line(-.70f, .02f, -.56f, .02f, crystal, .07f);
                Line(.56f, .02f, .70f, .02f, crystal, .07f);
                if (variant == 1)
                {
                    Diamond(-.55f, .44f, .14f, .28f, aqua);
                    Diamond(.56f, -.36f, .12f, .23f, gold);
                }
                break;
            case 6: // Gil
                if (variant == 1)
                {
                    Disk(-.31f, .29f, .46f, Col(178, 105, 40));
                    Ring(-.31f, .29f, .38f, gold);
                    Disk(.25f, .27f, .46f, Col(178, 105, 40));
                    Ring(.25f, .27f, .38f, gold);
                }
                Disk(0, -.12f, .64f, Col(199, 126, 43));
                Disk(0, -.12f, .54f, gold);
                Ring(0, -.12f, .44f, Col(255, 237, 153), .05f);
                Diamond(0, -.12f, .21f, .30f, Col(255, 247, 188));
                break;
            case 7: // Bomb
                var bomb = variant == 0 ? Col(228, 74, 66) : Col(86, 143, 237);
                Disk(0, .15f, .62f, bomb);
                Tri(-.23f, -.37f, -.56f, -.75f, -.02f, -.50f, bomb);
                Tri(.23f, -.37f, .56f, -.75f, .02f, -.50f, bomb);
                Line(0, -.46f, .09f, -.80f, Col(69, 54, 71), .10f);
                Tri(.09f, -.98f, -.04f, -.73f, .28f, -.79f, variant == 0 ? gold : aqua);
                Disk(-.23f, .10f, .07f, ink);
                Disk(.23f, .10f, .07f, ink);
                Line(-.22f, .40f, .22f, .40f, ink, .06f);
                Tri(-.10f, .40f, 0, .55f, .09f, .40f, white);
                break;
            case 8: // Tonberry
                var hood = variant == 0 ? Col(71, 153, 109) : Col(83, 184, 137);
                Tri(0, -.85f, -.67f, .71f, .67f, .71f, hood);
                Disk(0, -.43f, .48f, Col(126, 188, 107));
                Disk(-.17f, -.44f, .06f, gold);
                Disk(.17f, -.44f, .06f, gold);
                Box(-.55f, .65f, .55f, .80f, Col(54, 101, 75), .06f);
                if (variant == 0)
                {
                    Line(.48f, .28f, .76f, .15f, Col(117, 90, 60), .07f);
                    Box(.64f, .05f, .93f, .43f, gold, .03f);
                    Box(.69f, .10f, .88f, .37f, Col(255, 244, 165), .02f);
                }
                else
                {
                    Line(.47f, .40f, .74f, .18f, Col(111, 74, 51), .07f);
                    Tri(.69f, .21f, .98f, -.12f, .76f, .28f, white);
                }
                break;
            case 9: // Mandragora
                var leaf = variant == 0 ? green : Col(167, 219, 80);
                Tri(0, -.42f, -.55f, -.94f, -.14f, -.89f, leaf);
                Tri(0, -.42f, .55f, -.94f, .14f, -.89f, leaf);
                Tri(0, -.45f, -.13f, -1f, .13f, -1f, Col(94, 180, 74));
                Disk(0, .25f, .53f, white);
                Disk(-.20f, .16f, .06f, ink);
                Disk(.20f, .16f, .06f, ink);
                Disk(0, .40f, .05f, Col(230, 141, 131));
                if (variant == 1)
                {
                    Line(-.30f, -.56f, -.30f, -.79f, gold, .05f);
                    Line(.30f, -.56f, .30f, -.79f, gold, .05f);
                    Diamond(0, -.74f, .13f, .15f, gold);
                }
                break;
            case 10: // Namazu
                Disk(0, .06f, .63f, variant == 0 ? Col(227, 232, 218) : Col(244, 215, 142));
                Box(-.55f, -.48f, .55f, -.25f, variant == 0 ? Col(68, 139, 219) : red, .03f);
                Disk(-.21f, -.06f, .06f, ink);
                Disk(.21f, -.06f, .06f, ink);
                Line(-.35f, .22f, -.88f, .12f, ink, .035f);
                Line(-.35f, .31f, -.86f, .50f, ink, .035f);
                Line(.35f, .22f, .88f, .12f, ink, .035f);
                Line(.35f, .31f, .86f, .50f, ink, .035f);
                Line(-.10f, .29f, .10f, .29f, ink, .04f);
                break;
            case 11: // Paissa
                var paissa = variant == 0 ? Col(206, 221, 115) : Col(180, 197, 140);
                Tri(-.44f, .15f, -.78f, -.05f, -.59f, .45f, paissa);
                Tri(.44f, .15f, .78f, -.05f, .59f, .45f, paissa);
                Disk(0, .14f, .62f, paissa);
                Tri(-.40f, -.38f, -.60f, -.90f, -.13f, -.55f, paissa);
                Tri(.40f, -.38f, .60f, -.90f, .13f, -.55f, paissa);
                var lid = variant == 0 ? -.22f : -.08f;
                Disk(-.25f, lid, variant == 0 ? .14f : .09f, white);
                Disk(.25f, lid, variant == 0 ? .14f : .09f, white);
                Disk(-.25f, lid, .045f, ink);
                Disk(.25f, lid, .045f, ink);
                Tri(-.09f, .27f, .09f, .27f, 0, .44f, Col(224, 157, 79));
                break;
            case 12: // Gold Saucer
                if (variant == 0)
                {
                    Ring(0, 0, .70f, gold, .13f);
                    for (var ray = 0; ray < 8; ray++)
                    {
                        var angle = ray * MathF.PI / 4f;
                        Line(MathF.Cos(angle) * .72f, MathF.Sin(angle) * .72f,
                            MathF.Cos(angle) * .89f, MathF.Sin(angle) * .89f, gold, .08f);
                    }
                    Diamond(0, 0, .33f, .47f, Col(255, 245, 171));
                }
                else
                {
                    Ring(0, 0, .70f, Col(206, 148, 53), .15f);
                    Ring(0, 0, .48f, gold, .07f);
                    for (var ray = 0; ray < 6; ray++)
                    {
                        var angle = ray * MathF.PI / 3f;
                        Line(0, 0, MathF.Cos(angle) * .67f, MathF.Sin(angle) * .67f, gold, .06f);
                    }
                    Disk(0, 0, .15f, Col(255, 242, 170));
                }
                break;
            case 13: // Triple Triad
                Box(-.52f, -.72f, .52f, .72f, variant == 0 ? Col(82, 78, 153) : Col(144, 68, 146), .08f);
                draw.AddRect(P(-.43f, -.63f), P(.43f, .63f), gold, .06f * r, ImDrawFlags.None, .05f * r);
                Diamond(0, -.13f, .29f, .36f, variant == 0 ? aqua : gold);
                for (var star = -1; star <= 1; star++)
                    Disk(star * .19f, .43f, .07f, gold);
                break;
            case 14: // Phoenix
                if (variant == 0)
                {
                    Tri(-.06f, -.85f, -.35f, .75f, .42f, .17f, Col(251, 108, 59));
                    Tri(.15f, -.67f, -.09f, .58f, .56f, .00f, gold);
                    Line(-.27f, .81f, .32f, -.57f, Col(255, 231, 156), .055f);
                    Tri(-.38f, .35f, -.64f, -.07f, -.13f, .21f, red);
                }
                else
                {
                    Tri(0, -.95f, -.65f, .30f, -.19f, .76f, red);
                    Tri(0, -.70f, .65f, .30f, .19f, .76f, Col(247, 131, 57));
                    Tri(0, -.53f, -.28f, .40f, .28f, .40f, gold);
                    Disk(0, .42f, .17f, Col(255, 243, 169));
                }
                break;
            case 15: // Spriggan
                var spriggan = variant == 0 ? Col(72, 70, 97) : Col(104, 85, 112);
                for (var tuft = 0; tuft < 9; tuft++)
                {
                    var angle = tuft * MathF.Tau / 9f;
                    Disk(MathF.Cos(angle) * .45f, MathF.Sin(angle) * .43f, .25f, spriggan);
                }
                Disk(0, .05f, .55f, spriggan);
                Disk(-.19f, -.05f, .08f, gold);
                Disk(.19f, -.05f, .08f, gold);
                Diamond(0, .45f, .26f, .31f, variant == 0 ? aqua : gold);
                Line(-.48f, .32f, -.18f, .46f, spriggan, .16f);
                Line(.48f, .32f, .18f, .46f, spriggan, .16f);
                break;
            case 16: // Morbol
                var morbol = variant == 0 ? Col(100, 173, 72) : Col(145, 104, 174);
                Line(-.30f, .29f, -.76f, .65f, morbol, .18f);
                Line(-.11f, .37f, -.27f, .84f, morbol, .15f);
                Line(.11f, .37f, .27f, .84f, morbol, .15f);
                Line(.30f, .29f, .76f, .65f, morbol, .18f);
                Disk(0, -.04f, .65f, morbol);
                Tri(-.34f, -.42f, -.59f, -.73f, -.12f, -.55f, Col(70, 137, 68));
                Tri(.34f, -.42f, .59f, -.73f, .12f, -.55f, Col(70, 137, 68));
                Disk(-.27f, -.30f, .17f, morbol);
                Disk(.27f, -.30f, .17f, morbol);
                Disk(-.27f, -.20f, .13f, Col(242, 224, 148));
                Disk(.27f, -.20f, .13f, Col(242, 224, 148));
                Disk(-.27f, -.20f, .045f, ink);
                Disk(.27f, -.20f, .045f, ink);
                Box(-.32f, .14f, .32f, .43f, Col(75, 47, 70), .12f);
                for (var tooth = -1; tooth <= 1; tooth++)
                    Tri(tooth * .20f - .07f, .15f, tooth * .20f + .07f, .15f, tooth * .20f, .30f, white);
                break;
            case 17: // Dodo
                var dodo = variant == 0 ? Col(171, 125, 91) : Col(96, 144, 187);
                Disk(-.20f, .23f, .51f, dodo);
                Disk(.13f, -.30f, .38f, dodo);
                Tri(-.49f, .01f, -.92f, -.29f, -.66f, .31f, dodo);
                Tri(-.55f, .27f, -.95f, .08f, -.69f, .51f, dodo);
                Tri(.35f, -.34f, .91f, -.16f, .32f, -.07f, gold);
                Tri(.32f, -.07f, .87f, -.11f, .44f, .20f, Col(224, 139, 82));
                Disk(.23f, -.42f, .075f, white);
                Disk(.24f, -.42f, .035f, ink);
                Line(-.29f, .63f, -.34f, .86f, Col(218, 153, 92), .09f);
                Line(.04f, .65f, .08f, .86f, Col(218, 153, 92), .09f);
                break;
            case 18: // Goobbue
                var goobbue = variant == 0 ? Col(138, 159, 118) : Col(166, 152, 128);
                Disk(0, .13f, .67f, goobbue);
                Disk(-.50f, .56f, .21f, goobbue);
                Disk(.50f, .56f, .21f, goobbue);
                Box(-.58f, -.45f, .58f, -.24f, Col(69, 123, 76), .08f);
                for (var sprout = -1; sprout <= 1; sprout++)
                    Tri(sprout * .30f, -.36f, sprout * .30f - .14f, -.77f, sprout * .30f + .15f, -.39f, green);
                Disk(-.29f, -.02f, .07f, ink);
                Disk(.29f, -.02f, .07f, ink);
                Box(-.40f, .28f, .40f, .47f, Col(87, 78, 73), .09f);
                if (variant == 1)
                {
                    Disk(.22f, -.69f, .13f, Col(245, 135, 179));
                    Disk(.22f, -.69f, .055f, gold);
                }
                break;
            case 19: // Korpokkur
                var korpokkur = variant == 0 ? Col(122, 187, 91) : Col(148, 202, 110);
                Disk(0, .28f, .54f, korpokkur);
                Line(0, -.26f, 0, -.64f, Col(77, 152, 73), .11f);
                Tri(0, -.55f, -.78f, -.88f, -.19f, -.39f, Col(104, 185, 91));
                Tri(0, -.55f, .78f, -.88f, .19f, -.39f, Col(104, 185, 91));
                Disk(-.21f, .24f, .06f, ink);
                Disk(.21f, .24f, .06f, ink);
                Tri(-.08f, .38f, .08f, .38f, 0, .49f, gold);
                if (variant == 1)
                {
                    Disk(0, -.72f, .16f, Col(242, 160, 202));
                    Disk(0, -.72f, .07f, gold);
                }
                break;
            case 20: // Allagan tomestone
                var stone = variant == 0 ? Col(190, 72, 75) : Col(128, 90, 176);
                Box(-.46f, -.75f, .46f, .75f, Col(75, 76, 100), .08f);
                Box(-.35f, -.64f, .35f, .64f, stone, .05f);
                Line(-.23f, -.43f, .23f, -.43f, gold, .045f);
                Ring(0, .03f, .23f, gold, .06f);
                Diamond(0, .03f, .12f, .18f, variant == 0 ? red : violet);
                Line(-.20f, .43f, .20f, .43f, gold, .045f);
                break;
            case 21: // Materia
                var materia = variant == 0 ? Col(252, 117, 74) : Col(105, 210, 254);
                Disk(0, 0, .68f, variant == 0 ? Col(129, 54, 75) : Col(57, 99, 153));
                Disk(0, 0, .57f, materia);
                Ring(0, 0, .44f, Col(255, 242, 204), .045f);
                Diamond(0, 0, .23f, .34f, white);
                Disk(-.27f, -.27f, .10f, Col(255, 255, 255, 180));
                break;
            case 22: // Treasure coffer
                var chest = variant == 0 ? Col(139, 81, 52) : Col(94, 74, 130);
                Box(-.69f, -.22f, .69f, .63f, chest, .10f);
                Box(-.75f, -.43f, .75f, -.04f, chest, .15f);
                Line(-.71f, -.04f, .71f, -.04f, gold, .08f);
                Line(-.49f, -.43f, -.49f, .60f, gold, .08f);
                Line(.49f, -.43f, .49f, .60f, gold, .08f);
                Box(-.13f, -.11f, .13f, .27f, gold, .04f);
                Disk(0, .07f, .05f, ink);
                if (variant == 1)
                {
                    Diamond(0, -.67f, .15f, .17f, violet);
                    Disk(-.59f, -.57f, .06f, gold);
                    Disk(.59f, -.57f, .06f, gold);
                }
                break;
            case 23: // Job stones
                var job = variant == 0 ? Col(80, 154, 221) : Col(174, 106, 211);
                Tri(-.58f, -.72f, .58f, -.72f, 0, .88f, Col(207, 183, 111));
                Tri(-.45f, -.58f, .45f, -.58f, 0, .66f, job);
                if (variant == 0)
                {
                    Line(0, -.40f, 0, .43f, white, .11f);
                    Line(-.28f, -.12f, .28f, -.12f, white, .11f);
                    Disk(0, -.43f, .09f, gold);
                }
                else
                {
                    Tri(-.32f, .11f, .30f, .11f, -.03f, -.43f, ink);
                    Box(-.34f, .06f, .34f, .17f, ink, .02f);
                    Line(.04f, .19f, .04f, .43f, white, .05f);
                    Diamond(-.24f, -.38f, .06f, .09f, gold);
                    Diamond(.27f, -.28f, .06f, .09f, gold);
                }
                break;
            case 24: // Glamour prism
                var prism = variant == 0 ? Col(243, 131, 205) : Col(136, 218, 252);
                Diamond(0, 0, .49f, .76f, prism);
                Tri(0, -.76f, -.49f, 0, 0, .18f, white);
                Tri(0, -.76f, .49f, 0, 0, .18f, variant == 0 ? Col(191, 97, 222) : gold);
                Tri(-.49f, 0, 0, .76f, 0, .18f, variant == 0 ? Col(217, 76, 161) : Col(83, 189, 207));
                if (variant == 1)
                {
                    Tri(0, .18f, .49f, 0, 0, .76f, Col(118, 211, 121));
                    Tri(0, -.76f, .49f, 0, 0, -.13f, Col(239, 132, 156));
                }
                for (var sparkle = -1; sparkle <= 1; sparkle++)
                    Diamond(sparkle * .66f, sparkle == 0 ? -.76f : .42f, .07f, .12f, sparkle == 0 ? gold : white);
                break;
            case 25: // Behemoth
                var behemoth = variant == 0 ? Col(127, 90, 178) : Col(79, 79, 103);
                Tri(-.32f, -.34f, -.78f, -.91f, -.58f, .06f, Col(217, 195, 164));
                Tri(.32f, -.34f, .78f, -.91f, .58f, .06f, Col(217, 195, 164));
                Disk(0, .10f, .62f, behemoth);
                Tri(-.54f, .42f, -.81f, .71f, -.18f, .62f, behemoth);
                Tri(.54f, .42f, .81f, .71f, .18f, .62f, behemoth);
                Disk(-.22f, .00f, .07f, variant == 0 ? gold : red);
                Disk(.22f, .00f, .07f, variant == 0 ? gold : red);
                Tri(-.15f, .42f, .15f, .42f, 0, .67f, Col(184, 133, 159));
                Line(-.46f, .30f, -.83f, .28f, white, .035f);
                Line(.46f, .30f, .83f, .28f, white, .035f);
                break;
            case 26: // Coeurl
                var coeurl = variant == 0 ? Col(234, 176, 80) : Col(196, 205, 218);
                Tri(-.53f, -.38f, -.69f, -.82f, -.20f, -.55f, coeurl);
                Tri(.53f, -.38f, .69f, -.82f, .20f, -.55f, coeurl);
                Disk(0, .08f, .62f, coeurl);
                for (var spot = -1; spot <= 1; spot++)
                    Disk(spot * .21f, -.38f, .055f, Col(106, 85, 71));
                Disk(-.22f, .05f, .065f, ink);
                Disk(.22f, .05f, .065f, ink);
                Tri(-.10f, .27f, .10f, .27f, 0, .39f, Col(221, 109, 114));
                Line(-.28f, .32f, -.85f, .20f, Col(236, 222, 189), .045f);
                Line(-.28f, .41f, -.85f, .52f, Col(236, 222, 189), .045f);
                Line(.28f, .32f, .85f, .20f, Col(236, 222, 189), .045f);
                Line(.28f, .41f, .85f, .52f, Col(236, 222, 189), .045f);
                break;
            case 27: // Fat Chocobo
                var fatChocobo = variant == 0 ? Col(247, 201, 91) : white;
                Disk(0, .29f, .61f, fatChocobo);
                Disk(0, -.36f, .38f, fatChocobo);
                Tri(-.25f, -.59f, -.47f, -.91f, .02f, -.66f, fatChocobo);
                Tri(.02f, -.62f, .06f, -.97f, .31f, -.64f, fatChocobo);
                Disk(-.18f, -.40f, .055f, ink);
                Disk(.18f, -.40f, .055f, ink);
                Tri(-.18f, -.20f, .18f, -.20f, 0, .02f, Col(241, 144, 69));
                Tri(-.34f, .24f, -.76f, .04f, -.53f, .53f, fatChocobo);
                Tri(.34f, .24f, .76f, .04f, .53f, .53f, fatChocobo);
                Line(-.25f, .78f, -.25f, .93f, Col(220, 150, 74), .075f);
                Line(.25f, .78f, .25f, .93f, Col(220, 150, 74), .075f);
                break;
            case 28: // Apkallu
                var apkallu = variant == 0 ? Col(75, 178, 186) : Col(98, 111, 198);
                Disk(0, .05f, .58f, apkallu);
                Disk(0, .30f, .40f, white);
                Tri(-.40f, .10f, -.84f, .44f, -.48f, .42f, apkallu);
                Tri(.40f, .10f, .84f, .44f, .48f, .42f, apkallu);
                Disk(-.20f, -.12f, .075f, white);
                Disk(.20f, -.12f, .075f, white);
                Disk(-.20f, -.12f, .035f, ink);
                Disk(.20f, -.12f, .035f, ink);
                Tri(-.12f, .09f, .12f, .09f, 0, .27f, gold);
                Line(-.30f, .69f, -.45f, .82f, Col(234, 162, 79), .09f);
                Line(.30f, .69f, .45f, .82f, Col(234, 162, 79), .09f);
                break;
            case 29: // Chocobo egg
                var egg = variant == 0 ? Col(150, 218, 242) : Col(255, 221, 138);
                Disk(0, .23f, .51f, egg);
                Disk(0, -.21f, .38f, egg);
                Box(-.38f, -.22f, .38f, .29f, egg, .12f);
                for (var mark = -1; mark <= 1; mark++)
                    Diamond(mark * .29f, .08f, .09f, .14f, variant == 0 ? Col(61, 143, 207) : Col(241, 137, 79));
                if (variant == 1)
                {
                    Line(-.45f, .58f, .45f, .58f, Col(199, 139, 69), .065f);
                    Diamond(0, -.38f, .10f, .15f, white);
                }
                break;
            case 30: // Aether potion
                var potion = variant == 0 ? aqua : red;
                Box(-.24f, -.78f, .24f, -.52f, Col(183, 154, 105), .04f);
                Box(-.37f, -.55f, .37f, .72f, Col(144, 177, 196), .13f);
                Box(-.28f, -.12f, .28f, .58f, potion, .09f);
                Disk(0, .19f, .18f, variant == 0 ? Col(74, 145, 224) : Col(250, 178, 103));
                Diamond(0, .19f, .09f, .13f, white);
                Line(-.28f, -.34f, .28f, -.34f, white, .06f);
                break;
            case 31: // Bard harp
                var harp = variant == 0 ? Col(190, 125, 66) : gold;
                Line(-.42f, -.80f, -.42f, .72f, harp, .14f);
                Line(-.42f, -.80f, .54f, -.54f, harp, .12f);
                Line(.54f, -.54f, .31f, .68f, harp, .13f);
                Line(-.42f, .72f, .31f, .68f, harp, .13f);
                for (var harpString = 0; harpString < 4; harpString++)
                {
                    var x = -.27f + harpString * .16f;
                    Line(x, -.62f + harpString * .05f, x, .60f, Col(242, 227, 177), .025f);
                }
                Disk(-.43f, -.78f, .10f, gold);
                if (variant == 1)
                    Diamond(.51f, -.56f, .13f, .16f, violet);
                break;
            case 32: // Fisher's catch
                var fish = variant == 0 ? Col(153, 202, 222) : Col(250, 147, 125);
                Disk(-.10f, .04f, .48f, fish);
                Tri(-.39f, -.12f, -.88f, -.52f, -.82f, .49f, fish);
                Tri(-.10f, -.37f, .31f, -.77f, .29f, -.15f, fish);
                Tri(.17f, .35f, .59f, .60f, .16f, .53f, fish);
                Disk(.17f, -.05f, .065f, ink);
                Line(.46f, .15f, .67f, .22f, white, .045f);
                if (variant == 1)
                {
                    Disk(-.20f, -.02f, .06f, Col(255, 220, 170));
                    Disk(-.35f, .16f, .06f, Col(255, 220, 170));
                }
                break;
            case 33: // Wind-up airship
                var ship = variant == 0 ? Col(176, 125, 78) : Col(95, 177, 215);
                Box(-.62f, -.08f, .62f, .25f, ship, .12f);
                Tri(-.62f, .00f, -.88f, .18f, -.49f, .24f, ship);
                Tri(.62f, .00f, .89f, .18f, .49f, .24f, ship);
                Tri(-.17f, -.11f, 0, -.70f, .17f, -.11f, ship);
                Tri(-.45f, .18f, -.62f, .60f, -.20f, .22f, ship);
                Tri(.45f, .18f, .62f, .60f, .20f, .22f, ship);
                Disk(0, .05f, .13f, gold);
                Line(.02f, -.70f, .02f, -.86f, white, .05f);
                break;
            case 34: // Dalamud
                var moon = variant == 0 ? Col(208, 63, 83) : Col(139, 91, 148);
                Disk(0, 0, .65f, moon);
                Ring(0, 0, .49f, variant == 0 ? Col(250, 151, 116) : Col(215, 163, 226), .065f);
                Diamond(0, 0, .25f, .34f, variant == 0 ? Col(255, 208, 150) : Col(75, 66, 104));
                for (var ray = 0; ray < 6; ray++)
                {
                    var angle = ray * MathF.Tau / 6f;
                    var near = variant == 0 ? .67f : .76f;
                    Line(MathF.Cos(angle) * near, MathF.Sin(angle) * near,
                        MathF.Cos(angle) * .88f, MathF.Sin(angle) * .88f,
                        variant == 0 ? red : violet, .06f);
                }
                break;
        }
    }
}
