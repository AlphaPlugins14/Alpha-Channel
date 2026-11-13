using System.Runtime.InteropServices;
using System.Text;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;

namespace AlphaChannel.Plugin;

// Temporary TV editing experiment. Remove this file and its two UiBuilder.Draw
// subscriptions in Plugin.cs to remove the feature.
internal sealed class TvGizmoOverlay
{
    private const int LeftMouseButton = 0x01;
    private const float AxisLength = 0.9f;
    private const float HitRadius = 14f;
    private const float RotationRadius = 70f;
    private static readonly int[] PictionaryTextVirtualKeys =
        Enumerable.Range(0x30, 10).Concat(Enumerable.Range(0x41, 26))
            .Concat(Enumerable.Range(0x60, 10))
            .Concat([0x08, 0x0D, 0x1B, 0x20, 0x6A, 0x6B, 0x6D, 0x6E, 0x6F,
                0xBA, 0xBB, 0xBC, 0xBD, 0xBE, 0xBF, 0xC0, 0xDB, 0xDC, 0xDD, 0xDE])
            .ToArray();

    private enum EditMode { Move, Rotate, Scale }
    private enum Axis { None, X, Y, Z }

    private readonly VideoEngine engine;
    private readonly Func<int> shortcutKey;
    private readonly Func<bool> editingBlocked;
    private readonly Func<bool> quizControlsInteractive;
    private readonly Func<Vector2, bool> quizControlHitTest;
    private readonly Action<Vector2?> setQuizControlHover;
    private readonly Action<Vector2> submitQuizControl;
    private readonly Func<bool> rouletteControlsInteractive;
    private readonly Func<Vector2, bool> rouletteControlHitTest;
    private readonly Action<Vector2?> setRouletteControlHover;
    private readonly Action<Vector2> submitRouletteControl;
    private readonly Action<Vector2?, bool, bool> updateRoulettePointer;
    private readonly Func<bool> pictionaryControlsInteractive;
    private readonly Func<Vector2, bool> pictionaryControlHitTest;
    private readonly Action<Vector2?> setPictionaryControlHover;
    private readonly Action<Vector2> submitPictionaryControl;
    private readonly Action<Vector2?, bool, bool> updatePictionaryDrawingPointer;
    private readonly Func<bool> pictionaryDrawingActive;
    private readonly Func<bool> pictionaryTextInputActive;
    private readonly Func<string> getPictionaryTextInput;
    private readonly Action<string> setPictionaryTextInput;
    private readonly Action<bool> finishPictionaryTextInput;
    private bool wasVisible;
    private bool wasMouseDown;
    private bool quizTvCaptureVisible;
    private bool pictionaryTextWasActive;
    private readonly HashSet<int> pictionaryTextKeysDown = [];
    private readonly Dictionary<int, long> pictionaryTextKeyRepeatAt = [];
    private bool dragging;
    private bool changedDuringDrag;
    private EditMode dragMode;
    private Axis dragAxis;
    private Vector2 dragStartMouse;
    private Vector2 dragAxisScreen;
    private Vector2 dragCenterScreen;
    private Vector3 dragStartPosition;
    private float dragStartYaw;
    private float dragStartScale;

