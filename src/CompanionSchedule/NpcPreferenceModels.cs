using System.Collections.Generic;
using Newtonsoft.Json;

namespace ValleytalkReborn
{
    public class NpcPoiPreference
    {
        [JsonProperty("PoiId")]  public string PoiId  { get; set; } = "";
        [JsonProperty("Weight")] public int    Weight { get; set; } = 50;
    }

    public class NpcPreference
    {
        [JsonProperty("PreferredPois")] public List<NpcPoiPreference> PreferredPois { get; set; } = new();
    }
}