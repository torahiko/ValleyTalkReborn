using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using ValleytalkReborn;
using ValleytalkReborn.Services;

namespace ValleytalkReborn.Cutscene.Perception
{
    /// <summary>
    /// 虚拟导演编剧上下文
    /// </summary>
    public sealed class CutsceneContext
    {
        public string LocationName { get; set; } = string.Empty;
        public string LocationFriendlyName { get; set; } = string.Empty;
        public string Season { get; set; } = string.Empty;
        public int DayOfMonth { get; set; }
        public string Weather { get; set; } = string.Empty;
        public string FestivalName { get; set; }
        public int TimeOfDay { get; set; }
        public Vector2 FarmerTile { get; set; }
        public List<ActorProfile> Actors { get; set; } = new();
        public List<PoiInfo> NearbyPois { get; set; } = new();
    }

    /// <summary>
    /// 参演角色画像
    /// </summary>
    public sealed class ActorProfile
    {
        public string Name { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public Vector2 CurrentTile { get; set; }
        public int FacingDirection { get; set; }
        public int FriendshipPoints { get; set; }
        public int HeartLevel { get; set; }
        public string PersonalitySummary { get; set; } = string.Empty;
    }

    /// <summary>
    /// 地标兴趣点
    /// </summary>
    public sealed class PoiInfo
    {
        public string Description { get; set; } = string.Empty;
        public Vector2 Tile { get; set; }
    }

    /// <summary>
    /// 运行时上下文收集器：负责为大模型收集当下真实的地标、NPC 人设与时间天气
    /// </summary>
    public static class CutsceneContextCollector
    {
        public static CutsceneContext Collect(List<NPC> actors, GameLocation location)
        {
            var ctx = new CutsceneContext();

            if (location == null) return ctx;

            // 1. 基础环境
            ctx.LocationName = location.NameOrUniqueName ?? string.Empty;
            ctx.LocationFriendlyName = EnvironmentScanner.GetLocationFriendlyName(ctx.LocationName);
            ctx.Season = Game1.currentSeason ?? "spring";
            ctx.DayOfMonth = Game1.dayOfMonth;
            ctx.TimeOfDay = Game1.timeOfDay;
            ctx.Weather = Game1.isRaining ? "Raining" : (Game1.isSnowing ? "Snowing" : "Sunny");
            ctx.FestivalName = EnvironmentScanner.GetTodayFestivalName();
            ctx.FarmerTile = Game1.player?.Tile ?? Vector2.Zero;

            // 2. 参演角色画像
            if (actors != null)
            {
                foreach (var npc in actors)
                {
                    if (npc == null) continue;

                    var profile = new ActorProfile
                    {
                        Name = npc.Name,
                        DisplayName = npc.displayName ?? npc.Name,
                        CurrentTile = npc.Tile,
                        FacingDirection = npc.FacingDirection
                    };

                    // 好感度
                    if (Game1.player?.friendshipData != null &&
                        Game1.player.friendshipData.TryGetValue(npc.Name, out var friendship))
                    {
                        profile.FriendshipPoints = friendship.Points;
                        profile.HeartLevel = friendship.Points / 250;
                    }

                    // 人设概述（从 BioStorage 提取）
                    try
                    {
                        if (ModEntry.BioStorage != null)
                        {
                            var bio = ModEntry.BioStorage.LoadEditableBio(npc.Name);
                            if (bio != null)
                            {
                                string traits = bio.Traits != null
                                    ? string.Join("、", bio.Traits.Keys)
                                    : string.Empty;
                                profile.PersonalitySummary = !string.IsNullOrWhiteSpace(traits)
                                    ? traits
                                    : (bio.Biography?.Substring(0, Math.Min(120, bio.Biography.Length)) ?? string.Empty);
                            }
                        }
                    }
                    catch
                    {
                        // 忽略人设提取异常
                    }

                    ctx.Actors.Add(profile);
                }
            }

            // 3. 附近可用可站立 POI 坐标扫描（严格红线 #2 保证可通行）
            try
            {
                var anchorNpc = actors?.FirstOrDefault();
                var rawAssets = EnvironmentScanner.ScanNearbyObjectsWithTiles(anchorNpc, radiusTiles: 15, maxItems: 10);

                foreach (var (desc, tile) in rawAssets)
                {
                    // 将 POI 换算为周边合法通行格
                    var safeTile = MovementPathfinding.FindNearestWalkableTile(location, tile, anchorNpc, radius: 2);
                    ctx.NearbyPois.Add(new PoiInfo
                    {
                        Description = desc,
                        Tile = safeTile
                    });
                }
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[CutsceneContextCollector] POI scan failed: {ex.Message}", StardewModdingAPI.LogLevel.Warn);
            }

            return ctx;
        }
    }
}
