using System.Collections.Generic;
using Newtonsoft.Json;

namespace ValleytalkReborn
{
    public class PoiConditions
    {
        [JsonProperty("AllowedSeasons")] public List<string> AllowedSeasons { get; set; } = new();
        [JsonProperty("AllowedWeather")]  public List<string> AllowedWeather  { get; set; } = new();
        [JsonProperty("TimeRange")]       public List<int>    TimeRange       { get; set; } = new();
    }

    public class PoiTile
    {
        [JsonProperty("X")] public int X { get; set; }
        [JsonProperty("Y")] public int Y { get; set; }
    }

    public class PoiAsset
    {
        [JsonProperty("MapName")]           public string        MapName           { get; set; } = "";
        [JsonProperty("TargetTile")]        public PoiTile       TargetTile        { get; set; } = new();
        [JsonProperty("Conditions")]        public PoiConditions Conditions        { get; set; } = new();
        [JsonProperty("CsharpAnimation")]   public string        CsharpAnimation   { get; set; } = "";
        [JsonProperty("DescriptionForLLM")] public string        DescriptionForLLM { get; set; } = "";
        [JsonProperty("DescriptionForLLM_Zh")] public string        DescriptionForLLM_Zh { get; set; } = "";

        /// <summary>该地点建议停留的游戏分钟数。未在 JSON 里配置时默认为 90 分钟。</summary>
        [JsonProperty("StayMinutes")]       public int           StayMinutes       { get; set; } = 90;
    }
}