    internal TvGizmoOverlay(
        VideoEngine engine,
        Func<int> shortcutKey,
        Func<bool> editingBlocked,
        Func<bool> quizControlsInteractive,
        Func<Vector2, bool> quizControlHitTest,
        Action<Vector2?> setQuizControlHover,
        Action<Vector2> submitQuizControl,
        Func<bool> rouletteControlsInteractive,
        Func<Vector2, bool> rouletteControlHitTest,
        Action<Vector2?> setRouletteControlHover,
        Action<Vector2> submitRouletteControl,
        Action<Vector2?, bool, bool> updateRoulettePointer,
        Func<bool> pictionaryControlsInteractive,
        Func<Vector2, bool> pictionaryControlHitTest,
        Action<Vector2?> setPictionaryControlHover,
        Action<Vector2> submitPictionaryControl,
        Action<Vector2?, bool, bool> updatePictionaryDrawingPointer,
        Func<bool> pictionaryDrawingActive,
        Func<bool> pictionaryTextInputActive,
        Func<string> getPictionaryTextInput,
        Action<string> setPictionaryTextInput,
        Action<bool> finishPictionaryTextInput)
    {
        this.engine = engine;
        this.shortcutKey = shortcutKey;
        this.editingBlocked = editingBlocked;
        this.quizControlsInteractive = quizControlsInteractive;
        this.quizControlHitTest = quizControlHitTest;
        this.setQuizControlHover = setQuizControlHover;
        this.submitQuizControl = submitQuizControl;
        this.rouletteControlsInteractive = rouletteControlsInteractive;
        this.rouletteControlHitTest = rouletteControlHitTest;
        this.setRouletteControlHover = setRouletteControlHover;
        this.submitRouletteControl = submitRouletteControl;
        this.updateRoulettePointer = updateRoulettePointer;
        this.pictionaryControlsInteractive = pictionaryControlsInteractive;
        this.pictionaryControlHitTest = pictionaryControlHitTest;
        this.setPictionaryControlHover = setPictionaryControlHover;
        this.submitPictionaryControl = submitPictionaryControl;
        this.updatePictionaryDrawingPointer = updatePictionaryDrawingPointer;
        this.pictionaryDrawingActive = pictionaryDrawingActive;
        this.pictionaryTextInputActive = pictionaryTextInputActive;
        this.getPictionaryTextInput = getPictionaryTextInput;
        this.setPictionaryTextInput = setPictionaryTextInput;
        this.finishPictionaryTextInput = finishPictionaryTextInput;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("user32.dll")]
    private static extern nint GetKeyboardLayout(uint threadId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ToUnicodeEx(uint virtualKey, uint scanCode, byte[] keyboardState,
        StringBuilder buffer, int bufferLength, uint flags, nint keyboardLayout);

    private static bool Held(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    private static unsafe bool TryGetMouse(out Vector2 mouse)
    {
        var viewport = ImGui.GetMainViewport();
        var handle = (nint)viewport.PlatformHandleRaw;
        if (handle == 0) handle = (nint)viewport.PlatformHandle;
        return GameWindowFocus.TryGetMouse(handle, out mouse);
    }

    private static Vector3 AxisVector(Axis axis) => axis switch
    {
        Axis.X => Vector3.UnitX,
        Axis.Y => Vector3.UnitY,
        Axis.Z => Vector3.UnitZ,
        _ => Vector3.Zero,
    };

    private static float DistanceToSegment(Vector2 point, Vector2 start, Vector2 end)
    {
        var segment = end - start;
        var lengthSquared = segment.LengthSquared();
        if (lengthSquared < 1f)
        {
            return float.MaxValue;
        }

        // The centre is shared by all three axes; require a point along the
        // handle so that clicking the centre does not pick an arbitrary axis.
        var fraction = Math.Clamp(
            Vector2.Dot(point - start, segment) / lengthSquared, 0f, 1f);
        if (fraction < 0.2f)
        {
            return float.MaxValue;
        }

        return Vector2.Distance(point, start + segment * fraction);
    }

    private void FinishDrag()
    {
        if (changedDuringDrag)
        {
            Plugin.Cfg.ScreenPosition = engine.ScreenPosition;
            Plugin.Cfg.ScreenYaw = engine.ScreenYaw;
            Plugin.Cfg.ScreenScale = engine.ScreenScale;
            Plugin.Cfg.ScreenWidthScale = engine.ScreenWidthScale;
            Plugin.Cfg.ScreenHeightScale = engine.ScreenHeightScale;
            Plugin.Cfg.Save();
        }

        dragging = false;
        changedDuringDrag = false;
        dragAxis = Axis.None;
    }

    internal Action? OnInteraction { get; set; }

    internal void Draw()
    {
        var mouseDown = Held(LeftMouseButton);
        if (!TryGetMouse(out _))
        {
            FinishDrag();
            wasVisible = false;
            wasMouseDown = mouseDown;
            quizTvCaptureVisible = false;
            updateRoulettePointer(null, false, false);
            updatePictionaryDrawingPointer(null, false, false);
            setQuizControlHover(null);
            setRouletteControlHover(null);
            setPictionaryControlHover(null);
            return;
        }
        DrawTvInteraction(mouseDown);
        DrawPictionaryTextInput();
        if (engine.HalloweenPlayback || !engine.IsActive || editingBlocked() || !Held(shortcutKey()))
        {
            FinishDrag();
            wasVisible = false;
            wasMouseDown = mouseDown;
            return;
        }

        if (!TryGetMouse(out var mouse) ||
            !Plugin.GameGui.WorldToScreen(engine.ScreenPosition, out var center))
        {
            FinishDrag();
            wasVisible = false;
            wasMouseDown = mouseDown;
            return;
        }

        // A draw-list alone cannot claim the mouse from FFXIV. Give the
        // Tab-held editing mode a real ImGui item so Dalamud captures clicks
        // and the game camera does not start rotating during a handle drag.
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos);
        ImGui.SetNextWindowSize(viewport.Size);
        if (!wasVisible)
        {
            ImGui.SetNextWindowFocus();
        }

        ImGui.Begin(
            "##AlphaChannelTvGizmoInput",
            ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoDocking);
        wasVisible = true;
        ImGui.SetCursorScreenPos(viewport.Pos);
        ImGui.InvisibleButton("##TvGizmoMouseCapture", viewport.Size);

        var drawList = ImGui.GetForegroundDrawList();
        var white = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f));
        var purple = ImGui.GetColorU32(new Vector4(0.62f, 0.39f, 1f, 1f));
        var scaleHandle = center + new Vector2(105f, 75f);
        var scaleHovered =
            !dragging && Vector2.Distance(mouse, scaleHandle) <= HitRadius;
        var (hoverAxis, axisSegment) = FindMoveHandle(center, mouse);
        if (dragging || scaleHovered)
        {
            hoverAxis = Axis.None;
        }

