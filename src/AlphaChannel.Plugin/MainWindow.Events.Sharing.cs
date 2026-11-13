using AlphaChannel.Plugin.Auth;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool eventDetailsLoading;
    private int eventDetailsGeneration;
    private void ReloadEventDetails(string id,bool edit=false)
    {
        if(CurrentSession is not { } session)return;
        var page=currentPage;var request=++eventDetailsGeneration; var selected=eventSelected?.Id; var wasOpen=eventDialogOpen; var wasEditing=eventEditorOpen;
        eventDetailsLoading=true;
        _=Task.Run(async()=>
        {
            try{var result=await eventsApi.Get(session.Token,id,youtubeFeedToken);await Plugin.Framework.RunOnFrameworkThread(()=>
            {if(!IsCurrentMessageSession(session)||request!=eventDetailsGeneration||currentPage!=page||eventSelected?.Id!=selected||eventDialogOpen!=wasOpen||eventEditorOpen!=wasEditing)return;events=events.Where(e=>e.Id!=id).Append(result).ToArray();if(result.Room is not null)eventRoomReceived[id]=DateTime.UtcNow;eventSelected=result;if(edit)OpenEventEditor(result,false);else eventDialogOpen=true;});}
            catch{await Plugin.Framework.RunOnFrameworkThread(()=>{if(IsCurrentMessageSession(session)&&request==eventDetailsGeneration&&currentPage==page&&eventSelected?.Id==selected&&eventDialogOpen==wasOpen&&eventEditorOpen==wasEditing){eventsError="This event could not be opened. It may no longer be available.";currentPage=HomePage.Events;}});}
            finally{await Plugin.Framework.RunOnFrameworkThread(()=>{if(request==eventDetailsGeneration)eventDetailsLoading=false;});}
        });
    }
}
