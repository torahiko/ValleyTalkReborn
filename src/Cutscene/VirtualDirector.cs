using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using ValleytalkReborn.Cutscene.Compiler;
using ValleytalkReborn.Cutscene.Model;

namespace ValleytalkReborn.Cutscene
{
    /// <summary>
    /// 虚拟导演中控：单例存在于主线程，负责接管游戏状态、调度动作队列、控制相机与电影黑边，
    /// 支持并发动作（Parallel Actions）与无缝黑屏渐变遮罩（Fade-to-Black）优雅复原现场。
    /// </summary>
    public sealed class VirtualDirector
    {
        public static VirtualDirector Instance { get; } = new();

        public bool IsActive { get; private set; }

        private enum DirectorPhase
        {
            Idle,
            Playing,
            FadingOut,
            FadingIn
        }

        private DirectorPhase _phase = DirectorPhase.Idle;

        // 状态与队列（升级支持并发动作集合）
        private readonly Queue<IDirectorAction> _actionQueue = new();
        private readonly List<IDirectorAction> _activeActions = new();
        private CutsceneSnapshot _snapshot;
        private readonly List<NPC> _participatingActors = new();

        // 视觉表现：电影黑边
        private float _blackBarHeight;
        private const float TargetBarHeight = 70f;
        private const float BlackBarTransitionSpeed = 4f; // 像素/帧

        // 视觉表现：全屏黑幕过渡
        private float _fadeAlpha = 0f;
        private const float FadeSpeed = 2.5f; // ~0.4s 渐出，~0.4s 渐入，总耗时约 0.8s 电影级过渡

        // 视觉表现：相机平滑插值
        private Vector2? _cameraTargetPixel;
        private const float CameraLerpSpeed = 0.08f;

        // ESC 键状态
        private KeyboardState _lastKeyState;

        private VirtualDirector() { }

        /// <summary>
        /// 从原始 JSON 字符串编译并播放剧本
        /// </summary>
        public bool PlayScript(string rawJson, out string errorMessage)
        {
            errorMessage = string.Empty;
            if (!Context.IsWorldReady || Game1.player?.currentLocation == null)
            {
                errorMessage = "World not ready or player location null.";
                return false;
            }

            var compileResult = CutsceneScriptCompiler.Compile(rawJson, Game1.player.currentLocation);
            if (!compileResult.Success)
            {
                errorMessage = compileResult.ErrorMessage;
                ModEntry.SMonitor?.Log($"[VirtualDirector] PlayScript compile failed: {errorMessage}", LogLevel.Warn);
                return false;
            }

            foreach (var warn in compileResult.Warnings)
            {
                ModEntry.SMonitor?.Log($"[VirtualDirector] Compile warning: {warn}", LogLevel.Warn);
            }

            Play(compileResult.Actions, compileResult.ResolvedActors);
            return true;
        }

        /// <summary>
        /// 从 CutsceneScriptIR 编译并播放剧本
        /// </summary>
        public bool PlayScript(CutsceneScriptIR ir, out string errorMessage)
        {
            errorMessage = string.Empty;
            if (!Context.IsWorldReady || Game1.player?.currentLocation == null)
            {
                errorMessage = "World not ready or player location null.";
                return false;
            }

            var compileResult = CutsceneScriptCompiler.Compile(ir, Game1.player.currentLocation);
            if (!compileResult.Success)
            {
                errorMessage = compileResult.ErrorMessage;
                ModEntry.SMonitor?.Log($"[VirtualDirector] PlayScript compile failed: {errorMessage}", LogLevel.Warn);
                return false;
            }

            Play(compileResult.Actions, compileResult.ResolvedActors);
            return true;
        }

