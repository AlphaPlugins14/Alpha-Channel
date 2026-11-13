using Dalamud.Game.Gui.NamePlate;

namespace AlphaChannel.Plugin.Video;

internal sealed class ScreenController : IDisposable
{
    private readonly Func<float> nameplateHideRadius;
    private float lastNameplateHideRadius = float.NaN;
    private bool lastScreenActive;
    private int disposeStarted;
    internal VideoEngine Engine { get; }

    public ScreenController(Func<float> nameplateHideRadius)
    {
        this.nameplateHideRadius = nameplateHideRadius;
        Engine = new VideoEngine();
        try { Plugin.NamePlateGui.OnNamePlateUpdate += OnNamePlateUpdate; }
        catch
        {
            Engine.Dispose();
            throw;
        }
    }

    public void OnFrameworkUpdate()
    {
        if (Volatile.Read(ref disposeStarted) != 0 || !Plugin.ClientState.IsLoggedIn)
            return;

        Engine.OnFrameworkUpdate();

        var currentRadius =
            Math.Clamp(
                nameplateHideRadius(),
                0f,
                5f);
        var currentScreenActive = Engine.IsActive;

        // NamePlateStripper uses Dalamud's persistent removed-field markers.
        // Stopping future stripping is therefore not enough to restore plates
        // that were already hidden. A full redraw rebuilds the original game
        // data, after which OnNamePlateUpdate reapplies the current radius.
        if (float.IsNaN(lastNameplateHideRadius) ||
            MathF.Abs(currentRadius - lastNameplateHideRadius) > 0.001f ||
            currentScreenActive != lastScreenActive)
        {
            Plugin.NamePlateGui.RequestRedraw();
            lastNameplateHideRadius = currentRadius;
            lastScreenActive = currentScreenActive;
        }
    }

    private void OnNamePlateUpdate(INamePlateUpdateContext context, IReadOnlyList<INamePlateUpdateHandler> handlers)
    {
        if (Volatile.Read(ref disposeStarted) != 0 || !Plugin.ClientState.IsLoggedIn)
            return;

        var hideRadius =
            Math.Clamp(
                nameplateHideRadius(),
                0f,
                5f);

        if (hideRadius <= 0f || !Engine.IsActive)
        {
            return;
        }

        foreach (var handler in handlers)
        {
            var gameObject = handler.GameObject;
            if (gameObject is null)
            {
                continue;
            }

            if (Vector3.Distance(gameObject.Position, Engine.ScreenPosition) <= hideRadius)
            {
                NamePlateStripper.Strip(handler);
                handler.VisibilityFlags = 0;
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposeStarted, 1) != 0)
            return;

        Plugin.NamePlateGui.OnNamePlateUpdate -= OnNamePlateUpdate;
        // Logout destroys the nameplate scene itself. Only request a restore
        // when unloading while a character and hidden plates still exist.
        if (lastScreenActive && Plugin.ClientState.IsLoggedIn)
            Plugin.NamePlateGui.RequestRedraw();
        Engine.Dispose();
    }
}
