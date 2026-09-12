using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn
{
    /// <summary>
    /// POI 资产仓储管理器。
    /// 负责 GlobalPoiAssets 的加载、重载以及环境条件过滤（季节、天气、时间窗口）。
    /// </summary>
    public class PoiRepository
    {
        public const string POI_ASSET_KEY = "ValleytalkReborn/GlobalPoiAssets";

        private readonly IModHelper _helper;
        private Dictionary<string, PoiAsset> _poiAssets = new(StringComparer.OrdinalIgnoreCase);
        private bool _loaded = false;

        public PoiRepository(IModHelper helper)
        {
            _helper = helper ?? throw new ArgumentNullException(nameof(helper));
        }

        public bool IsLoaded => _loaded;
        public int TotalPoiCount => _poiAssets.Count;

        /// <summary>加载 POI 资产。</summary>
        public void LoadAssets()
        {
            try
            {
                _poiAssets = _helper.GameContent.Load<Dictionary<string, PoiAsset>>(POI_ASSET_KEY)
                    ?? new(StringComparer.OrdinalIgnoreCase);

                _loaded = true;
                ModEntry.SMonitor?.Log(
                    $"[PoiRepo] Loaded {_poiAssets.Count} POIs.",
                    LogLevel.Info);
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[PoiRepo] Asset load failed: {ex.Message}", LogLevel.Error);
                _poiAssets = new(StringComparer.OrdinalIgnoreCase);
                _loaded = false;
            }
        }

        /// <summary>热重载资产。</summary>
        public void ReloadAssets()
        {
            _loaded = false;
            LoadAssets();
        }

        /// <summary>根据当前季节/天气/时间过滤出可用的 POI。</summary>
        public Dictionary<string, PoiAsset> GetFilteredPois(string npcName)
        {
            if (!_loaded)
                LoadAssets();

            string season  = Game1.season.ToString().ToLowerInvariant();
            string weather = GetCurrentWeatherString();
            int currentTime = Game1.timeOfDay;

            var result = new Dictionary<string, PoiAsset>(StringComparer.OrdinalIgnoreCase);
            if (_poiAssets == null) return result;

            foreach (var (poiId, asset) in _poiAssets)
            {
                if (asset == null) continue;
                var cond = asset.Conditions ?? new PoiConditions();

                // 季节过滤
                if (cond.AllowedSeasons?.Count > 0 &&
                    !cond.AllowedSeasons.Any(s => s.Equals(season, StringComparison.OrdinalIgnoreCase)))
                    continue;

                // 天气过滤
                if (cond.AllowedWeather?.Count > 0 &&
                    !cond.AllowedWeather.Any(w => w.Equals(weather, StringComparison.OrdinalIgnoreCase)))
                    continue;

                // 时间窗口过滤：晚于当前时间且未到晚上 20:00 回家打烊点
                if (cond.TimeRange?.Count == 2)
                {
                    if (cond.TimeRange[1] <= currentTime || cond.TimeRange[0] >= 2000)
                        continue;
                }

                result[poiId] = asset;
            }

            ModEntry.SMonitor?.Log(
                $"[PoiRepo] FilterLegalPois({npcName}): {result.Count}/{_poiAssets.Count} POIs pass " +
                $"(season={season}, weather={weather})",
                LogLevel.Debug);

            return result;
        }

        /// <summary>获取直接 POI 对象定义（不带过滤）。</summary>
        public PoiAsset GetPoiById(string poiId)
        {
            if (!_loaded) LoadAssets();
            return _poiAssets.TryGetValue(poiId, out var asset) ? asset : null;
        }

        private static string GetCurrentWeatherString()
        {
            if (Game1.isLightning) return "Stormy";
            if (Game1.isRaining)   return "Rainy";
            if (Game1.isSnowing)   return "Snowy";
            return "Sunny";
        }
    }
}