        /// <summary>
        /// 开始播放过场：接管游戏控制、捕获快照、压制原生 UI、启动黑边过渡
        /// </summary>
        public void Play(List<IDirectorAction> actions, List<NPC> actors)
        {
            if (IsActive)
            {
                ModEntry.SMonitor?.Log("[VirtualDirector] Already active, ignoring Play call.", LogLevel.Warn);
                return;
            }

            if (actions == null || actions.Count == 0)
            {
                ModEntry.SMonitor?.Log("[VirtualDirector] No actions provided, aborting.", LogLevel.Warn);
                return;
            }

            if (!Context.IsWorldReady || Game1.player == null)
            {
                ModEntry.SMonitor?.Log("[VirtualDirector] World not ready, aborting.", LogLevel.Warn);
                return;
            }

            // BOUNDARY: 过场仅允许在玩家完全自由时启动（无事件/无菜单/未被强制控制），
            // 否则快照-复原语义会被外部状态破坏。
            if (!Context.IsPlayerFree)
            {
                ModEntry.SMonitor?.Log(
                    "[VirtualDirector] Player not free (event/menu active) — cutscene refused.",
                    LogLevel.Warn);
                return;
            }

            try
            {
                IsActive = true;
                _phase = DirectorPhase.Playing;
                _fadeAlpha = 0f;

                // 1. 捕获初始快照与唤醒参演角色
                _snapshot = CutsceneSnapshot.Capture(actors ?? new List<NPC>());
                _participatingActors.Clear();
                if (actors != null)
                {
                    _participatingActors.AddRange(actors);
                    foreach (var actor in _participatingActors)
                    {
                        CutsceneActorHelper.WakeupActor(actor);
                    }
                }

                // 2. 压制游戏原生系统
                SuppressGame();

                // 3. 加载动作队列并派发首批动作（支持并发启动）
                _activeActions.Clear();
                _actionQueue.Clear();
                foreach (var action in actions)
                    _actionQueue.Enqueue(action);

                AdvanceToNextActions();

                // 4. 启动黑边平滑升起
                _blackBarHeight = 0f;

                _lastKeyState = Keyboard.GetState();

                ModEntry.SMonitor?.Log(
                    $"[VirtualDirector] Cutscene started with {actions.Count} actions and {_participatingActors.Count} actors.",
                    LogLevel.Info);
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[VirtualDirector] Play failed: {ex}", LogLevel.Error);
                Abort(immediate: true);
            }
        }

        /// <summary>
        /// 中止过场：默认走优雅黑屏过渡（~0.8s），也可在异常或重置时立即硬复原。
        /// </summary>
        public void Abort(bool immediate = false)
        {
            if (!IsActive) return;

            if (immediate)
            {
                ImmediateRestore();
                return;
            }

            if (_phase == DirectorPhase.FadingOut || _phase == DirectorPhase.FadingIn)
            {
                // 已经在过渡收尾中，忽略重复触发
                return;
            }

            StartFadeOutAndRestore();
        }

        /// <summary>
        /// 立即硬复原：在未预期的异常分支或强制停止时跳过渐变动画。
        /// </summary>
        private void ImmediateRestore()
        {
            try
            {
                ModEntry.SMonitor?.Log("[VirtualDirector] Immediate restore requested.", LogLevel.Debug);

                foreach (var action in _activeActions)
                {
                    try { action.Exit(); } catch { }
                }
                _activeActions.Clear();
                _actionQueue.Clear();

                _snapshot?.Restore();
                RestoreGame();

                _blackBarHeight = 0f;
                _cameraTargetPixel = null;
                _participatingActors.Clear();
                _fadeAlpha = 0f;
                _phase = DirectorPhase.Idle;
                _snapshot = null;

                IsActive = false;

                ModEntry.SMonitor?.Log("[VirtualDirector] Cutscene immediately restored.", LogLevel.Info);
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[VirtualDirector] ImmediateRestore failed: {ex}", LogLevel.Error);
                _phase = DirectorPhase.Idle;
                IsActive = false;
                _snapshot = null;
            }
        }

