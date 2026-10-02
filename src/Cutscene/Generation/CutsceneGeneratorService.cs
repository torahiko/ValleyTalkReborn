using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StardewModdingAPI;
using StardewValley;
using ValleytalkReborn.Cutscene.Compiler;
using ValleytalkReborn.Cutscene.Perception;
using ValleytalkReborn.Cutscene.Prompt;
using ValleytalkReborn;

namespace ValleytalkReborn.Cutscene.Generation
{
    /// <summary>
    /// 虚拟导演异步编剧服务：串联感知、Prompt 构建、LLM 推理、编译器解析与主线程开演
    /// </summary>
    public static class CutsceneGeneratorService
    {
        private static bool _isGenerating = false;

        public static bool IsGenerating => _isGenerating;

        /// <summary>
        /// 异步根据当前场景与角色人设生成微剧本并自动开演
        /// </summary>
        public static async Task<(bool Success, string Message)> GenerateAndPlayAsync(
            List<NPC> actors,
            string userIntent = null,
            CancellationToken ct = default)
        {
            if (_isGenerating)
            {
                return (false, "另一个剧本正在生成中，请稍候");
            }

            if (!Context.IsWorldReady || Game1.player?.currentLocation == null)
            {
                return (false, "游戏世界尚未就绪");
            }

            if (VirtualDirector.Instance.IsActive)
            {
                return (false, "当前已有过场正在演出中");
            }

            if (!Context.IsPlayerFree)
            {
                return (false, "玩家当前未处于自由行动状态（有菜单或原版事件进行中）");
            }

            if (actors == null || actors.Count == 0)
            {
                return (false, "参演角色列表为空");
            }

            var location = Game1.player.currentLocation;

            try
            {
                _isGenerating = true;

                // 1. 采集当下场景与演员上下文
                var ctx = CutsceneContextCollector.Collect(actors, location);

                // 2. 构造符合 JSON IR 规范的提示词
                string sysPrompt = CutscenePromptBuilder.BuildSystemPrompt();
                string userPrompt = CutscenePromptBuilder.BuildUserPrompt(ctx, userIntent);

                ModEntry.SMonitor?.Log("[CutsceneGenerator] Requesting LLM for dynamic cutscene script...", LogLevel.Info);
                Game1.addHUDMessage(new HUDMessage("🎬 虚拟导演正在构思即兴剧本中...", HUDMessage.newQuest_type));

                // 3. 异步调用大模型推理（不卡主线程）
                var llmResp = await Llm.Instance.RunInference(
                    systemPromptString: sysPrompt,
                    gameCacheString: string.Empty,
                    npcCacheString: string.Empty,
                    promptString: userPrompt,
                    responseStart: string.Empty,
                    n_predict: 2048,
                    cacheContext: LlmContextTypes.Director
                );

                if (llmResp == null || string.IsNullOrWhiteSpace(llmResp.Text))
                {
                    ModEntry.SMonitor?.Log("[CutsceneGenerator] LLM returned null or empty response.", LogLevel.Warn);
                    Game1.addHUDMessage(new HUDMessage("🎬 大模型响应为空，剧本生成取消", HUDMessage.error_type));
                    return (false, "大模型未返回有效文本");
                }

                // 4. 语义编译器校验与动作队列生成
                var compileResult = CutsceneScriptCompiler.Compile(llmResp.Text, location);
                if (!compileResult.Success)
                {
                    ModEntry.SMonitor?.Log($"[CutsceneGenerator] Script compilation failed: {compileResult.ErrorMessage}", LogLevel.Warn);
                    Game1.addHUDMessage(new HUDMessage($"🎬 剧本解析失败: {compileResult.ErrorMessage}", HUDMessage.error_type));
                    return (false, $"剧本编译失败: {compileResult.ErrorMessage}");
                }

                // 5. 跨线程安全派发到主线程开演
                VirtualDirector.EnqueueMainThread(() =>
                {
                    if (!Context.IsPlayerFree || VirtualDirector.Instance.IsActive)
                    {
                        ModEntry.SMonitor?.Log("[CutsceneGenerator] Main thread busy when attempting to play cutscene.", LogLevel.Warn);
                        return;
                    }

                    Game1.addHUDMessage(new HUDMessage($"🎬 即兴短剧《{compileResult.Title}》开演！", HUDMessage.achievement_type));
                    VirtualDirector.Instance.Play(compileResult.Actions, compileResult.ResolvedActors);
                });

                return (true, $"剧本《{compileResult.Title}》生成成功并已进入开演队列");
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[CutsceneGenerator] Error during generation: {ex}", LogLevel.Error);
                Game1.addHUDMessage(new HUDMessage($"🎬 编剧异常: {ex.Message}", HUDMessage.error_type));
                return (false, $"生成异常: {ex.Message}");
            }
            finally
            {
                _isGenerating = false;
            }
        }
    }
}
