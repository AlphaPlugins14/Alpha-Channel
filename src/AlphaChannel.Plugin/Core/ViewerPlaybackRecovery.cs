namespace AlphaChannel.Plugin.Core;

// Called only with fresh host states. Never infer a pause or restart from focus
// changes, missing heartbeats, a host pause, or an initial decoder load.
internal sealed class ViewerPlaybackRecovery
{
    private string? media;
    private DateTime sampled,progressAt,hostMovedAt,retryAt,started,healthySince;
    private double local,host,hostAdvance;
    private int attempts;
    private bool ignoreSeekJump;
    private DateTime seekAt;

    internal void Reset(){media=null;attempts=0;seekAt=default;ignoreSeekJump=false;}
    private void Motion(DateTime now,double localPosition,double hostPosition)
    {sampled=progressAt=hostMovedAt=now;local=localPosition;host=hostPosition;hostAdvance=0;ignoreSeekJump=false;healthySince=default;}

    internal bool Observe(string key,DateTime now,double hostPosition,bool hostPaused,
        double localPosition,bool loading,bool failed,bool finiteVideo)
    {
        if(!double.IsFinite(hostPosition)||!double.IsFinite(localPosition))return false;
        if(media!=key){Reset();media=key;started=now;retryAt=now.AddSeconds(5);Motion(now,localPosition,hostPosition);return false;}
        if(hostPaused||loading||now-sampled>TimeSpan.FromSeconds(5)||hostPosition<host-.5)
        {Motion(now,localPosition,hostPosition);return false;}
        sampled=now;
        if(hostPosition>host+.1){hostMovedAt=now;hostAdvance+=hostPosition-host;}
        host=hostPosition;
        if(Math.Abs(localPosition-local)>.15)
        {
            if(ignoreSeekJump){ignoreSeekJump=false;healthySince=default;}
            else
            {
                progressAt=now;
                if(healthySince==default)healthySince=now;
                if(attempts>0&&now-healthySince>=TimeSpan.FromSeconds(60))attempts=0;
            }
            local=localPosition;
        }
        if(failed||now-progressAt>TimeSpan.FromSeconds(3))healthySince=default;
        if(now-hostMovedAt>TimeSpan.FromSeconds(3)){Motion(now,localPosition,hostPosition);return false;}
        if(attempts>=3||now<retryAt||hostAdvance<2||now-started<TimeSpan.FromSeconds(5))return false;
        if(!failed&&(!finiteVideo||now-progressAt<TimeSpan.FromSeconds(20)))return false;
        attempts++;retryAt=now.AddSeconds(30);seekAt=now;
        Motion(now,localPosition,hostPosition);
        return true;
    }

    internal bool TryCorrectPosition(DateTime now)
    {
        if(now<seekAt)return false;
        seekAt=now.AddSeconds(3);ignoreSeekJump=true;return true;
    }
}
