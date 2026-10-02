using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Newtonsoft.Json;
using StardewModdingAPI;
using StardewValley;
using ValleytalkReborn.Cutscene.Actions;
using ValleytalkReborn.Cutscene.Model;

namespace ValleytalkReborn.Cutscene.Compiler
{
    /// <summary>
    /// 剧本编译结果封装
    /// </summary>
    public sealed class CompiledCutsceneResult
    {
        public bool Success { get; set; }
        public string Title { get; set; } = string.Empty;
        public List<IDirectorAction> Actions { get; } = new();
        public List<NPC> ResolvedActors { get; } = new();
        public List<string> Warnings { get; } = new();
        public string ErrorMessage { get; set; } = string.Empty;
    }

    /// <summary>
    /// 声明式剧本编译器：将 JSON IR 转换为强类型、带安全换算与容错保护的 Director 动作序列。
    /// </summary>
    public static class CutsceneScriptCompiler
    {
        /// <summary>
        /// 从原始字符串（可能带有 Markdown 围栏或前后杂质）编译剧本
        /// </summary>
        public static CompiledCutsceneResult Compile(string rawJson, GameLocation location)
        {
            var result = new CompiledCutsceneResult();

            if (string.IsNullOrWhiteSpace(rawJson))
            {
                result.ErrorMessage = "Script input is null or empty.";
                return result;
            }

            string cleanJson = ExtractJson(rawJson);
            if (string.IsNullOrWhiteSpace(cleanJson))
            {
                result.ErrorMessage = "Failed to extract valid JSON payload from input.";
                return result;
            }

            CutsceneScriptIR ir;
            try
            {
                ir = JsonConvert.DeserializeObject<CutsceneScriptIR>(cleanJson);
            }
            catch (Exception ex)
            {
                result.ErrorMessage = $"JSON Deserialization failed: {ex.Message}";
                return result;
            }

            if (ir == null)
            {
                result.ErrorMessage = "Deserialized CutsceneScriptIR is null.";
                return result;
            }

            return Compile(ir, location);
        }