        var rotationHovered =
            !dragging &&
            !scaleHovered &&
            hoverAxis == Axis.None &&
            MathF.Abs(Vector2.Distance(mouse, center) - RotationRadius) <= HitRadius;

        drawList.AddCircle(
            center,
            RotationRadius,
            rotationHovered || dragging && dragMode == EditMode.Rotate
                ? white
                : purple,
            64,
            3f);

        drawList.AddLine(center, scaleHandle, purple, 2f);
        drawList.AddRectFilled(
            scaleHandle - new Vector2(9f),
            scaleHandle + new Vector2(9f),
            scaleHovered || dragging && dragMode == EditMode.Scale
                ? white
                : purple,
            2f);
        drawList.AddText(scaleHandle + new Vector2(14f, -8f), white, "Scale");

        DrawMoveHandles(drawList, center, hoverAxis);
        drawList.AddCircleFilled(center, 5f, white);

        if (mouseDown && !wasMouseDown && !dragging)
        {
            if (scaleHovered)
            {
                StartDrag(EditMode.Scale, Axis.None, mouse, center, default);
            }
            else if (hoverAxis != Axis.None)
            {
                StartDrag(EditMode.Move, hoverAxis, mouse, center, axisSegment);
            }
            else if (rotationHovered)
            {
                StartDrag(EditMode.Rotate, Axis.None, mouse, center, default);
            }
        }

        if (dragging && mouseDown)
        {
            UpdateDrag(mouse);
        }
        else if (dragging && !mouseDown)
        {
            FinishDrag();
        }

