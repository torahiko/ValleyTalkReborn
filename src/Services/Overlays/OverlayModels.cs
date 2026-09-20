#nullable enable
using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace ValleytalkReborn.Services.Overlays;

internal sealed class DateLocationOverlayFile
{
    [JsonProperty("Entries")] public Dictionary<string, DateLocationInfo> Entries = new(StringComparer.OrdinalIgnoreCase);
    [JsonProperty("RemovedLocationIds")] public List<string> RemovedLocationIds = new();
}

internal sealed class NpcRelationOverlayFile
{
    [JsonProperty("Relations")] public List<NpcRelationEntry> Relations = new();
}

internal sealed class CustomFestivalEntry
{
    [JsonProperty("Names")]        public Dictionary<string, string> Names        = new();  // 语言键 "zh"/"en"
    [JsonProperty("Descriptions")] public Dictionary<string, string> Descriptions = new();  // 语言键 "zh"/"en"
    [JsonProperty("IsCustomDate")] public bool IsCustomDate;
}

internal sealed class WorldSummaryOverlayFile
{
    [JsonProperty("LocationDescriptions")] public Dictionary<string, string> LocationDescriptions = new(StringComparer.OrdinalIgnoreCase);
    [JsonProperty("RemovedLocationDescriptionIds")] public List<string> RemovedLocationDescriptionIds = new();
    [JsonProperty("Festivals")] public Dictionary<string, CustomFestivalEntry> Festivals = new(StringComparer.OrdinalIgnoreCase);  // Key 形如 "spring13"
    [JsonProperty("RemovedFestivalKeys")] public List<string> RemovedFestivalKeys = new();
}

internal sealed class PoiOverlayFile
{
    [JsonProperty("NpcPreferences")] public Dictionary<string, NpcPreference> NpcPreferences = new(StringComparer.OrdinalIgnoreCase);
    [JsonProperty("RemovedNpcNames")] public List<string> RemovedNpcNames = new();
    [JsonProperty("CustomPois")] public Dictionary<string, PoiAsset> CustomPois = new(StringComparer.OrdinalIgnoreCase);
    [JsonProperty("RemovedCustomPoiIds")] public List<string> RemovedCustomPoiIds = new();
}