        /// <summary>
        /// 从已反序列化的 CutsceneScriptIR 编译为运行时动作序列
        /// </summary>
        public static CompiledCutsceneResult Compile(CutsceneScriptIR ir, GameLocation location)
        {
            var result = new CompiledCutsceneResult();

            if (ir == null)
            {
                result.ErrorMessage = "CutsceneScriptIR is null.";
                return result;
            }

            if (location == null)
            {
                result.ErrorMessage = "Target GameLocation is null.";
                return result;
            }

            result.Title = ir.Title ?? "Untitled Cutscene";

            // 1. 辅助方法：在当前地图中按内部名或本地化显示名匹配 NPC
            NPC FindNpc(string name)
            {
                if (string.IsNullOrWhiteSpace(name)) return null;

                return location.characters?.FirstOrDefault(n =>
                    n != null &&
                    (string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(n.displayName, name, StringComparison.OrdinalIgnoreCase)));
            }

            // 2. 解析显式声明的演员列表
            if (ir.Actors != null)
            {
                foreach (var actorName in ir.Actors)
                {
                    var npc = FindNpc(actorName);
                    if (npc != null)
                    {
                        if (!result.ResolvedActors.Contains(npc))
                            result.ResolvedActors.Add(npc);
                    }
                    else
                    {
                        result.Warnings.Add($"Declared actor '{actorName}' is not present in {location.NameOrUniqueName}.");
                    }
                }
            }

            // 3. 逐个动作编译与安全校验
            if (ir.Actions == null || ir.Actions.Count == 0)
            {
                result.ErrorMessage = "Cutscene contains no actions.";
                return result;
            }

            foreach (var actionIr in ir.Actions)
            {
                if (actionIr == null) continue;

                string actionType = actionIr.Type?.Trim().ToLowerInvariant() ?? string.Empty;

                try
                {
                    switch (actionType)
                    {
                        case "camera":
                        {
                            IDirectorAction camAction = null;
                            if (string.Equals(actionIr.Target, "farmer", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(actionIr.Target, "player", StringComparison.OrdinalIgnoreCase))
                            {
                                camAction = new CameraAction(targetPlayer: true);
                            }
                            else if (!string.IsNullOrWhiteSpace(actionIr.Target))
                            {
                                var npc = FindNpc(actionIr.Target);
                                if (npc != null)
                                {
                                    camAction = new CameraAction(npc);
                                    if (!result.ResolvedActors.Contains(npc))
                                        result.ResolvedActors.Add(npc);
                                }
                            }

                            if (camAction == null)
                            {
                                var tile = actionIr.ResolveTargetTile();
                                if (tile.HasValue)
                                    camAction = new CameraAction(tile.Value);
                            }

                            if (camAction != null)
                            {
                                camAction.WaitForCompletion = actionIr.WaitForCompletion;
                                result.Actions.Add(camAction);
                            }
                            else
                            {
                                result.Warnings.Add($"[Compiler] Camera target '{actionIr.Target}' could not be resolved, skipping.");
                            }
                            break;
                        }

                        case "move":
                        {
                            var npc = FindNpc(actionIr.Actor);
                            if (npc == null)
                            {
                                result.Warnings.Add($"[Compiler] Move actor '{actionIr.Actor}' not found, skipping.");
                                break;
                            }
                            if (!result.ResolvedActors.Contains(npc))
                                result.ResolvedActors.Add(npc);

                            var rawTile = actionIr.ResolveTargetTile();
                            if (!rawTile.HasValue)
                            {
                                result.Warnings.Add($"[Compiler] Move target tile for '{actionIr.Actor}' is invalid or missing, skipping.");
                                break;
                            }

                            // ★ 红线 #2：强制经过 FindNearestWalkableTile 换算可通行格子，绝不死锁！
                            var safeTile = MovementPathfinding.FindNearestWalkableTile(location, rawTile.Value, npc, radius: 5);
                            var moveAction = new MoveToTileAction(npc, safeTile, actionIr.Timeout ?? 6f)
                            {
                                WaitForCompletion = actionIr.WaitForCompletion
                            };
                            result.Actions.Add(moveAction);
                            break;
                        }

                        case "lookat":
                        {
                            var npc = FindNpc(actionIr.Actor);
                            if (npc == null)
                            {
                                result.Warnings.Add($"[Compiler] LookAt actor '{actionIr.Actor}' not found, skipping.");
                                break;
                            }
                            if (!result.ResolvedActors.Contains(npc))
                                result.ResolvedActors.Add(npc);

                            IDirectorAction lookAction = null;
                            if (string.Equals(actionIr.Target, "farmer", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(actionIr.Target, "player", StringComparison.OrdinalIgnoreCase))
                            {
                                if (Game1.player != null)
                                    lookAction = new LookAtAction(npc, Game1.player);
                            }
                            else if (!string.IsNullOrWhiteSpace(actionIr.Target))
                            {
                                var targetNpc = FindNpc(actionIr.Target);
                                if (targetNpc != null)
                                {
                                    lookAction = new LookAtAction(npc, targetNpc);
                                    if (!result.ResolvedActors.Contains(targetNpc))
                                        result.ResolvedActors.Add(targetNpc);
                                }
                            }

                            if (lookAction == null)
                            {
                                var tile = actionIr.ResolveTargetTile();
                                if (tile.HasValue)
                                    lookAction = new LookAtAction(npc, tile.Value);
                            }

                            if (lookAction != null)
                            {
                                lookAction.WaitForCompletion = actionIr.WaitForCompletion;
                                result.Actions.Add(lookAction);
                            }
                            else
                            {
                                result.Warnings.Add($"[Compiler] LookAt target '{actionIr.Target}' could not be resolved, skipping.");
                            }
                            break;
                        }

                        case "face":
                        {
                            var npc = FindNpc(actionIr.Actor);
                            if (npc == null)
                            {
                                result.Warnings.Add($"[Compiler] Face actor '{actionIr.Actor}' not found, skipping.");
                                break;
                            }
                            if (!result.ResolvedActors.Contains(npc))
                                result.ResolvedActors.Add(npc);

                            var dir = actionIr.ResolveDirection();
                            if (!dir.HasValue)
                            {
                                result.Warnings.Add($"[Compiler] Face direction for '{actionIr.Actor}' invalid, defaulting to Down(2).");
                                dir = 2;
                            }

                            var faceAction = new FaceAction(npc, dir.Value)
                            {
                                WaitForCompletion = actionIr.WaitForCompletion
                            };
                            result.Actions.Add(faceAction);
                            break;
                        }

                        case "emote":
                        {
                            var npc = FindNpc(actionIr.Actor);
                            if (npc == null)
                            {
                                result.Warnings.Add($"[Compiler] Emote actor '{actionIr.Actor}' not found, skipping.");
                                break;
                            }
                            if (!result.ResolvedActors.Contains(npc))
                                result.ResolvedActors.Add(npc);

                            string emote = string.IsNullOrWhiteSpace(actionIr.Emote) ? "SURPRISE" : actionIr.Emote;
                            var emoteAction = new EmoteAction(npc, emote)
                            {
                                WaitForCompletion = actionIr.WaitForCompletion
                            };
                            result.Actions.Add(emoteAction);
                            break;
                        }

                        case "speak":
                        {
                            var npc = FindNpc(actionIr.Actor);
                            if (npc == null)
                            {
                                result.Warnings.Add($"[Compiler] Speak actor '{actionIr.Actor}' not found, skipping.");
                                break;
                            }
                            if (!result.ResolvedActors.Contains(npc))
                                result.ResolvedActors.Add(npc);

                            string text = actionIr.Text ?? string.Empty;
                            var speakAction = new SpeakAction(npc, text)
                            {
                                WaitForCompletion = actionIr.WaitForCompletion
                            };
                            result.Actions.Add(speakAction);
                            break;
                        }

                        case "wait":
                        {
                            float dur = actionIr.Duration ?? 1.0f;
                            var waitAction = new WaitAction(dur)
                            {
                                WaitForCompletion = actionIr.WaitForCompletion
                            };
                            result.Actions.Add(waitAction);
                            break;
                        }

                        case "sound":
                        {
                            if (string.IsNullOrWhiteSpace(actionIr.SoundName))
                            {
                                result.Warnings.Add("[Compiler] Sound name is empty, skipping.");
                                break;
                            }

                            var soundAction = new SoundAction(actionIr.SoundName)
                            {
                                WaitForCompletion = actionIr.WaitForCompletion
                            };
                            result.Actions.Add(soundAction);
                            break;
                        }

                        default:
                            result.Warnings.Add($"[Compiler] Unknown action type '{actionIr.Type}', skipping.");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"[Compiler] Failed to compile action '{actionType}': {ex.Message}");
                }
            }

            if (result.Actions.Count == 0)
            {
                result.ErrorMessage = "All actions failed compilation or were invalid.";
                return result;
            }

            result.Success = true;
            return result;
        }

        /// <summary>
        /// 健壮提取字符串中的 JSON 主体（剥离 Markdown 围栏 ```json ... ``` 及前后无关文本）
        /// </summary>
        public static string ExtractJson(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            string text = raw.Trim();

            // 1. 尝试提取 ```json ... ``` 或 ``` ... ```
            int fenceStart = text.IndexOf("```", StringComparison.Ordinal);
            if (fenceStart >= 0)
            {
                int contentStart = text.IndexOf('\n', fenceStart);
                if (contentStart >= 0)
                {
                    contentStart++; // 跳过换行符
                    int fenceEnd = text.IndexOf("```", contentStart, StringComparison.Ordinal);
                    if (fenceEnd > contentStart)
                    {
                        text = text.Substring(contentStart, fenceEnd - contentStart).Trim();
                    }
                }
            }

            // 2. 深度扫描最外层大括号 {...}
            int start = text.IndexOf('{');
            if (start < 0) return null;

            int depth = 0;
            bool inString = false;
            bool escaped = false;

            for (int i = start; i < text.Length; i++)
            {
                char c = text[i];

                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (c == '\\' && inString)
                {
                    escaped = true;
                    continue;
                }

                if (c == '"')
                {
                    inString = !inString;
                    continue;
                }

                if (inString) continue;

                if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return text.Substring(start, i - start + 1);
                    }
                }
            }

            return null;
        }
    }
}
