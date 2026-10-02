#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StardewModdingAPI;
using StardewValley;
using ValleytalkReborn.Services;

namespace ValleytalkReborn.Cutscene.Storage
{
    /// <summary>
    /// 剧本归档与持久化回放服务：
    /// 负责即兴剧本在本地的安全原子落盘、历史索引读取以及就地重播
    /// </summary>
    public static class CutsceneStorageService
    {
        /// <summary>
        /// 获取剧本存储目录（优先使用当前存档的本地目录，未载档时回退至全局目录）
        /// </summary>
        public static string GetStorageDirectory()
        {
            string baseDir = StorageLayout.LocalBaseDir ?? StorageLayout.GlobalBaseDir;
            return Path.Combine(baseDir, "cutscenes");
        }

        /// <summary>
        /// 归档一个新生成的剧本
        /// </summary>
        public static bool Save(ArchivedCutscene cutscene)
        {
            if (cutscene == null || string.IsNullOrWhiteSpace(cutscene.RawJson))
                return false;

            try
            {
                string dir = GetStorageDirectory();
                string filePath = Path.Combine(dir, $"{cutscene.Id}.json");
                StorageJson.Write(filePath, cutscene);
                ModEntry.SMonitor?.Log($"[CutsceneStorage] Cutscene archived: '{cutscene.Title}' ({cutscene.Id})", LogLevel.Info);
                return true;
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[CutsceneStorage] Failed to save cutscene: {ex.Message}", LogLevel.Warn);
                return false;
            }
        }

        /// <summary>
        /// 加载所有归档剧本（按创建时间倒序排列）
        /// </summary>
        public static List<ArchivedCutscene> LoadAll()
        {
            var list = new List<ArchivedCutscene>();
            try
            {
                string dir = GetStorageDirectory();
                if (!Directory.Exists(dir))
                    return list;

                var files = Directory.GetFiles(dir, "*.json");
                foreach (var file in files)
                {
                    try
                    {
                        var item = StorageJson.Read<ArchivedCutscene>(file);
                        if (item != null && !string.IsNullOrWhiteSpace(item.RawJson))
                        {
                            list.Add(item);
                        }
                    }
                    catch (Exception ex)
                    {
                        ModEntry.SMonitor?.Log($"[CutsceneStorage] Error reading cutscene file {Path.GetFileName(file)}: {ex.Message}", LogLevel.Debug);
                    }
                }

                list.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[CutsceneStorage] Failed to load cutscenes: {ex.Message}", LogLevel.Warn);
            }
            return list;
        }

        /// <summary>
        /// 删除指定 ID 的归档剧本
        /// </summary>
        public static bool Delete(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;

            try
            {
                string dir = GetStorageDirectory();
                string filePath = Path.Combine(dir, $"{id}.json");
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    ModEntry.SMonitor?.Log($"[CutsceneStorage] Deleted cutscene {id}", LogLevel.Info);
                    return true;
                }
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[CutsceneStorage] Error deleting cutscene {id}: {ex.Message}", LogLevel.Warn);
            }
            return false;
        }

        /// <summary>
        /// 尝试重播归档剧本
        /// </summary>
        public static bool Replay(ArchivedCutscene cutscene, out string errorMessage)
        {
            errorMessage = string.Empty;
            if (cutscene == null || string.IsNullOrWhiteSpace(cutscene.RawJson))
            {
                errorMessage = "剧本数据为空";
                return false;
            }

            if (!Context.IsWorldReady || Game1.player?.currentLocation == null)
            {
                errorMessage = "游戏世界未就绪";
                return false;
            }

            if (VirtualDirector.Instance.IsActive)
            {
                errorMessage = "当前已有过场正在演出中";
                return false;
            }

            if (!Context.IsPlayerFree)
            {
                errorMessage = "玩家当前正处于事件或菜单中，无法开演";
                return false;
            }

            // 预检演员是否在当前场景
            var loc = Game1.player.currentLocation;
            if (cutscene.ActorNames != null && cutscene.ActorNames.Count > 0)
            {
                var missingActors = cutscene.ActorNames.Where(name =>
                    loc.characters.All(c => !string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase) &&
                                            !string.Equals(c.displayName, name, StringComparison.OrdinalIgnoreCase))).ToList();

                if (missingActors.Count > 0)
                {
                    errorMessage = $"部分演员（{string.Join(", ", missingActors)}）不在当前场景（{loc.DisplayName}），无法重播";
                    return false;
                }
            }

            return VirtualDirector.Instance.PlayScript(cutscene.RawJson, out errorMessage);
        }
    }
}