        /// <summary>
        /// 启动黑屏渐出过渡，准备幕后复原
        /// </summary>
        private void StartFadeOutAndRestore()
        {
            if (_phase != DirectorPhase.Playing)
                return;

            ModEntry.SMonitor?.Log("[VirtualDirector] Beginning fade-out transition...", LogLevel.Debug);
            _phase = DirectorPhase.FadingOut;

            foreach (var action in _activeActions)
            {
                try
                {
                    action.Exit();
                }
                catch (Exception ex)
                {
                    ModEntry.SMonitor?.Log($"[VirtualDirector] Error exiting action: {ex.Message}", LogLevel.Warn);
                }
            }

            _activeActions.Clear();
            _actionQueue.Clear();
        }

        /// <summary>
        /// 世界销毁路径（读档/回标题）：仅清空导演内部状态，不触碰世界对象。
        /// 与 Abort 的区别：不执行快照复原——彼时世界已不存在。
        /// </summary>
        public void ForceStop()
        {
            _activeActions.Clear();
            _actionQueue.Clear();
            _snapshot = null;
            _participatingActors.Clear();
            _blackBarHeight = 0f;
            _cameraTargetPixel = null;
            _fadeAlpha = 0f;
            _phase = DirectorPhase.Idle;

            if (IsActive)
            {
                IsActive = false;
                ModEntry.SMonitor?.Log(
                    "[VirtualDirector] Force stopped (world teardown).",
                    LogLevel.Debug);
            }
        }

        /// <summary>
        /// 每帧更新：驱动动作队列、维护演员保活、相机插值、黑屏过渡与 ESC 监听
        /// </summary>
        public void Update(UpdateTickedEventArgs e)
        {
            if (!IsActive) return;

            try
            {
                float dt = (float)Game1.currentGameTime.ElapsedGameTime.TotalSeconds;

                if (_phase == DirectorPhase.FadingOut)
                {
                    UpdateFadingOut(dt);
                    return;
                }

                if (_phase == DirectorPhase.FadingIn)
                {
                    UpdateFadingIn(dt);
                    return;
                }

                if (_phase == DirectorPhase.Playing)
                {
                    UpdatePlaying(dt);
                }
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[VirtualDirector] Update error: {ex}", LogLevel.Error);
                Abort(immediate: true);
            }
        }

        private void UpdateFadingOut(float dt)
        {
            // 渐变黑屏过程中保持玩家定身与时钟静止
            if (Game1.player != null)
            {
                Game1.player.freezePause = 100;
                Game1.player.CanMove = false;
            }
            Game1.gameTimeInterval = 0;

            _fadeAlpha += dt * FadeSpeed;
            if (_fadeAlpha >= 1f)
            {
                _fadeAlpha = 1f;

                // ★ 核心：黑幕完全掩盖视线（100% 纯黑）后，在幕后神不知鬼不觉地复原现场
                ModEntry.SMonitor?.Log("[VirtualDirector] Screen fully black — restoring snapshot behind curtain.", LogLevel.Debug);

                // 1. 恢复快照（NPC 归位、重置控制器与原版日程）
                _snapshot?.Restore();

                // 2. 解除游戏视口冻结与相机跟随，恢复 HUD
                RestoreGame();
                _cameraTargetPixel = null;
                _blackBarHeight = 0f;
                _participatingActors.Clear();

                // 3. 转入淡入阶段，保持玩家定身直到淡入完成
                if (Game1.player != null)
                {
                    Game1.player.freezePause = 10;
                    Game1.player.CanMove = false;
                }
                _phase = DirectorPhase.FadingIn;
            }
        }

        private void UpdateFadingIn(float dt)
        {
            // 淡入过程中保持玩家定身，避免在黑屏尚未完全退去时误触移动
            if (Game1.player != null)
            {
                Game1.player.freezePause = 10;
                Game1.player.CanMove = false;
            }

            _fadeAlpha -= dt * FadeSpeed;
            if (_fadeAlpha <= 0f)
            {
                _fadeAlpha = 0f;
                _phase = DirectorPhase.Idle;
                IsActive = false;

                // 最终归还玩家控制权（依据快照真实记录的值还原）
                if (Game1.player != null && _snapshot != null)
                {
                    Game1.player.CanMove = _snapshot.PlayerCanMove;
                    Game1.player.freezePause = 0;
                }
                _snapshot = null;

                ModEntry.SMonitor?.Log("[VirtualDirector] Cutscene transition completed, control returned.", LogLevel.Info);
            }
        }