        wasMouseDown = mouseDown;
        ImGui.End();
    }

    private void DrawTvInteraction(bool mouseDown)
    {
        var ownedMouseCapture = quizTvCaptureVisible;
        quizTvCaptureVisible = false;
        var useQuizControls = quizControlsInteractive();
        var useRouletteControls = !useQuizControls && rouletteControlsInteractive();
        var usePictionaryControls = !useQuizControls && !useRouletteControls && pictionaryControlsInteractive();
        var mousePressed = mouseDown && !wasMouseDown;
        if (!engine.IsActive || (!useQuizControls && !useRouletteControls && !usePictionaryControls) || Held(shortcutKey()) ||
            (ImGui.GetIO().WantCaptureMouse && !ownedMouseCapture) || !TryGetMouse(out var mouse))
        {
            updateRoulettePointer(null, mouseDown, mousePressed);
            if (usePictionaryControls)
                updatePictionaryDrawingPointer(null, mouseDown, mousePressed);
            setQuizControlHover(null);
            setRouletteControlHover(null);
            setPictionaryControlHover(null);
            return;
        }

        var hasUv = engine.TryScreenPointToTvUv(mouse, out var uv);
        if (useRouletteControls)
            updateRoulettePointer(hasUv ? uv : null, mouseDown, mousePressed);
        if (usePictionaryControls)
            updatePictionaryDrawingPointer(hasUv ? uv : null, mouseDown, mousePressed);
        var drawingCapture = usePictionaryControls && pictionaryDrawingActive();
        var hit = hasUv && (useQuizControls ? quizControlHitTest(uv) :
            useRouletteControls ? rouletteControlHitTest(uv) : pictionaryControlHitTest(uv));
        if (!hit && !drawingCapture)
        {
            setQuizControlHover(null);
            setRouletteControlHover(null);
            setPictionaryControlHover(null);
            return;
        }

        if (mousePressed || drawingCapture && mouseDown) OnInteraction?.Invoke();
        quizTvCaptureVisible = true;
        if (useQuizControls && hit)
        {
            setQuizControlHover(uv);
            setRouletteControlHover(null);
            setPictionaryControlHover(null);
        }
        else if (useRouletteControls && hit)
        {
            setQuizControlHover(null);
            setRouletteControlHover(uv);
            setPictionaryControlHover(null);
        }
        else if (hit)
        {
            setQuizControlHover(null);
            setRouletteControlHover(null);
            setPictionaryControlHover(uv);
        }
        else
        {
            setQuizControlHover(null);
            setRouletteControlHover(null);
            setPictionaryControlHover(null);
        }

        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

        // Once a TV stroke begins, capture the full viewport until mouse-up so FFXIV cannot
        // rotate the camera and the stroke is safely completed if the pointer leaves the TV.
        var viewport = ImGui.GetMainViewport();
        var captureSize = drawingCapture ? viewport.Size : new Vector2(28f, 28f);
        var capturePosition = drawingCapture ? viewport.Pos : mouse - captureSize * 0.5f;
        ImGui.SetNextWindowPos(capturePosition);
        ImGui.SetNextWindowSize(captureSize);
        ImGui.Begin(
            "##AlphaChannelTvControlClick",
            ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoDocking);
        ImGui.SetCursorScreenPos(capturePosition);
        ImGui.InvisibleButton("##TvControlCapture", captureSize);
        ImGui.End();

        if (hit && mousePressed)
        {
            if (useQuizControls)
                submitQuizControl(uv);
            else if (useRouletteControls)
                submitRouletteControl(uv);
            else
                submitPictionaryControl(uv);
        }
    }

    private void DrawPictionaryTextInput()
    {
        var active = pictionaryTextInputActive();
        if (!active)
        {
            pictionaryTextWasActive = false;
            pictionaryTextKeysDown.Clear();
            pictionaryTextKeyRepeatAt.Clear();
            return;
        }

        if (!pictionaryTextWasActive)
        {
            // Ignore keys that were already held when the field opened. New physical key
            // presses are read from Windows because Dalamud's game-facing state is suppressed.
            foreach (var virtualKey in PictionaryTextVirtualKeys)
                if (Held(virtualKey))
                    pictionaryTextKeysDown.Add(virtualKey);
            pictionaryTextWasActive = true;
            return;
        }

        ImGui.SetNextFrameWantCaptureKeyboard(true);
        var now = Environment.TickCount64;
        foreach (var virtualKey in PictionaryTextVirtualKeys)
        {
            var down = Held(virtualKey);
            var shouldHandle = false;
            if (down && pictionaryTextKeysDown.Add(virtualKey))
            {
                shouldHandle = true;
                pictionaryTextKeyRepeatAt[virtualKey] = now + 450;
            }
            else if (down && pictionaryTextKeyRepeatAt.TryGetValue(virtualKey, out var repeatAt) && now >= repeatAt)
            {
                shouldHandle = true;
                pictionaryTextKeyRepeatAt[virtualKey] = now + 35;
            }
            else if (!down && pictionaryTextKeysDown.Remove(virtualKey))
            {
                pictionaryTextKeyRepeatAt.Remove(virtualKey);
            }

            if (shouldHandle && HandlePictionaryTextKey(virtualKey))
                break;
        }
    }

    private bool HandlePictionaryTextKey(int virtualKey)
    {
        if (virtualKey == 0x0D)
        {
            finishPictionaryTextInput(true);
            return true;
        }
        if (virtualKey == 0x1B)
        {
            finishPictionaryTextInput(false);
            return true;
        }

        var value = getPictionaryTextInput();
        if (virtualKey == 0x08)
        {
            if (value.Length > 0)
                setPictionaryTextInput(value[..^1]);
            return false;
        }

        // Ctrl and Alt combinations belong to shortcuts rather than answer text.
        if (Held(0x11) || Held(0x12))
            return false;

        var keyboardState = new byte[256];
        for (var key = 0; key < keyboardState.Length; key++)
            if (Held(key))
                keyboardState[key] = 0x80;
        if ((GetKeyState(0x14) & 1) != 0)
            keyboardState[0x14] |= 1;

        var text = new StringBuilder(4);
        var count = ToUnicodeEx((uint)virtualKey, MapVirtualKey((uint)virtualKey, 0), keyboardState,
            text, text.Capacity, 0, GetKeyboardLayout(0));
        if (count > 0)
        {
            var characters = new string(text.ToString().Take(count).Where(character => !char.IsControl(character)).ToArray());
            if (characters.Length > 0)
                setPictionaryTextInput(value + characters);
        }
        return false;
    }

    private (Axis Axis, Vector2 Segment) FindMoveHandle(
        Vector2 center,
        Vector2 mouse)
    {
        var bestAxis = Axis.None;
        var bestDistance = HitRadius;
        var bestSegment = Vector2.Zero;

        foreach (var axis in new[] { Axis.X, Axis.Y, Axis.Z })
        {
            if (!Plugin.GameGui.WorldToScreen(
                    engine.ScreenPosition + AxisVector(axis) * AxisLength,
                    out var end))
            {
                continue;
            }

            var segment = end - center;
            if (segment.LengthSquared() < 400f)
            {
                continue;
            }

            var distance = DistanceToSegment(mouse, center, end);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestAxis = axis;
                bestSegment = segment;
            }
        }

        return (bestAxis, bestSegment);
    }

    private void DrawMoveHandles(
        ImDrawListPtr drawList,
        Vector2 center,
        Axis hoveredAxis)
    {
        foreach (var axis in new[] { Axis.X, Axis.Y, Axis.Z })
        {
            if (!Plugin.GameGui.WorldToScreen(
                    engine.ScreenPosition + AxisVector(axis) * AxisLength,
                    out var end))
            {
                continue;
            }

            var segment = end - center;
            if (segment.LengthSquared() < 400f)
            {
                continue;
            }

            var colour = axis switch
            {
                Axis.X => new Vector4(1f, 0.32f, 0.36f, 1f),
                Axis.Y => new Vector4(0.34f, 0.92f, 0.54f, 1f),
                _ => new Vector4(0.35f, 0.62f, 1f, 1f),
            };
            var selected = dragging
                ? dragMode == EditMode.Move && dragAxis == axis
                : hoveredAxis == axis;
            var stroke = ImGui.GetColorU32(
                selected ? new Vector4(1f, 1f, 1f, 1f) : colour);
            drawList.AddLine(center, end, stroke, selected ? 5f : 3f);

            var direction = Vector2.Normalize(segment);
            var sideways = new Vector2(-direction.Y, direction.X);
            drawList.AddTriangleFilled(
                end,
                end - direction * 15f + sideways * 7f,
                end - direction * 15f - sideways * 7f,
                stroke);
        }

    }

    private void StartDrag(
        EditMode mode,
        Axis axis,
        Vector2 mouse,
        Vector2 center,
        Vector2 axisScreen)
    {
        OnInteraction?.Invoke();
        dragging = true;
        dragMode = mode;
        dragAxis = axis;
        dragStartMouse = mouse;
        dragCenterScreen = center;
        dragAxisScreen = axisScreen;
        dragStartPosition = engine.ScreenPosition;
        dragStartYaw = engine.ScreenYaw;
        dragStartScale = engine.ScreenScale;
    }

    private void UpdateDrag(Vector2 mouse)
    {
        var position = dragStartPosition;
        var yaw = dragStartYaw;
        var scale = dragStartScale;

        if (dragMode == EditMode.Move)
        {
            var screenLengthSquared = dragAxisScreen.LengthSquared();
            if (screenLengthSquared < 1f)
            {
                return;
            }

            var amount = Vector2.Dot(mouse - dragStartMouse, dragAxisScreen)
                         / screenLengthSquared * AxisLength;
            position += AxisVector(dragAxis) * Math.Clamp(amount, -25f, 25f);
        }
        else if (dragMode == EditMode.Rotate)
        {
            var from = dragStartMouse - dragCenterScreen;
            var to = mouse - dragCenterScreen;
            yaw += MathF.Atan2(to.Y, to.X) - MathF.Atan2(from.Y, from.X);
        }
        else
        {
            scale = Math.Clamp(
                dragStartScale * MathF.Exp((mouse.X - dragStartMouse.X) * 0.01f),
                VideoEngine.MinScreenScale,
                VideoEngine.MaxScreenScale);
        }

        engine.SetScreenTransform(position, yaw, scale, manualAdjustment: true);
        changedDuringDrag = true;
    }
}
