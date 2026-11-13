namespace AlphaChannel.Plugin.Lighting;

// One render-time owner shared by manual lighting and the Halloween show.
internal sealed class SceneTimeControl(Func<uint, bool> apply, Func<bool> owns, Action release) : IDisposable
{
    internal int? Minutes { get; private set; }
    internal bool ShowActive { get; private set; }
    internal bool Active => Minutes.HasValue || ShowActive;
    private string? area;

    internal bool Set(int minutes, string scope)
    {
        if (ShowActive || minutes is < 0 or > 1439) return false;
        Update(scope);
        if (!apply((uint)minutes * 60)) { Update(scope); return false; }
        Minutes = minutes; area = scope;
        return true;
    }

    internal bool BeginShow(string scope)
    {
        Update(scope);
        if (!apply(0)) return false;
        ShowActive = true; area = scope;
        return true;
    }

    internal void EndShow(string? scope)
    {
        if (!ShowActive) return;
        Update(scope);
        if (!ShowActive) return;
        ShowActive = false;
        if (Minutes is { } minutes && apply((uint)minutes * 60)) return;
        Dispose();
    }

    internal void ResetManual()
    {
        Minutes = null;
        if (!ShowActive) Dispose();
    }

    internal void Update(string? scope)
    {
        if (Active && (scope is null || scope != area || !owns())) Dispose();
    }

    public void Dispose()
    {
        release(); Minutes = null; ShowActive = false; area = null;
    }
}