        private void UpdatePlaying(float dt)
        {
            // 1. 维护参演 Actor 保活（关键：防止时间冻结时 NPC 物理更新被跳过）
            foreach (var actor in _participatingActors)
            {
                if (actor?.currentLocation != null)
                {
                    actor.forceUpdateTimer = 1000;
                    if (actor.movementPause > 0)
                        actor.movementPause = 0;
                }
            }

            // 2. 维持玩家定身
            if (Game1.player != null)
            {
                Game1.player.freezePause = 100;
                Game1.player.CanMove = false;
            }

            // 3. 维持时钟静止
            Game1.gameTimeInterval = 0;

            // 4. 黑边平滑过渡
            if (_blackBarHeight < TargetBarHeight)
            {
                _blackBarHeight = Math.Min(_blackBarHeight + BlackBarTransitionSpeed, TargetBarHeight);
            }

            // 5. 相机平滑插值
            if (_cameraTargetPixel.HasValue)
            {
                var targetViewport = new Vector2(
                    _cameraTargetPixel.Value.X - Game1.viewport.Width / 2f,
                    _cameraTargetPixel.Value.Y - Game1.viewport.Height / 2f
                );

                // Clamp 边界限制：防止相机滑出地图
                var currentLoc = Game1.currentLocation;
                if (currentLoc != null && currentLoc.map != null)
                {
                    int mapWidthPixels = currentLoc.map.Layers[0].LayerWidth * 64;
                    int mapHeightPixels = currentLoc.map.Layers[0].LayerHeight * 64;

                    targetViewport.X = Math.Clamp(targetViewport.X, 0, mapWidthPixels - Game1.viewport.Width);
                    targetViewport.Y = Math.Clamp(targetViewport.Y, 0, mapHeightPixels - Game1.viewport.Height);
                }

                Game1.viewport.X = (int)MathHelper.Lerp(Game1.viewport.X, targetViewport.X, CameraLerpSpeed);
                Game1.viewport.Y = (int)MathHelper.Lerp(Game1.viewport.Y, targetViewport.Y, CameraLerpSpeed);
            }

            // 6. 驱动所有活跃动作（支持并发执行）
            for (int i = _activeActions.Count - 1; i >= 0; i--)
            {
                var action = _activeActions[i];
                bool completed = false;
                try
                {
                    completed = action.Update(Game1.currentGameTime);
                }
                catch (Exception ex)
                {
                    ModEntry.SMonitor?.Log($"[VirtualDirector] Action update error: {ex.Message}", LogLevel.Warn);
                    completed = true; // 异常时当作完成，防止死锁卡死
                }

                if (completed)
                {
                    try
                    {
                        action.Exit();
                    }
                    catch (Exception ex)
                    {
                        ModEntry.SMonitor?.Log($"[VirtualDirector] Action exit error: {ex.Message}", LogLevel.Warn);
                    }
                    _activeActions.RemoveAt(i);
                }
            }

            // 若当前批次所有活跃动作（包含并发动作与阻塞动作）全部运行完成，派发下一批动作
            if (_activeActions.Count == 0)
            {
                AdvanceToNextActions();
            }

            // 7. ESC 键监听：随时可中止
            var currentKeyState = Keyboard.GetState();
            if (currentKeyState.IsKeyDown(Keys.Escape) && _lastKeyState.IsKeyUp(Keys.Escape))
            {
                ModEntry.SMonitor?.Log("[VirtualDirector] ESC pressed, beginning fade-out abort.", LogLevel.Debug);
                StartFadeOutAndRestore();
            }
            _lastKeyState = currentKeyState;
        }

