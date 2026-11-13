namespace AlphaChannel.Plugin;

// Published global-service worlds, verified against The Lodestone on 2026-10-14:
// https://na.finalfantasyxiv.com/lodestone/worldstatus/
// The client World sheet also contains unused/test entries; IsPublic alone is insufficient.
// Keep this local so opening/searching the picker never makes a network request.
internal static class FfxivEventWorlds
{
    internal sealed record World(string Name,string Centre,string Region);
    internal static readonly IReadOnlyList<World> All=Array.AsReadOnly(new[]
    {
        Group("North America","Aether","Adamantoise","Cactuar","Faerie","Gilgamesh","Jenova","Midgardsormr","Sargatanas","Siren"),
        Group("North America","Crystal","Balmung","Brynhildr","Coeurl","Diabolos","Goblin","Malboro","Mateus","Zalera"),
        Group("North America","Dynamis","Cuchulainn","Golem","Halicarnassus","Kraken","Maduin","Marilith","Rafflesia","Seraph"),
        Group("North America","Primal","Behemoth","Excalibur","Exodus","Famfrit","Hyperion","Lamia","Leviathan","Ultros"),
        Group("Europe","Chaos","Cerberus","Louisoix","Moogle","Omega","Phantom","Ragnarok","Sagittarius","Spriggan"),
        Group("Europe","Light","Alpha","Lich","Odin","Phoenix","Raiden","Shiva","Twintania","Zodiark"),
        Group("Oceania","Materia","Bismarck","Ravana","Sephirot","Sophia","Zurvan"),
        Group("Japan","Elemental","Aegis","Atomos","Carbuncle","Garuda","Gungnir","Kujata","Tonberry","Typhon"),
        Group("Japan","Gaia","Alexander","Bahamut","Durandal","Fenrir","Ifrit","Ridill","Tiamat","Ultima"),
        Group("Japan","Mana","Anima","Asura","Chocobo","Hades","Ixion","Masamune","Pandaemonium","Titan"),
        Group("Japan","Meteor","Belias","Mandragora","Ramuh","Shinryu","Unicorn","Valefor","Yojimbo","Zeromus")
    }.SelectMany(g=>g).OrderBy(w=>w.Name,StringComparer.Ordinal).ToArray());
    private static World[] Group(string region,string centre,params string[] names)=>names.Select(name=>new World(name,centre,region)).ToArray();
    internal static World? Find(string name)=>All.FirstOrDefault(w=>w.Name.Equals(name,StringComparison.OrdinalIgnoreCase));
}
