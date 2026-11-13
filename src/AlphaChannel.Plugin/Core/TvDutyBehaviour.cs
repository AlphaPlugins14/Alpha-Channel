namespace AlphaChannel.Plugin;

internal enum TvDutyBehaviour { StopPlayback, MiniPlayer }

internal enum ViewerPlaybackStartBehaviour { PromptOnly, SpawnTv, MiniPlayer }

internal static class TvDutyBehaviourPolicy
{
    internal static bool Applies(bool inDuty, bool hosting) => inDuty && !hosting;
}

// Keep numeric values stable so saved PausePlayback selections become StopPlayback.
internal enum TvInterruptionBehaviour { NoEffect, StopPlayback, LowerVolume, Mute }