        /// <summary>
        /// 绘制电影黑边遮罩与全屏转场黑幕
        /// </summary>
        public void DrawOverlay(SpriteBatch b)
        {
            if (!IsActive) return;

            try
            {
                var viewport = Game1.graphics.GraphicsDevice.Viewport;

                // 1. 绘制电影上下黑边（当黑边高度 > 0）
                if (_blackBarHeight > 0f)
                {
                    int barHeight = (int)_blackBarHeight;

                    // 上黑边
                    b.Draw(
                        Game1.staminaRect,
                        new Rectangle(0, 0, viewport.Width, barHeight),
                        Color.Black
                    );

                    // 下黑边
                    b.Draw(
                        Game1.staminaRect,
                        new Rectangle(0, viewport.Height - barHeight, viewport.Width, barHeight),
                        Color.Black
                    );
                }

                // 2. 绘制全屏黑幕渐变（FadingOut / FadingIn 阶段）
                if (_fadeAlpha > 0f)
                {
                    float alpha = Math.Clamp(_fadeAlpha, 0f, 1f);
                    b.Draw(
                        Game1.staminaRect,
                        new Rectangle(0, 0, viewport.Width, viewport.Height),
                        Color.Black * alpha
                    );
                }
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[VirtualDirector] DrawOverlay error: {ex.Message}", LogLevel.Warn);
            }
        }

        /// <summary>
        /// 设置相机对焦目标（由 CameraAction 调用）
        /// </summary>
        public void SetCameraTarget(NPC npc)
        {
            if (npc?.currentLocation != null)
            {
                _cameraTargetPixel = npc.Position + new Vector2(32f, 32f); // NPC 中心点
            }
        }

        /// <summary>
        /// 设置相机对焦坐标
        /// </summary>
        public void SetCameraTarget(Vector2 tilePosition)
        {
            _cameraTargetPixel = tilePosition * 64f + new Vector2(32f, 32f);
        }

        // ══════════════════════════════════════════════════════════════
        //  内部辅助方法
        // ══════════════════════════════════════════════════════════════

        private void SuppressGame()
        {
            if (Game1.player != null)
            {
                Game1.player.CanMove = false;
                Game1.player.freezePause = 100;
            }

            Game1.displayHUD = false;
            Game1.gameTimeInterval = 0;
            Game1.viewportFreeze = true;
        }

        private void RestoreGame()
        {
            if (Game1.player != null)
            {
                Game1.player.freezePause = 0;
            }

            Game1.displayHUD = true;
            Game1.viewportFreeze = false;
        }

        /// <summary>
        /// 派发下一批动作：连续出队直至遇到 WaitForCompletion == true 的动作，或队列为空
        /// </summary>
        private void AdvanceToNextActions()
        {
            while (_actionQueue.Count > 0)
            {
                var action = _actionQueue.Dequeue();
                try
                {
                    action.Enter();
                    _activeActions.Add(action);
                    ModEntry.SMonitor?.Log(
                        $"[VirtualDirector] Started action: {action.GetType().Name} (WaitForCompletion: {action.WaitForCompletion})",
                        LogLevel.Debug);
                }
                catch (Exception ex)
                {
                    ModEntry.SMonitor?.Log($"[VirtualDirector] Action Enter error: {ex.Message}", LogLevel.Warn);
                }

                if (action.WaitForCompletion)
                {
                    // 遇到阻塞动作，停止本轮并发派发，等待活跃动作更新完成
                    break;
                }
            }

            if (_activeActions.Count == 0 && _actionQueue.Count == 0)
            {
                // 剧本全部动作播毕：平滑黑屏过渡并恢复
                ModEntry.SMonitor?.Log("[VirtualDirector] All actions completed, starting fade-out transition.", LogLevel.Info);
                StartFadeOutAndRestore();
            }
        }
    }
}
