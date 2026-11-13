using Dalamud.Bindings.ImGui;

namespace AlphaChannel.Plugin;

// Semantic availability is separate from the outer input mask used when the
// game is unfocused. ImGui normally fades only the first disabled scope, so a
// nested unavailable control otherwise loses its fade under that input mask.
internal static class UiDisabled
{
    [ThreadStatic] private static bool inputMasked;
    [ThreadStatic] private static float maskedDisabledAlpha;
    [ThreadStatic] private static int disabledDepth;
    [ThreadStatic] private static Stack<IDisposable>? manualScopes;

    internal static IDisposable Scope(bool disabled = true) => new DisabledScope(disabled);
    internal static IDisposable InputMask() => new InputMaskScope();
    internal static void Begin() => (manualScopes ??= new()).Push(Scope());
    internal static void End() => manualScopes!.Pop().Dispose();

    private sealed class DisabledScope : IDisposable
    {
        private readonly bool disabled, manualAlpha;
        private bool disposed;

        internal DisabledScope(bool disabled)
        {
            this.disabled = disabled;
            manualAlpha = disabled && inputMasked && disabledDepth == 0;
            if (manualAlpha) ImGui.PushStyleVar(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * maskedDisabledAlpha);
            if (disabled) disabledDepth++;
            ImGui.BeginDisabled(disabled);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            ImGui.EndDisabled();
            if (disabled) disabledDepth--;
            if (manualAlpha) ImGui.PopStyleVar();
        }
    }

    private sealed class InputMaskScope : IDisposable
    {
        private readonly bool previousMasked = inputMasked;
        private readonly float previousAlpha = maskedDisabledAlpha;
        private bool disposed;

        internal InputMaskScope()
        {
            if (!inputMasked) maskedDisabledAlpha = ImGui.GetStyle().DisabledAlpha;
            inputMasked = true;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            inputMasked = previousMasked;
            maskedDisabledAlpha = previousAlpha;
        }
    }
}
