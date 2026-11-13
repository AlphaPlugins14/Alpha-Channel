using System.Diagnostics;
using System.Text;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private sealed record HelpCategory(string Name, FontAwesomeIcon Icon);
    private sealed record HelpQuestion(string Category, string Question, string Answer, params string[] Actions);

    private static readonly HelpCategory[] HelpCategories =
    [
        new("Getting started", FontAwesomeIcon.BookOpen),
        new("TV & screen", FontAwesomeIcon.Desktop),
        new("Media & playback", FontAwesomeIcon.Play),
        new("YouTube", FontAwesomeIcon.Film),
        new("Watch Parties", FontAwesomeIcon.Users),
        new("Broadcasting", FontAwesomeIcon.BroadcastTower),
        new("Browser", FontAwesomeIcon.Globe),
        new("Retro Games", FontAwesomeIcon.Gamepad),
        new("Music & DJ", FontAwesomeIcon.Music),
        new("Arcade activities", FontAwesomeIcon.Gamepad),
        new("Canvas", FontAwesomeIcon.PaintBrush),
        new("Friends & social", FontAwesomeIcon.UserFriends),
        new("Events", FontAwesomeIcon.CalendarAlt),
        new("Patreon & allowances", FontAwesomeIcon.Heart),
        new("Troubleshooting", FontAwesomeIcon.ExclamationTriangle),
        new("Allowance calculator", FontAwesomeIcon.Calculator),
    ];

    private static readonly HelpQuestion[] HelpQuestions =
    [
        new("Getting started", "How do I set up my TV?", "Open Screen from the sidebar, spawn your TV, then adjust its position and size in the game.", "Open Screen Settings"),
        new("Getting started", "How do I play my first video?", "Open Media Player and Add Media, choose a supported source, then select Play. The media will appear on your spawned Alpha Channel TV.", "Open Add Media"),
        new("Getting started", "How do I start my first DJ stream?", "Open Music → Broadcast Studio. Choose Song queue for your music library or Application audio for a running music app. Check the audio, optionally enable your microphone, then choose your quality and broadcast title. Select Go Live to broadcast to a hosted Watch Party.", "Open Broadcast Studio"),
        new("Getting started", "How do I create a Watch Party?", "Open Watch Party and choose Create Watch Party. You can then invite friends or let people find your room through the Party Directory.", "Open Watch Party", "Open Party Directory"),
        new("Getting started", "Do I need to create a Watch Party to use Alpha Channel?", "No. You can use your TV and play supported media by yourself. A Watch Party is only needed when you want to watch or interact with other people."),
        new("Getting started", "Can I change Alpha Channel settings later?", "Yes. Open Settings from the sidebar at any time to adjust your preferences.", "Open Settings"),
        new("Getting started", "How do I use a Discord Quick View Code?", "Paste the EV- code into Quick view on Events, then choose Open. You can also type /alpha view followed by the code. Sign-in is required.", "Open Events"),

        new("TV & screen", "Why can’t I see the TV?", "Open Screen and make sure the TV is spawned. If it is active but out of view, use the position controls to bring it back in front of your character.", "Open Screen Settings"),
        new("TV & screen", "Can I move my TV independently in a Watch Party?", "Yes. Disable ‘Match TV position & size with host’ in Screen Settings or from the Watch Party page.", "Open Screen Settings", "Open Watch Party"),
        new("TV & screen", "How do I change the TV volume?", "Click the speaker icon near the top-right corner of the Alpha Channel window to open TV volume and mute controls. Volume controls are also available on the playback bar and relevant playback pages. Broadcast Studio has separate controls for music, microphone, broadcast output and local listening.", "Open Screen Settings", "Open Broadcast Studio"),
        new("TV & screen", "Where are my screen controls?", "Open the Screen page from the menu to spawn, resize, reposition, mute, or despawn your TV.", "Open Screen Settings"),
        new("TV & screen", "Why does my TV move when the host moves theirs?", "The ‘Match TV position & size with host’ option is enabled. Disable it in Screen Settings if you want to position and resize your TV independently.", "Open Screen Settings"),
        new("TV & screen", "How do I reset the TV if it is off-screen?", "Open the Screen page and select Recenter TV to place it in front of your character. Spawning the TV also recentres it, unless an eligible saved preset is auto-applied.", "Open Screen Settings"),
        new("TV & screen", "Can I hide the TV without leaving my Watch Party?", "Yes. You can despawn the TV from the Screen or Watch Party page while remaining connected to the Watch Party.", "Open Screen Settings", "Open Watch Party"),
        new("TV & screen", "Why is there a bouncing screensaver on my TV?", "When the TV is spawned with nothing playing or loading, it shows the bouncing screensaver. The mini player does the same when idle. Start media to replace it; loading content uses the loading display."),
        new("TV & screen", "How do I open or close the mini player and mini chat?", "Use their buttons on the minimised bar. Clicking a button opens its window; clicking it again closes that window. Mini chat is available while connected to a Watch Party."),

        new("Media & playback", "Which media sources can I use?", "Browse YouTube, Dailymotion or Internet Archive, or open Media Player → Add Media to choose another supported source. You can also play local videos, radio and live streams, use the embedded browser, or play retro games and party activities such as Canvas. Music → Broadcast Studio lets you mix songs or application audio with your microphone.", "Open Add Media", "Open Dailymotion", "Open Internet Archive", "Open Broadcast Studio"),
        new("Media & playback", "What are the Dailymotion tabs for?", "Popular shows videos popular with Alpha Channel users. Use Search to find a video, or paste a Dailymotion video link into the search box.", "Open Dailymotion"),
        new("Media & playback", "How do I choose an Internet Archive episode?", "Open Internet Archive and browse a category or search for a title. A single video has a Play button. If an item contains several episodes, choose its episodes button to open the list, then select the episode you want to watch.", "Open Internet Archive"),

        new("Media & playback", "Why is nothing playing?", "Check that the TV is spawned, the selected media is still available, and Alpha Channel is connected. Try stopping the current item before starting a different media type."),
        new("Media & playback", "How does the queue work?", "Open Media Player → Queue. Personal queues keep your own lists of videos; the Watch Party queue is shared with your party. Use the queue buttons to choose which list to view. Resume queue starts the selected playable list, and Auto-play next continues when a video finishes. During a Watch Party, the host controls shared playback; viewing a personal queue does not replace the party queue.", "Open Media Queue"),
        new("Media & playback", "How do I create and switch personal queues?", "Open Media Player → Queue and choose New queue to create a list in an available slot. You can keep up to three personal queues. Click a queue name to view it, and use Activate queue when you want that list to supply your personal playback. Watch Party Queue opens the shared list while you are in a party. You can still view and organise personal queues during a party, but cannot activate one to take over shared playback.", "Open Media Queue"),
        new("Media & playback", "How do I reorder or copy queued videos?", "Drag a video row to a new position; the dragged preview and position marker show where it will go. The video’s three-dot menu also offers Move up and Move down. Only the host can reorder the Watch Party queue. In a personal queue, use the Copy to section to copy a video to another personal queue or the Watch Party queue. Copying keeps it in the original list. The Watch Party destination is available only while you are in a party.", "Open Media Queue"),
        new("Media & playback", "What do Up next, Requests and Played mean?", "Up next contains approved videos waiting to play. Requests contains videos awaiting the host’s approval, with older requests first. Played contains previous videos; use a video’s menu to add or request it again. Switching tabs takes you to the first upcoming video, the oldest request, or the latest played video.", "Open Media Queue"),
        new("Media & playback", "When does Auto-play next start the party queue?", "With Auto-play next enabled, adding a video when there is no current video or upcoming queued video starts the queue automatically. A request must be accepted first. Adding a video to an existing queue does not restart it or skip the current video. Use Resume queue to start a waiting queue manually. End any active TV activity before starting queued videos.", "Open Media Queue"),

        new("Media & playback", "Can I play media without broadcasting it?", "Yes. Some media can be played locally on your own TV. Broadcasting is used when you want to send supported content through Alpha Channel to your Watch Party."),
        new("Media & playback", "Why did playback stop when I changed media types?", "Alpha Channel may need to stop the current source before starting a different type of media. Wait for the current item to close, then start the new source again."),
        new("Media & playback", "Can viewers add items to the queue?", "Yes, if the host allows it. When you choose Play while viewing a Watch Party, select a personal queue or the party queue. Host only lets the host add videos; Requests sends videos for approval; Open queue adds directly to Up next. The host controls shared playback and can accept requests from the Requests tab.", "Open Media Queue"),
        new("Media & playback", "Why can’t I seek a radio station or live DJ broadcast?", "Live audio is an ongoing stream rather than a recording. The playback bar shows how long you have been listening, but it cannot be clicked or dragged to seek. The LIVE label appears beside the content name."),

        new("Watch Parties", "How do I join a friend’s Watch Party?", "Open Friends, Watch Party or Party Directory and choose an available room. You can also find a friend’s room using their Alpha Channel username. Locked rooms require the host’s password.", "Open Friends", "Open Watch Party", "Open Party Directory"),
        new("Watch Parties", "Why is my playback different from the host?", "A delay of a couple of seconds can be normal. Use Re-sync on the Watch Party page to try to bring your playback back into sync.", "Open Watch Party"),
        new("Watch Parties", "Who can control playback?", "The host controls shared playback. Viewers can request media where supported, but the host decides what plays on the party TV."),
        new("Watch Parties", "What happens if the host leaves the Watch Party?", "If the host closes the room or disconnects, the party ends and viewers are removed. To keep it open, the host can first choose Make host from a viewer’s roster menu. End active games or locally produced streams before transferring host; the option is disabled while unsupported content is active.", "Open Watch Party"),
        new("Watch Parties", "What is the difference between a locked and public Watch Party?", "Public Watch Parties can be joined by anyone via the Party Directory. Locked Watch Parties require a password from the host to join.", "Open Party Directory", "Open Watch Party"),
        new("Watch Parties", "How do I kick or ban someone from my party?", "As host, open the three-dot menu beside a person in the party roster and choose Kick or Ban. Kick removes them from the current party, but they can join again. Ban also prevents them joining your Watch Parties until you unban them.", "Open Watch Party"),
        new("Watch Parties", "How do I manage or remove party bans?", "As host, choose Manage bans beside Invite friends on the Watch Party page. Find the person and choose Unban to let them join your parties again. Your bans also apply to future Watch Parties you host.", "Open Watch Party"),

        new("Watch Parties", "Why can’t I start a broadcast in the Watch Party?", "Only the host can begin a shared broadcast. The host must also have sufficient weekly broadcast allowance remaining."),

        new("Broadcasting", "What uses broadcast credits?", "Browser sharing, retro games, local video, PC live streaming, DJ Live and Broadcast Studio consume the shared weekly broadcast allowance while actively broadcasting to at least one viewer. No allowance is used when there are no viewers. Both Studio sources—Song queue and Application audio—use allowance when sent to your Watch Party. Local Studio preview and managing your library do not use broadcast credits."),
        new("Broadcasting", "When does my allowance reset?", "Your weekly broadcast allowance resets Monday at 00:00 UTC, regardless of your local timezone. The allowance bar shows your remaining credits and the reset time."),
        new("Broadcasting", "Why are some quality options locked?", "Quality access depends on the broadcast source. In Broadcast Studio, Data Saver, Standard and Recommended are available to everyone. High Quality requires Patreon Tier 2, and Highest Quality requires Tier 3. Other broadcast sources have their own quality levels; each locked option shows its required tier.", "Open Broadcast Studio"),
        new("Broadcasting", "When are broadcast credits consumed?", "Credits are consumed while a supported broadcast is actively being sent to at least one viewer in your Watch Party. No allowance is used with zero viewers. The rate depends on the broadcast method, stream quality, and number of viewers. Verified Patreon viewers reduce usage for Patreon hosts; this viewer discount does not apply to free hosts.\nYou can check your average credits being used per minute while broadcasting in the Watch Party \"Now Playing\" tab.", "Open Party Now Playing"),
        new("Broadcasting", "What happens when my broadcast allowance runs out?", "You will be unable to continue or start another broadcast until your allowance resets or your available allowance is increased through Patreon membership.\nYou can still join/host Watch Parties and use most media sources, just not those which require live broadcasting to our server."),
        new("Broadcasting", "Why does PC live streaming use credits at different rates?", "PC live streaming usage depends on the stream bitrate. Lower the bitrate or resolution in your streaming software to use less allowance. Your current average credits per minute is shown in Watch Party under Now Playing.", "Open Party Now Playing"),

        new("Browser", "Why won’t a protected video service play?", "Services such as Netflix, Disney+, and Amazon Prime use DRM that the embedded browser cannot currently play. Supported non-DRM sites can still be used."),
        new("Browser", "How do I control the embedded browser?", "Choose Control Browser beneath the browser view. This sends keyboard and mouse input to the browser.", "Open Browser"),
        new("Browser", "Can I broadcast the embedded browser to my Watch Party?", "Yes. The host can broadcast supported browser content while a Watch Party is active and broadcast allowance is available."),
        new("Browser", "What is the difference between browser broadcast quality levels?", "Data Saver broadcasts at 640×360 and approximately 700 Kbps. Standard Quality requires Patreon Tier 2 and broadcasts at 960×540 and approximately 1,500 Kbps. High Quality requires Patreon Tier 3 or higher and broadcasts at 960×540 and approximately 1,900 Kbps. FPS is chosen separately: 20 FPS is suggested for Saver/Standard, 25 FPS for High Quality, and 30 FPS for video streams. Stop broadcasting before changing quality or FPS."),
        new("Browser", "Why can I hear a website but not see its video?", "The website may be using protected video, an unsupported playback method, or hardware rendering that cannot be captured. Try another supported website or media source."),
        new("Browser", "Can I visit any website?", "You should treat this like any other browser and only visit websites you'd feel comfortable visiting in your own web browser. You should exercise normal internet safety when browsing."),

        new("Retro Games", "How do I add a game?", "Choose a console, select Add Games, and import a compatible ROM.", "Open Retro Games"),
        new("Retro Games", "Can viewers control a game?", "Only the host can control a retro game. Viewers can watch its broadcast; remote multiplayer control is not currently supported."),
        new("Retro Games", "Which consoles are supported?", "Open Retro Games to see the currently supported console categories and manage the games available for each one.", "Open Retro Games"),
        new("Retro Games", "Why isn’t my controller working?", "Open Configure Controller for the selected console and check that the intended controller and bindings are selected before starting the game.", "Open Retro Games"),
        new("Retro Games", "What happens when I switch to another program?", "Emulator controls are ignored while FFXIV is not the active window and resume when you return. The default Start key is T. Other plugins may still receive control keys, so avoid Enter or / when choosing custom bindings."),
        new("Retro Games", "Can I broadcast a game to my Watch Party?", "Yes. Start the game and host a Watch Party, then select Start Broadcast to choose quality and FPS in the settings popup. Creating a party does not start broadcasting. Choose 15, 24 or Native game FPS; lowering broadcast FPS does not change your local game speed.", "Open Retro Games"),

        new("Music & DJ", "Which music mode should I use?", "DJ Live connects external DJ or broadcasting software using your private connection settings. Broadcast Studio mixes songs from your library or audio from a running application with your microphone inside Alpha Channel. Radio Stations is for listening to and sharing stations. Scene controls the visualiser, screen text and surrounding effects.", "Open DJ Live", "Open Broadcast Studio", "Open Radio Stations", "Open Scene"),
        new("Music & DJ", "How do I start a Broadcast Studio stream?", "Open Music → Broadcast Studio. Choose Song queue or Application audio and check that your source is producing audio. For songs, select Play; for an application, enable Capture on. Optionally enable your microphone and check its meter. Choose your broadcast quality and enter a title under Your broadcast. Select Go Live; if you are not already hosting a Watch Party, complete the party creation form. Only the host can broadcast. Previewing locally does not send audio to listeners or spend broadcast credits.", "Open Broadcast Studio"),
        new("Music & DJ", "How do I set up my music library?", "In Broadcast Studio, select Song queue, then Library. Choose a folder on your PC to store your music. Use Import songs or Import folder to add files, or Scan folder to find supported files already there. Use the library search to find a song and Rename to change its displayed name. Supported file types include MP3, WAV, FLAC, OGG, Opus, M4A, AAC, AIFF and WMA; individual files still need to be decodable.", "Open Broadcast Studio"),
        new("Music & DJ", "Does importing move or copy my songs?", "Imported songs are copied into your chosen music library folder; the originals stay where they were. Files already directly inside that folder are used without another copy. Importing the same file again reuses its matching copy. Different files with the same name get separate filenames so existing songs are not overwritten.", "Open Broadcast Studio"),
        new("Music & DJ", "What happens when I change the music library folder?", "Use Change folder in Library. Alpha Channel copies your library into the new folder and updates the song paths in your library, queue and saved playlists. Copies in the old folder remain. Keep the new folder available whenever you want to play those songs.", "Open Broadcast Studio"),
        new("Music & DJ", "Does removing a library song delete its file?", "No. Remove in Library removes the library entry but keeps the music file and existing playlists. Scanning the folder can add it back. Remove song in the queue only removes that queue entry; it does not remove the library song or delete its file.", "Open Broadcast Studio"),
        new("Music & DJ", "How do I add songs and manage my queue?", "Choose Song queue, then Add songs. Select songs from your music library and choose Add selected. Click a queue row to select a song, then press Play in the Studio controls. The Play button beside each song starts it immediately. You can also use its three-dot menu for Play now, Play next, Move up, Move down or Remove song. Shuffle changes the playback order, Repeat allows the queue to wrap, and Auto next advances when a song finishes. The Studio song queue is separate from the general media queue.", "Open Broadcast Studio"),
        new("Music & DJ", "How do I save or load a playlist?", "Choose Save playlist beneath the song queue and enter a name. Saving an existing name replaces that playlist. Load playlist lets you load or delete a saved playlist; loading replaces the current queue. Playlists refer to your library files, so keep those files available. Removing a playlist does not delete its songs.", "Open Broadcast Studio"),
        new("Music & DJ", "How do I capture Spotify, a browser or another music player?", "In Broadcast Studio, choose Application audio, select a running application under Capture audio from, then enable Capture on. Start and control music in that application; Studio handles its broadcast volume, microphone mixing and scene. Use Refresh if the application is missing or has restarted. Capture includes the selected application's child processes, so a browser may include audio from multiple tabs. Application capture requires Windows build 20348 or later.", "Open Broadcast Studio"),
        new("Music & DJ", "What happens when I switch between songs and application audio?", "Only the selected source feeds the Studio music input. Switching sources pauses queue playback and turns application capture off. Your queue and saved playlists remain. When you return to Song queue, press Play to resume; in Application audio, choose your application and turn capture on. Capture is off after plugin launch, and the application must be selected again.", "Open Broadcast Studio"),
        new("Music & DJ", "How do push-to-talk and Duck music work?", "Enable your microphone and choose its device. Push to talk sends your voice only while you hold the selected key. With it disabled, the enabled microphone sends continuously. Duck music lowers the selected music source in the broadcast while you speak; Ducking amount controls the reduction. These controls work with both songs and application audio. The microphone starts off after plugin launch.", "Open Broadcast Studio"),
        new("Music & DJ", "What do Music, Input, Master and Local listening volume control?", "Music volume controls the selected source before it is mixed. Input volume controls the microphone. Master volume controls the combined broadcast output. Local listening and the shared playback volume/mute controls adjust the Studio's local monitor without changing listeners' volume. Unmuting in application mode also enables Hear captured application. Hear microphone enables local voice monitoring; use headphones. In application mode, Local listening does not change the original application's own volume. Hear captured application must be enabled if you want Studio to monitor that capture locally.", "Open Broadcast Studio"),
        new("Music & DJ", "Why can I hear the application twice?", "The selected application normally already plays through your speakers or headphones. Leave Hear captured application off to avoid Studio playing a second copy. Enable it only when you deliberately need Studio's local monitor. If you hear a delayed copy while broadcasting, also check whether your TV is unmuted and playing the returning broadcast.", "Open Broadcast Studio"),
        new("Music & DJ", "Which Broadcast Studio quality should I choose?", "Data Saver — 96 kbps uses the least allowance. Standard — 128 kbps is a normal-quality option. Recommended — 192 kbps balances quality and allowance use. High Quality — 256 kbps requires Patreon Tier 2; Highest Quality — 320 kbps requires Tier 3. Higher bitrates use allowance faster, and applicable Watch Party discounts can reduce usage. The selector below the allowance bar controls broadcast quality, not the quality of your source files. Stop broadcasting before changing it.", "Open Broadcast Studio"),
        new("Music & DJ", "How do listeners see my broadcast title and listener count?", "The title under Your broadcast is used as the content name in your Watch Party and Party Directory. You can edit it while broadcasting. The Listeners label shows the Watch Party member count; it is not a measurement of who currently has their audio audible.", "Open Broadcast Studio"),
        new("Music & DJ", "How do I connect external DJ software?", "Open Music → DJ Live, get your Connection settings and copy them into compatible DJ software. Begin sending audio, then use Check connection before Start broadcasting. These connection settings are for external software; Broadcast Studio uses its built-in connection and does not need you to enter them.", "Open DJ Live"),
        new("Music & DJ", "What audio settings should I use in external DJ software?", "Use MP3, OGG or AAC where supported, stereo, and 44.1 or 48 kHz. Use 128 kbps as the normal-quality baseline for external DJ streaming. Configure the bitrate in your external software. The Broadcast Studio quality selector controls Studio broadcasts and does not change your external encoder.", "Open DJ Live"),
        new("Music & DJ", "Why does DJ Live say Not connected?", "Confirm that your DJ software is actively streaming and that its server, port, username, password and mount details exactly match those shown in Alpha Channel.", "Open DJ Live"),
        new("Music & DJ", "When should I press Check connection?", "Start broadcasting from your DJ software first. Once it is sending audio, select Check connection so Alpha Channel can detect the incoming stream. Broadcast Studio connects when you select Go Live and does not need this external-software connection check.", "Open DJ Live"),
        new("Music & DJ", "Should I share my DJ Live connection details?", "No. Keep the password and private connection details secure. Anyone with those details may be able to send audio to your DJ Live connection."),
        new("Music & DJ", "Why is my TV muted while DJing?", "Alpha Channel automatically mutes the host's returning DJ broadcast to avoid a delayed second copy of audio you are already hearing locally. This does not mute the broadcast for listeners. You can unmute using the TV audio controls or the DJ mute notice. Muting your TV again still allows the visualiser to react while you are hosting the DJ broadcast."),
        new("Music & DJ", "Where can I adjust my music scene?", "Open Music → Scene, choose Edit scene in Broadcast Studio, use Adjust Scene on the playback bar, or choose Adjust Music Scene in Watch Party. Your scene is used for supported music playback and shared with listeners when hosting. Viewers follow the host's scene and cannot edit it.", "Open Scene"),
        new("Music & DJ", "How do I customise my music scene?", "Choose a visualiser style, colour theme or gradient, and background colour. Toggle speakers, lasers, halo, LED strips, expanding rings and sparkles, then choose their colours. Rainbow LED colours overrides the single LED colour. Add optional top, middle and bottom screen text and use Edit text for its font and appearance. Changes apply to active supported music playback.", "Open Scene"),
        new("Music & DJ", "Why is the scene preview static?", "The Scene page shows a static illustration of your design so you can edit it without playing music. The actual TV visualiser reacts to audio and the surrounding effects animate during playback. A static preview does not mean the live scene has stopped working.", "Open Scene"),
        new("Music & DJ", "Why do scene effects continue during silence?", "Enabled surrounding effects keep an idle fallback between songs and during quiet moments, then react more strongly when audio returns. The actual visualiser still follows the audio and can flatten during silence. Turn off an individual effect in Scene if you do not want it visible.", "Open Scene"),
        new("Music & DJ", "Can I save a music scene?", "Choose Save scene and give it a name. A saved scene stores the visualiser, screen text, fonts, effects and colours. Use Saved scenes to load or delete a design. Loading applies it to active supported music playback. Reset scene restores the default design after confirmation; saved designs are separate.", "Open Scene"),
        new("Music & DJ", "Where can I find radio stations?", "Open Music → Radio Stations to search recommended stations or save your own direct audio stream URL. Choose Play for your current playback or Play in Party to share with a Watch Party.", "Open Radio Stations"),

        new("Canvas", "What is the difference between Solo and Team mode?", "Solo lets you draw by yourself without a Watch Party. In a party, Solo lets only the host draw. Team lets everyone in the Watch Party contribute, while the host keeps undo, clear, background and stop controls."),
        new("Canvas", "Will new participants see the existing canvas?", "No. Solo and Team Canvas are live-only. Late joiners start with a blank canvas and see only drawing sent after they join. Earlier strokes are not replayed. Existing participants keep their drawing when someone joins or leaves."),
        new("Canvas", "Who can undo or clear the Team Canvas?", "Only the Watch Party host can undo strokes, clear the canvas, change the shared background, or stop the Canvas session."),
        new("Canvas", "Do Canvas drawings use broadcast allowance?", "No. Canvas sends drawing commands between Watch Party members and does not use the weekly live-broadcast allowance."),
        new("Canvas", "What happens to the drawing when the Canvas session ends?", "The active shared canvas is cleared when the session ends. Start a new Solo or Team Canvas session when you want to draw again."),

        new("Friends & social", "How do I add a friend?", "Open Friends and enter their Alpha Channel username or friend code. You can also choose Add Friend from the three-dot menu beside someone in the Watch Party roster. The option is disabled if they are already your friend. Once the request is accepted, you can see their presence and available parties.", "Open Friends", "Open Watch Party"),
        new("Friends & social", "How do I start a private message?", "Open Chats → Messages and choose New direct chat, or use the plus button beside the conversation list. Choose a friend and open the conversation, then type your message and press Send. Select a conversation in the list to return to it.", "Open Messages"),
        new("Friends & social", "How do I create or join a group chat?", "Open Chats → Groups and choose Create group. Choose a private group for invited people or a public group that others can find in Discover, then fill in the details and invite people. Group invitations appear in Groups; accept an invitation to join.", "Open Groups"),
        new("Friends & social", "How do I find public group chats?", "Open Chats → Discover to browse or search public groups. Choose Join to become a member. Clicking a group you have already joined opens its chat; clicking one you have not joined highlights its Join button. Your joined groups are available in Groups.", "Open Discover"),
        new("Friends & social", "What do green message ticks mean?", "Green ticks mean your message has been seen. In a direct conversation, the other person has seen it. In a group, at least one other member has seen it; it does not mean everyone has read it.", "Open Messages"),
        new("Friends & social", "How do I use Alpha Chat and find older messages?", "Open Chats → Alpha Chat to join the community conversation. Scroll to the top and choose Load more messages to see older messages available in your history. A new installation may have less history than a plugin you have been using for a while.", "Open Alpha Chat"),
        new("Friends & social", "How do I delete an Alpha Chat message?", "Open your message’s menu and choose Delete message. Recent messages are removed for everyone. Some older messages are only available in your own history; deleting one of those removes only your copy.", "Open Alpha Chat"),

        new("Friends & social", "Can I hide reactions from someone?", "Use the three-dot menu beside that person in the Watch Party roster to choose which of their chat and TV reactions you want to see.", "Open Watch Party"),
        new("Friends & social", "How do I invite a friend to my Watch Party?", "While hosting, choose Invite friends on the Watch Party page and select who to invite. You can also open a friend’s profile and choose Invite to Watch Party. Friends can still find your room through Friends or Party Directory; locked rooms require the party password.", "Open Watch Party", "Open Friends"),
        new("Friends & social", "What does a friend’s presence status mean?", "Presence shows whether a friend is currently connected to Alpha Channel and whether they have an available Watch Party you can join."),
        new("Friends & social", "Can I hide all reactions?", "Open Watch Party → Chat, then open Chat options and enable Hide all reactions in party chat. Disable it when you want reactions to appear in chat again.", "Open Party Chat"),

        new("Patreon & allowances", "What does Patreon membership change?", "Patreon tiers increase the weekly broadcast allowance. Higher tiers can also unlock additional broadcast quality options and increase your Watch Party member limits."),
        new("Patreon & allowances", "Why is broadcasting limited?", "Live broadcasting consumes Alpha Channel server bandwidth. The allowance lets everyone try broadcasting while memberships help fund bandwidth and hosting."),
        new("Patreon & allowances", "What is the weekly allowance for each Patreon tier?", "Weekly broadcast allowances are:\nFree: 420 credits\nTier 1: 1,030 credits\nTier 2: 1,750 credits\nTier 3: 2,960 credits\nTier 4: 4,740 credits\nTier 5 or higher: 6,920 credits\n\nThese are allowance totals. How quickly you use them depends on the broadcast method, quality and applicable party-size discounts."),
        new("Patreon & allowances", "How do viewers affect broadcast credit usage?", "Each viewers slightly increases how much of your broadcast allowance is consumed.\nViewers with an active Patreon subscription apply a discount to your allowance usage."),
        new("Patreon & allowances", "Which Patreon tier unlocks higher broadcast quality?", "Each locked quality option displays the required tier. Tier 2 unlocks the first quality upgrade, while sources with a further upgrade require Tier 3 for their highest quality."),

        new("YouTube", "Why does the TV say Establishing connection to YouTube?", "Alpha Channel is still preparing the connection. Leave the video loading; the message stays on the TV until playback starts. If an error appears or it never starts, stop the video and try again.", "Open Add Media"),
        new("YouTube", "Why is a video blocked for being age restricted?", "Some videos require a signed-in YouTube account that is eligible to watch age-restricted content. Choose here in the Video blocked by YouTube warning, or open Settings → YouTube account, to connect your account. You can dismiss the warning and keep using the current page. Connecting an account does not remove YouTube’s age or availability restrictions.", "Open YouTube Settings"),
        new("YouTube", "Why do I get the same age-restriction warning when trying again?", "Connect a usable YouTube account in Settings before trying that video again. Repeatedly pressing Play without connecting an account will show the warning again rather than keep trying a video YouTube has already blocked. In a Watch Party, the warning appears once for that playback.", "Open YouTube Settings"),

        new("YouTube", "What are the YouTube tabs for?", "Topics helps you browse by interest. Subscriptions and Likes show content from your connected YouTube account. Recommended provides suggestions, while Popular shows videos being shared across Alpha Channel. Popular and Recommended are separate feeds."),
        new("YouTube", "How do I connect my YouTube account?", "Open Settings and find YouTube account. Connect the account signed into Alpha Channel’s browser and choose whether to sync liked videos, subscriptions and recommendations. Each browser keeps its own YouTube sign-in.", "Open YouTube Settings"),
        new("YouTube", "What is Popular on Alpha Channel?", "It shows the most popular YouTube videos watched by users across the Alpha Channel plugin over the last 30 days."),
        new("Arcade activities", "How do I start an arcade activity?", "Open The Arcade, choose an activity and read its rules/start page. Choose standard rules or customise the options where available, then start solo or with a Watch Party. The party host starts and ends shared activities. Available modes and player requirements are shown for each game.", "Open The Arcade"),
        new("Arcade activities", "Do activities use real currency or credits?", "Arcade activities use temporary play chips, coins or scores. They do not spend or award real gil or MGP, and do not use live-broadcast allowance. Broadcasting a retro emulator, browser or local video is separate and does use allowance."),
        new("Arcade activities", "How do I join and start Poker?", "Choose an open seat at the table. The host selects Deal hand when everyone is ready. One seated player faces the house bot if enabled; two or more human players play each other without a bot. Standard/custom rules control starting chips, blinds, turn time and the solo bot.", "Open Poker"),
        new("Arcade activities", "How do Poker hands and betting work?", "Your two private cards combine with the five shared board cards to make your best five-card hand. Choose Fold, Check/Call, Bet/Raise or All in on your turn. Betting runs before the flop and after the flop, turn and river. The strongest hand wins; ties split the pot and all-ins can create side pots. The start page lists hand ranks. Folded hands stay hidden.", "Open Poker"),
        new("Arcade activities", "Can I pause Poker or keep my chips?", "The host can pause between hands or end the table. Otherwise, hands continue automatically after results. Leaving and rejoining the same table retains your stack. The session ledger shows hands, wins, chips, net gains and free refills. Stacks below the big blind are refilled before a deal; refills do not count as profit. The ledger resets when the table ends.", "Open Poker"),
        new("Arcade activities", "How does Slot Machine shared play work?", "Host-only mode lets everyone watch the host play. In shared mode, click the coin slot to join the turn queue. Each player has their own balance while everyone watches the same cabinet. When your turn is ready, pull the handle within 20 seconds or your stake is refunded and the queue moves on.", "Open Slot Machine"),
        new("Arcade activities", "What are Slot Machine payouts and refills?", "Start with 100 play coins; each spin costs 5. Only the middle row pays: each symbol has its own matching-pair and triple payout, listed in the machine Help panel. Three Gil symbols return 250. Returns include your stake. Triples and jackpots trigger coin celebrations. The ledger and recent wins show how everyone is doing. A balance below the spin cost refills to 100 on your next turn; refills do not count as profit.", "Open Slot Machine"),
        new("Arcade activities", "How do Bingo cards and rounds work?", "Lock your card before the round starts, then follow the called numbers and complete the displayed pattern. Standard rules rotate through patterns; custom rules let the host choose the pattern, rounds and calling speed. Patterns include one, two or three lines, a cross, an X, four corners and a full house. Round wins earn points; players completing the pattern on the same call can tie. Final results show the scores, and the host can replay.", "Open Bingo"),
        new("Arcade activities", "How do I play Air Force EX?", "Join the ready lobby; the host starts the flight when pilots are ready. Aim and click on the TV or plugin preview. Blue, silver and gold targets earn points; red targets cost points. Consecutive hits build a combo, while misses or red hits reset it. Each pilot can hit each normal target once. Work together against a boss and its rockets. The host chooses waves and boss options; final results remain until replay or the game ends.", "Open Air Force EX"),
        new("Arcade activities", "How do Pictionary and Teams work?", "In Pictionary, players take turns drawing while others guess. Pictionary Teams uses red and blue teams competing to draw and guess. Choose the rules on the start page and follow the current drawer and round prompts. Submit guesses on the TV or in the plugin. In Pictionary, correct guesses mark players green for that round, and the drawer sees correct answers and incorrect guesses in red/green notifications, spaced three seconds apart and cleared at round end.", "Open Pictionary", "Open Pictionary Teams"),
        new("Arcade activities", "How do Quiz answers and scores work?", "Choose your answer before time runs out. When the answer is revealed, players’ scoreboard names briefly turn green for correct answers and red for incorrect answers. Follow the scoreboard to compare scores during the game; the game’s rules explain its round and scoring options.", "Open Quiz"),
        new("Arcade activities", "How do I place Roulette bets?", "Choose a chip and click a betting space while betting is open. Hover a space to see its payout odds and highlight the numbers it covers. You can place bets on different spaces before betting closes. Watch the result and use the ledger to compare balances and results.", "Open Roulette"),
        new("Arcade activities", "How do Choco RaceBets bets work?", "Choose chips and build a spread across one or more chocos, then select Place Bet to lock it for the race. The first placed bet starts the countdown. Unplaced chips clear when betting closes. Winning bets pay according to the displayed odds; check the results and ledger to see how everyone is doing. Starting chips and refills depend on the selected rules.", "Open Choco RaceBets"),
        new("Broadcasting", "How do I broadcast a browser or local video?", "Host a Watch Party first. Create Watch Party opens the party form but does not start broadcasting. Select Start Broadcast to open broadcast settings, review your allowance and choose quality and FPS, then confirm. The compact bar shows your quality and FPS while broadcasting. For local video, choose a file beside Browse; Play Local Video plays it on your own TV.", "Open Browser", "Open Local Video", "Open Watch Party"),
        new("Broadcasting", "Which broadcast FPS should I choose?", "Browser: 15 FPS uses the least CPU, 20 is suggested for Saver/Standard, 25 for High Quality, and 30 for video streams.\nRetro games: choose 15, 24 or Native game FPS.\nLocal video: choose a maximum of 15, 24 or 30 FPS; a slower source keeps its own rate.\n\nLower FPS reduces broadcast processing demand without changing local playback or game speed. FPS alone does not change the allowance rate. Stop broadcasting before changing settings."),
        new("Browser", "Why was a redirect or popup blocked?", "Redirect and popup protection can stop a page opening another destination. Built-in and personal whitelist entries allow redirects from recognised sites. Allow accepts the current request; Always allow remembers the source website. Ignore rejects it, and Ignore All blocks redirects from that page for the session. Manage your exceptions in the browser settings. A whitelisted site is not guaranteed safe or able to play protected video.", "Open Browser"),
        new("Browser", "How do I return keyboard control to FFXIV?", "Press Ctrl + F12 at any time to return control input to FFXIV, or select Control FFXIV below the browser. This shortcut also works while controlling a retro game.", "Open Browser"),
        new("Retro Games", "How do I switch controls back to FFXIV?", "Press Ctrl + F12 to force input back to FFXIV. You can also choose Control FFXIV in the main window, or use the controller/TV buttons above the mini dock while a retro game is running to switch between game and FFXIV controls.", "Open Retro Games"),
        new("TV & screen", "How do TV presets and auto-apply work?", "Save a TV position preset on the Screen page for your current location. Enable auto-apply to restore it when an eligible TV spawn happens there. Manually moving the TV while that preset is active disables its auto-apply if it was enabled. Other presets are unaffected.", "Open Screen Settings"),
        new("Canvas", "Can I draw directly on the TV?", "Yes. Use the TV drawing controls or draw in the plugin’s Canvas preview. Solo works without a Watch Party. In Team mode, everyone can draw on the shared canvas; only the host can undo, clear, change the background or stop it.", "Open Canvas"),
        new("Friends & social", "How do chat filters and mentions work?", "All shows every message. Chat shows conversation, Requests shows video requests, and Other shows reactions, game notices and join/leave information. When someone mentions you with @, an eye button appears above React Live if you have unseen mentions. Hover it to see the count and click to jump to the newest unseen mention. Repeat to visit older unseen mentions; mentions disappear from the count once seen.", "Open Party Chat"),
        new("Friends & social", "How do I preview or edit my public profile?", "Open Friends → My profile to see your public profile. Choose Edit profile there to change your details. Your friend code is also shown on Friends and can be copied for someone to add you.", "Open My Profile", "Open Friends"),
        new("Troubleshooting", "How can I improve a stuttering broadcast?", "For browser or local-video stutter, stop the broadcast and try a lower FPS in broadcast settings. Test a lower quality too. If audio crackles, cuts out or falls out of sync, report whether it affects the host, viewers or both. Include the browser used (Chromium or Edge), quality/FPS settings, relevant host/viewer logs and approximate times. Copy diagnostics as well; these symptoms can have different causes."),
        new("Troubleshooting", "Why is Broadcast Studio not receiving application audio?", "Check that Application audio is selected, the intended app is running and playing audio, and Capture on is enabled. Use Refresh after opening or restarting the app, then choose it again. Check the Audio level meter and the original application's volume. If capture reports an error, turn it off and on. Application capture requires Windows build 20348 or later. Closing the app ends its capture; Studio does not switch to another app automatically.", "Open Broadcast Studio"),
        new("Troubleshooting", "Why is a library song missing or unable to play?", "Library and playlist entries refer to files in your chosen folder. Make sure the folder or drive is available and the file has not been moved or deleted. Use Scan folder or import the song again, then add the available entry to your queue. A file with a supported extension can still be damaged or use an unsupported audio format; try another file.", "Open Broadcast Studio"),
        new("Troubleshooting", "Why can’t listeners hear my Studio microphone?", "Enable the microphone, choose the correct device and check Input volume and the Input level meter. If Push to talk is enabled, hold the selected key while speaking. Check Master volume and that your broadcast is live. Hear microphone only controls your own monitoring; it is not needed for listeners to hear you. Check your device's mute control too.", "Open Broadcast Studio"),
        new("Troubleshooting", "How do I refresh or restart Studio audio?", "Open the three-dot menu in Monitoring & settings. Refresh devices lists audio devices again and reconnects the microphone and local output. Restart audio stops the active broadcast and restarts Studio audio. Afterwards, re-enable your microphone or select your application and turn capture on as needed, then Go Live again. For the application list alone, use Refresh beside Capture audio from.", "Open Broadcast Studio"),

        new("Events", "How do I find events and say I am attending?", "Open Events and browse the calendar or event list. Select an event to read its details and choose I'm going to attend. My events helps you find events you are attending or have created. If a calendar day shows a count such as 2+, click it to jump to that day’s event list.", "Open Events"),
        new("Events", "How do I create or edit an event?", "Open Events and choose Create event. Fill in the event details, time and location, then save. To change an event you created, open its three-dot menu and choose Edit event. Check the event time before saving.", "Open Events"),
        new("Events", "How do I make an event repeat?", "In the event editor, open Schedule & repeats to set the repeat frequency and when the repeats end. Check the schedule before saving. When editing or cancelling a repeating event, review the available options to choose whether your change affects one event or future events too.", "Open Events"),
        new("Events", "How do I share my event to Discord?", "In the event editor, enable Share my event to the Alpha Channel Discord before saving. This shares the event details, attendance and a Quick View Code. Once the event has been saved with Discord sharing enabled, sharing cannot be turned off.", "Open Events"),
        new("Events", "How do I open an event from a Quick View Code?", "Paste the EV- code into Quick view on Events and choose Open, or type /alpha view followed by the code. Sign in if prompted.", "Open Events", "Open Party Directory"),

        new("Troubleshooting", "I have an issue, what should I try first?", "Stop the active media, check that Alpha Channel is connected, then reload the plugin. If the issue remains, copy diagnostics and include them in a support request on our Discord server."),
        new("Troubleshooting", "What information is safe to share?", "Copy diagnostics includes playback and Studio status, source mode, quality and safe error summaries. It omits passwords, room passwords, stream keys, authentication tokens, private connection details, music file paths and selected application identities. Screenshots and other logs can contain different information, so check them before sharing."),
        new("Troubleshooting", "Why does Alpha Channel say it is disconnected?", "Check your internet connection and wait briefly for Alpha Channel to reconnect. If it remains disconnected, reload the plugin and check whether the service is currently available."),
        new("Troubleshooting", "How do I copy diagnostic information?", "Open Help and select Copy diagnostics at the bottom of the page. Paste the copied information into your support request along with a description of what happened.", "Copy diagnostics"),
        new("Troubleshooting", "What should I include when reporting a problem?", "Explain what you were trying to do, what happened instead, and whether the issue happens repeatedly. Include copied diagnostics and any relevant error messages or screenshots."),
        new("Troubleshooting", "Should I include my Dalamud logs when reporting issues or requesting help?", "Yes, if you are comfortable sharing them. Relevant Dalamud logs can help identify a problem. Send them in a private support ticket to the Alpha Channel team on Discord."),
    ];

    private string helpSearch = string.Empty;
    private string helpCategory = "Getting started";
    private string? helpExpandedQuestion = "How do I set up my TV?";
    private string? helpStatusMessage;

    private void DrawHelpPage()
    {
        using var helpSpacing=ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing,UiVec(8f,6f));
        DrawHelpSearch();
        ImGui.Dummy(UiVec(0f, 4f));

        var width = ImGui.GetContentRegionAvail().X;
        var panelHeight = Math.Max(Ui(180f), ImGui.GetContentRegionAvail().Y - HelpSupportHeight(width) - ImGui.GetStyle().ItemSpacing.Y);
        if (width >= Ui(720f))
        {
            var categoriesWidth = Math.Clamp(width * 0.25f, Ui(220f), Ui(270f));
            DrawHelpCategories(categoriesWidth, panelHeight);
            ImGui.SameLine(0f, Ui(12f));
            DrawHelpQuestions(width - categoriesWidth - Ui(12f), panelHeight);
        }
        else
        {
            DrawHelpCategoryDropdown();
            ImGui.Dummy(UiVec(0f, 4f));
            panelHeight = Math.Max(Ui(180f), ImGui.GetContentRegionAvail().Y - HelpSupportHeight(width) - ImGui.GetStyle().ItemSpacing.Y);
            DrawHelpQuestions(width, panelHeight);
        }

        DrawHelpSupportStrip();
    }

    private void DrawHelpSearch()
    {
        using var colours = ImRaii.PushColor(ImGuiCol.FrameBg, CardBg)
            .Push(ImGuiCol.FrameBgHovered, CardBgHover)
            .Push(ImGuiCol.Border, BorderSubtle);
        using var style = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(10f))
            .Push(ImGuiStyleVar.FrameBorderSize, Ui(1f))
            .Push(ImGuiStyleVar.FramePadding, UiVec(16f, 13f));
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        ImGui.InputTextWithHint("##helpSearch", "Search help...", ref helpSearch, 120);
    }

    private void DrawHelpCategories(float width, float height)
    {
        using var colours = ImRaii.PushColor(ImGuiCol.ChildBg, CardBg).Push(ImGuiCol.Border, BorderSubtle);
        using var style = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(10f))
            .Push(ImGuiStyleVar.ChildBorderSize, Ui(1f))
            .Push(ImGuiStyleVar.WindowPadding, UiVec(12f, 13f));
        if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginChild("##helpCategories", new Vector2(width, height), true)))
        {
            DrawSocialHeading("Categories");
            ImGui.Dummy(UiVec(0f, 6f));
            foreach (var category in HelpCategories)
            {
                DrawHelpCategoryRow(category);
            }
        }
        ImGui.EndChild();
    }

    private void DrawHelpCategoryRow(HelpCategory category)
    {
        var selected = string.IsNullOrWhiteSpace(helpSearch) && helpCategory == category.Name;
        var origin = ImGui.GetCursorScreenPos();
        var size = new Vector2(ImGui.GetContentRegionAvail().X, Ui(32f));
        ImGui.PushID(category.Name);
        var clicked = ImGui.InvisibleButton("##category", size);
        var hovered = ImGui.IsItemHovered();
        ImGui.PopID();
        var drawList = ImGui.GetWindowDrawList();
        if (selected || hovered)
        {
            drawList.AddRectFilled(origin, origin + size, ImGui.GetColorU32(selected ? Accent : CardBgHover), Ui(8f));
        }
        var colour = selected ? Vector4.One : MutedText;
        UiFonts.DrawIcon(drawList, ImGui.GetFontSize(), origin + UiVec(11f, 7f), ImGui.GetColorU32(colour), category.Icon.ToIconString());
        drawList.AddText(origin + UiVec(39f, 7f), ImGui.GetColorU32(colour), category.Name);
        if (clicked)
        {
            helpSearch = string.Empty;
            helpCategory = category.Name;
            helpExpandedQuestion = HelpQuestions.FirstOrDefault(question => question.Category == category.Name)?.Question;
        }
    }

    private void DrawHelpCategoryDropdown()
    {
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo("##helpCategory", helpCategory)))
        {
            foreach (var category in HelpCategories)
            {
                if (ImGui.Selectable(category.Name, helpCategory == category.Name))
                {
                    helpSearch = string.Empty;
                    helpCategory = category.Name;
                    helpExpandedQuestion = HelpQuestions.FirstOrDefault(question => question.Category == category.Name)?.Question;
                }
            }
            ImGui.EndCombo();
        }
    }

    private void DrawHelpQuestions(float width, float height)
    {
        using var colours = ImRaii.PushColor(ImGuiCol.ChildBg, CardBg).Push(ImGuiCol.Border, BorderSubtle);
        using var style = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(10f))
            .Push(ImGuiStyleVar.ChildBorderSize, Ui(1f))
            .Push(ImGuiStyleVar.WindowPadding, UiVec(16f, 14f));
        if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginChild("##helpQuestions", new Vector2(width, height), true)))
        {
            var searching = !string.IsNullOrWhiteSpace(helpSearch);
            using var uiFontScale49681=UiFonts.PushRelative(1.28f);
            ImGui.TextColored(Hex(0xE2E7EF), searching ? "Search results" : helpCategory);
            uiFontScale49681.Dispose();
            using (ImRaii.TextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X))
                DrawSocialHelper(searching ? $"Answers matching ‘{helpSearch.Trim()}’." : HelpCategoryDescription(helpCategory));
            ImGui.Dummy(UiVec(0f, 5f));

            if (!searching && helpCategory == "Allowance calculator")
            {
                DrawHelpAllowanceCalculator();
            }
            else
            {
                var query = helpSearch.Trim();
                var questions = HelpQuestions.Where(question =>
                    searching
                        ? question.Question.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                          question.Answer.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                          question.Category.Contains(query, StringComparison.OrdinalIgnoreCase)
                        : question.Category == helpCategory).ToArray();

                if (questions.Length == 0)
                {
                    DrawSocialHelper("No help topics matched your search.");
                }
                foreach (var question in questions)
                {
                    DrawHelpQuestion(question);
                    ImGui.Dummy(UiVec(0f, 3f));
                }
            }
        }
        ImGui.EndChild();
    }

    private void DrawHelpQuestion(HelpQuestion question)
    {
        var expanded = helpExpandedQuestion == question.Question;
        var width = ImGui.GetContentRegionAvail().X;
        var origin = ImGui.GetCursorScreenPos();
        var titleWidth = Math.Max(Ui(60f), width - Ui(56f));
        var titleHeight = ImGui.CalcTextSize(question.Question, false, titleWidth).Y;
        var headerSize = new Vector2(width, Math.Max(Ui(46f), titleHeight + Ui(28f)));
        using var id = ImRaii.PushId(question.Question);
        var clicked = ImGui.InvisibleButton("##question", headerSize);
        var hovered = ImGui.IsItemHovered();
        var afterHeader = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(origin, origin + headerSize, ImGui.GetColorU32(hovered ? CardBgHover : FrameBg), Ui(8f), expanded ? ImDrawFlags.RoundCornersTop : ImDrawFlags.RoundCornersAll);
        drawList.AddRect(origin, origin + headerSize, ImGui.GetColorU32(BorderSubtle), Ui(8f), expanded ? ImDrawFlags.RoundCornersTop : ImDrawFlags.RoundCornersAll);
        ImGui.SetCursorScreenPos(origin + new Vector2(Ui(14f), (headerSize.Y - titleHeight) / 2));
        using (ImRaii.TextWrapPos(ImGui.GetCursorPosX() + titleWidth)) ImGui.TextColored(Vector4.One, question.Question);
        ImGui.SetCursorScreenPos(afterHeader);
        var chevron = (expanded ? FontAwesomeIcon.ChevronUp : FontAwesomeIcon.ChevronDown).ToIconString();
        UiFonts.DrawIcon(drawList, ImGui.GetFontSize(), origin + new Vector2(width - Ui(28f), (headerSize.Y - ImGui.GetFontSize()) / 2), ImGui.GetColorU32(MutedText), chevron);
        if (clicked)
        {
            helpExpandedQuestion = expanded ? null : question.Question;
            expanded = !expanded;
        }
        if (!expanded)
        {
            return;
        }

        using var bodyColours = ImRaii.PushColor(ImGuiCol.ChildBg, Vector4.Lerp(CardBg,FrameBg,.55f)).Push(ImGuiCol.Border, BorderSubtle);
        using var bodyStyle = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(8f))
            .Push(ImGuiStyleVar.ChildBorderSize, Ui(1f))
            .Push(ImGuiStyleVar.WindowPadding, UiVec(14f, 13f));
        var answerWrapWidth = Math.Max(Ui(120f), width - Ui(32f));
        float answerHeight;
        using(UiFonts.PushRelative(.90f)) answerHeight=ImGui.CalcTextSize(question.Answer,false,answerWrapWidth).Y;
        var buttonWidth = Math.Min(Ui(210f), width - Ui(28f));
        var buttonSpacing = ImGui.GetStyle().ItemSpacing;
        var buttonColumns = Math.Max(1, (int)((width - Ui(28f) + buttonSpacing.X) / (buttonWidth + buttonSpacing.X)));
        var buttonRows = (question.Actions.Length + buttonColumns - 1) / buttonColumns;
        var bodyHeight = answerHeight + Ui(26f) + buttonSpacing.Y +
            (buttonRows == 0 ? 0f : Ui(4f) + buttonSpacing.Y + buttonRows * (Ui(32f) + buttonSpacing.Y));
        if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginChild("##answer", new Vector2(width, bodyHeight), true, ImGuiWindowFlags.NoScrollbar)))
        {
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
            DrawSocialHelper(question.Answer);
            ImGui.PopTextWrapPos();
            if (question.Actions.Length > 0)
            {
                ImGui.Dummy(UiVec(0f, 4f));
                using var buttonColours = ImRaii.PushColor(ImGuiCol.Button, Accent).Push(ImGuiCol.ButtonHovered, AccentHover);
                for (var index = 0; index < question.Actions.Length; index++)
                {
                    if (index % buttonColumns != 0) ImGui.SameLine();
                    var action = question.Actions[index];
                    if (ImGui.Button(HelpActionLabel(action), new Vector2(buttonWidth, Ui(32f)))) NavigateFromHelp(action);
                }
            }
        }
        ImGui.EndChild();
    }

    private static string HelpActionLabel(string action) => action.Replace(" Settings", " settings").Replace("Add Media", "Add media").Replace("Party Now Playing", "Party now playing").Replace("Party Chat", "Party chat");

    private void NavigateFromHelp(string action)
    {
        switch (action)
        {
            case "Open Screen Settings": currentPage = HomePage.Screen; break;
            case "Open Watch Party":
                currentPage = HomePage.WatchAlong;
                partyPanelTab = PartyPanelTab.Watching;
                break;
            case "Open Party Now Playing":
                currentPage = HomePage.WatchAlong;
                partyPanelTab = PartyPanelTab.NowPlaying;
                break;
            case "Open Party Chat":
                currentPage = HomePage.WatchAlong;
                partyPanelTab = PartyPanelTab.Chat;
                break;
            case "Open Party Directory": currentPage = HomePage.PartyDirectory; break;
            case "Open Friends": currentPage = HomePage.Friends; break;
            case "Open My Profile":
                if (CurrentSession is { } profileSession)
                    OpenProfilePage(profileSession, profileSession.AccountId, profileSession.DisplayName);
                else currentPage = HomePage.Friends;
                break;
            case "Open Alpha Chat": SelectChatsHubTab(0); break;
            case "Open Messages": SelectChatsHubTab(1); break;
            case "Open Groups": SelectChatsHubTab(2); break;
            case "Open Discover": SelectChatsHubTab(3); break;
            case "Open Events": currentPage = HomePage.Events; break;
            case "Open Dailymotion": currentPage = HomePage.Dailymotion; break;
            case "Open Internet Archive": currentPage = HomePage.InternetArchive; break;
            case "Open Settings": currentPage = HomePage.Settings; break;
            case "Open Profile Settings":
                currentPage = HomePage.Settings;
                settingsTab = SettingsTab.Profile;
                break;
            case "Open YouTube Settings":
                currentPage = HomePage.Settings;
                settingsTab = SettingsTab.YouTube;
                break;
            case "Open Add Media":
                currentPage = HomePage.Player;
                activePlayerDrawer = PlayerDrawer.PlayVideo;
                showingAddMediaSources = true;
                break;
            case "Open Local Video":
                currentPage = HomePage.Player;
                activePlayerDrawer = PlayerDrawer.PlayVideo;
                OpenAddMediaSource(7);
                break;
            case "Open Media Queue":
                currentPage = HomePage.Player;
                activePlayerDrawer = PlayerDrawer.Queue;
                break;
            case "Open Browser": currentPage = HomePage.Browser; break;
            case "Open Retro Games": currentPage = HomePage.PlaySnes; break;
            case "Open The Arcade": currentPage = HomePage.Arcade; break;
            case "Open Canvas": currentPage = HomePage.Whiteboard; break;
            case "Open Poker": currentPage = HomePage.Poker; break;
            case "Open Slot Machine": currentPage = HomePage.SlotMachine; break;
            case "Open Bingo": currentPage = HomePage.Bingo; break;
            case "Open Air Force EX": currentPage = HomePage.SkyRail; break;
            case "Open Pictionary": currentPage = HomePage.Pictionary; break;
            case "Open Pictionary Teams": currentPage = HomePage.PictionaryTeams; break;
            case "Open Quiz": currentPage = HomePage.Quiz; break;
            case "Open Roulette": currentPage = HomePage.Roulette; break;
            case "Open Choco RaceBets": currentPage = HomePage.RacePreview; break;
            case "Copy diagnostics":
                ImGui.SetClipboardText(BuildHelpDiagnostics());
                helpStatusMessage = "Diagnostics copied. Passwords, stream keys and file paths were excluded.";
                break;
            case "Open Broadcast Studio":
                currentPage = HomePage.Music;
                musicTab = MusicTab.Studio;
                break;
            case "Open DJ Live":
                currentPage = HomePage.Music;
                musicTab = MusicTab.Dj;
                break;
            case "Open Radio Stations":
                currentPage = HomePage.Music;
                musicTab = MusicTab.Radio;
                break;
            case "Open Scene":
                currentPage = HomePage.Music;
                musicTab = MusicTab.Scene;
                break;
        }
    }

    private float HelpSupportHeight(float width)
    {
        var compact = width < Ui(880f);
        var message = helpStatusMessage ?? "Join the community or copy diagnostic information for a support request.";
        var textWidth = Math.Max(Ui(100f), width - Ui(compact ? 36f : 390f));
        float textHeight;
        using(UiFonts.PushRelative(.90f)) textHeight=ImGui.CalcTextSize(message,false,textWidth).Y;
        var compactHeight = textHeight + ImGui.GetTextLineHeight() * 1.1f + Ui(66f) + ImGui.GetStyle().ItemSpacing.Y * 3;
        return compact ? Math.Max(Ui(106f), compactHeight) : Math.Max(Ui(80f), textHeight + Ui(52f));
    }

    private void DrawHelpSupportStrip()
    {
        var width = ImGui.GetContentRegionAvail().X;
        var compact = width < Ui(880f);
        var message = helpStatusMessage ?? "Join the community or copy diagnostic information for a support request.";
        var textWidth = Math.Max(Ui(100f), width - Ui(compact ? 36f : 390f));
        var height = HelpSupportHeight(width);
        using var colours = ImRaii.PushColor(ImGuiCol.ChildBg, CardBg).Push(ImGuiCol.Border, BorderSubtle);
        using var style = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(10f))
            .Push(ImGuiStyleVar.ChildBorderSize, Ui(1f))
            .Push(ImGuiStyleVar.WindowPadding, UiVec(18f, 13f));
        if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginChild("##helpSupport", new Vector2(width, height), true, ImGuiWindowFlags.NoScrollbar)))
        {
            using var uiFontScale60319=UiFonts.PushRelative(1.1f);
            ImGui.TextColored(Hex(0xE2E7EF), "Still need help?");
            uiFontScale60319.Dispose();
            using (ImRaii.TextWrapPos(ImGui.GetCursorPosX() + textWidth)) DrawSocialHelper(message);

            var buttonWidth = Math.Min(Ui(160f), (width - Ui(46f)) / 2);
            if (compact) ImGui.Dummy(UiVec(0f, 2f));
            else ImGui.SetCursorPos(new Vector2(width - buttonWidth * 2f - Ui(28f), Ui(22f)));
            using (ImRaii.PushColor(ImGuiCol.Button, Accent).Push(ImGuiCol.ButtonHovered, AccentHover))
            {
                if (ImGui.Button("Open Discord", new Vector2(buttonWidth, Ui(38f))))
                {
                    OpenHelpDiscord();
                }
            }
            ImGui.SameLine(0f, Ui(10f));
            if (ImGui.Button("Copy diagnostics", new Vector2(buttonWidth, Ui(38f))))
            {
                ImGui.SetClipboardText(BuildHelpDiagnostics());
                helpStatusMessage = "Diagnostics copied. Passwords, stream keys and file paths were excluded.";
            }
        }
        ImGui.EndChild();
    }

    private static string HelpCategoryDescription(string category) => category switch
    {
        "Getting started" => "Everything you need to begin using Alpha Channel.",
        "TV & screen" => "Position, resize, synchronize, and control your TV.",
        "Media & playback" => "Playing media, controlling playback, and managing the queue.",
        "YouTube" => "Browse videos, connect your account and discover popular videos.",
        "Arcade activities" => "Start games, learn the rules and play together.",
        "Watch Parties" => "Creating rooms, joining friends, and staying synchronized.",
        "Broadcasting" => "Quality options, weekly allowances, and live sharing.",
        "Browser" => "Using and controlling the embedded browser.",
        "Retro Games" => "Adding games, configuring controls, and broadcasting gameplay.",
        "Music & DJ" => "Music libraries, application audio, microphone mixing, DJ streams and scenes.",
        "Canvas" => "Solo and Team drawing modes.",
        "Friends & social" => "Friends, private messages, groups, Alpha Chat and reactions.",
        "Events" => "Find events, manage attendance, create events and share them.",
        "Patreon & allowances" => "Membership tiers, qualities, and broadcast credits.",
        "Allowance calculator" => "See roughly how far your weekly allowance will go.",
        _ => "Common fixes and information for support requests.",
    };

    private const string DiscordInviteUrl = "https://discord.gg/BQK87eJ3EV";

    private void OpenHelpDiscord()
    {
        try
        {
            Process.Start(new ProcessStartInfo(DiscordInviteUrl) { UseShellExecute = true });
        }
        catch (Exception)
        {
            helpStatusMessage = "Could not open Discord in your browser.";
            AepLog.Warning("[Help] Failed to open Discord.");
        }
    }

    private string BuildHelpDiagnostics()
    {
        var assemblyVersion = typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "Unknown";
        var builder = new StringBuilder();
        builder.AppendLine("Alpha Channel diagnostics");
        builder.AppendLine($"Plugin version: {assemblyVersion}");
        builder.AppendLine($"Realtime connection: {stream.ConnectionStatusText}");
        builder.AppendLine($"Watch Party mode: {stream.Mode}");
        builder.AppendLine($"Party viewers: {stream.Roster.Length}");
        builder.AppendLine($"TV spawned: {screenController.Engine.IsActive}");
        builder.AppendLine($"Playback state: {video.State}");
        builder.AppendLine(video.BroadcastDiagnostics.ToSanitizedSummary());
        builder.AppendLine(PlaybackRenderDiagnostics.LivePlaybackReport());
        builder.AppendLine($"Active page before Help: {helpReturnPage}");
        var settings = studioInitialized ? studioSettings : Plugin.Cfg.StudioSettings ?? new();
        var studio = StudioState;
        builder.AppendLine($"Studio helper: {(studioClient == null ? "Not started" : studio.Ready ? "Ready" : "Starting or unavailable")}");
        builder.AppendLine($"Studio source: {(settings.MusicSource == AlphaChannel.BroadcastStudio.StudioMusicSource.Application ? "Application audio" : "Song queue")}");
        builder.AppendLine($"Studio quality: {StudioQualityLabel(settings.Bitrate)}");
        builder.AppendLine($"Studio broadcast: {studio.BroadcastState}");
        builder.AppendLine($"Studio application capture: {(studio.ApplicationCapturing ? "Active" : settings.ApplicationCaptureEnabled ? "Requested" : "Off")}");
        builder.AppendLine($"Studio application capture supported: {(studioClient == null ? "Not checked" : studio.ApplicationCaptureSupported ? "Yes" : "No")}");
        builder.AppendLine($"Studio microphone enabled: {settings.MicEnabled}; active: {studio.MicActive}; push-to-talk: {settings.PushToTalk}");
        builder.AppendLine($"Studio error: {HelpStudioErrorSummary(studio.Error)}");
        builder.AppendLine($"Studio device error: {HelpStudioErrorSummary(studio.DeviceError)}");
        builder.AppendLine($"Studio capture error: {HelpStudioErrorSummary(studio.ApplicationError)}");
        builder.AppendLine("Authentication, passwords, stream keys, file paths and application identities are excluded.");
        return builder.ToString();
    }

    // Export fixed summaries rather than arbitrary error strings: future native
    // errors can contain paths, addresses or private connection details.
    private static string HelpStudioErrorSummary(string? error) => error switch
    {
        null or "" => "None",
        "Application capture requires Windows build 20348 or later." => "Windows version does not support application capture",
        "Application closed. Refresh and choose a running application." => "Source application closed",
        "Refresh and choose a running application to capture." or
        "Application audio could not start. Refresh and choose a running application." => "Application capture could not start",
        "Application capture stopped. Turn capture on to retry." or
        "Application audio disconnected. Turn capture off and on to retry." => "Application capture stopped or disconnected",
        "Microphone disconnected. Choose an available microphone and refresh devices." or
        "Microphone could not start. Choose another device and refresh devices." => "Microphone unavailable",
        "Local monitoring disconnected. Choose an available output device and refresh devices." or
        "Local monitoring could not start. Choose an available output device and refresh devices." => "Local output unavailable",
        "Audio devices could not be listed. Press Refresh devices to retry." => "Device enumeration failed",
        "Could not read the song's metadata." or "The song could not be decoded. Try another audio file." or
        "Music decoding stopped unexpectedly." => "Song decoding failed",
        "Studio audio stopped. Press Restart audio to retry." => "Studio helper stopped",
        "Icecast rejected the connection details." => "Broadcast authentication rejected",
        "This mount is already broadcasting. Stop your other DJ software first." => "Another broadcast is using the connection",
        "Broadcast connection interrupted. Reconnecting…" => "Broadcast reconnecting",
        "Could not connect to Icecast. Check your connection and try Go Live again." => "Broadcast connection failed",
        _ => "Error reported; check the visible Studio message",
    };
}
