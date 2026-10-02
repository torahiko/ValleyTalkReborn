using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace ValleytalkReborn.Cutscene
{
    /// <summary>
    /// 虚拟导演中控：单例存在于主线程，负责接管游戏状态、驱动动作队列、控制相机与电影黑边。
    /// </summary>
    public sealed class VirtualDirector
    {
        public static VirtualDirector Instance { get; } = new();

        public bool IsActive { get; private set; }

        // 状态与队列
        private readonly Queue<IDirectorAction> _actionQueue = new();
        private IDirectorAction _currentAction;
        private CutsceneSnapshot _snapshot;
        private readonly List<NPC> _participatingActors = new();

        // 视觉表现：电影黑边
        private float _blackBarHeight;
        private const float TargetBarHeight = 70f;
        private const float BlackBarTransitionSpeed = 4f; // 像素/帧

        // 视觉表现：相机平滑插值
        private Vector2? _cameraTargetPixel;
        private const float CameraLerpSpeed = 0.08f;

        // ESC 键状态
        private KeyboardState _lastKeyState;

        private VirtualDirector() { }

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

                // 1. 捕获初始快照
                _snapshot = CutsceneSnapshot.Capture(actors ?? new List<NPC>());
                _participatingActors.Clear();
                if (actors != null)
                    _participatingActors.AddRange(actors);

                // 2. 压制游戏原生系统
                SuppressGame();

                // 3. 加载动作队列并启动第一个动作
                _actionQueue.Clear();
                foreach (var action in actions)
                    _actionQueue.Enqueue(action);

                AdvanceToNextAction();

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
                Abort();
            }
        }

        /// <summary>
        /// 随时中止过场：ESC 键触发或内部错误时调用，零延迟优雅复原
        /// </summary>
        public void Abort()
        {
            if (!IsActive) return;

            try
            {
                ModEntry.SMonitor?.Log("[VirtualDirector] Aborting cutscene...", LogLevel.Debug);

                // 1. 退出当前动作
                _currentAction?.Exit();
                _currentAction = null;

                // 2. 清空剩余动作
                _actionQueue.Clear();

                // 3. 恢复快照
                _snapshot?.Restore();

                // 4. 恢复游戏控制
                RestoreGame();

                // 5. 重置视觉状态
                _blackBarHeight = 0f;
                _cameraTargetPixel = null;
                _participatingActors.Clear();

                IsActive = false;

                ModEntry.SMonitor?.Log("[VirtualDirector] Cutscene aborted successfully.", LogLevel.Info);
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[VirtualDirector] Abort failed: {ex}", LogLevel.Error);
                IsActive = false;
            }
        }

        /// <summary>
        /// 世界销毁路径（读档/回标题）：仅清空导演内部状态，不触碰世界对象。
        /// 与 Abort 的区别：不执行快照复原——彼时世界已不存在。
        /// </summary>
        public void ForceStop()
        {
            _currentAction = null;
            _actionQueue.Clear();
            _snapshot = null;
            _participatingActors.Clear();
            _blackBarHeight = 0f;
            _cameraTargetPixel = null;

            if (IsActive)
            {
                IsActive = false;
                ModEntry.SMonitor?.Log(
                    "[VirtualDirector] Force stopped (world teardown).",
                    LogLevel.Debug);
            }
        }

        /// <summary>
        /// 每帧更新：驱动动作队列、维护演员保活、相机插值、ESC 监听
        /// </summary>
        public void Update(UpdateTickedEventArgs e)
        {
            if (!IsActive) return;

            try
            {
                // 1. 维护参演 Actor 保活（关键：防止时间冻结时 NPC 物理更新被跳过）
                foreach (var actor in _participatingActors)
                {
                    if (actor?.currentLocation != null)
                        actor.forceUpdateTimer = 60;
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

                // 6. 驱动当前动作
                if (_currentAction != null)
                {
                    bool completed = _currentAction.Update(Game1.currentGameTime);
                    if (completed)
                    {
                        _currentAction.Exit();
                        AdvanceToNextAction();
                    }
                }

                // 7. ESC 键监听：随时可中止
                var currentKeyState = Keyboard.GetState();
                if (currentKeyState.IsKeyDown(Keys.Escape) && _lastKeyState.IsKeyUp(Keys.Escape))
                {
                    ModEntry.SMonitor?.Log("[VirtualDirector] ESC pressed, aborting cutscene.", LogLevel.Debug);
                    Abort();
                }
                _lastKeyState = currentKeyState;
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[VirtualDirector] Update error: {ex}", LogLevel.Error);
                Abort();
            }
        }

        /// <summary>
        /// 绘制电影黑边遮罩
        /// </summary>
        public void DrawOverlay(SpriteBatch b)
        {
            if (!IsActive) return;
            if (_blackBarHeight <= 0f) return;

            try
            {
                int barHeight = (int)_blackBarHeight;
                var viewport = Game1.graphics.GraphicsDevice.Viewport;

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
                Game1.player.CanMove = true;
                Game1.player.freezePause = 0;
            }

            Game1.displayHUD = true;
            Game1.viewportFreeze = false;
        }

        private void AdvanceToNextAction()
        {
            if (_actionQueue.Count > 0)
            {
                _currentAction = _actionQueue.Dequeue();
                _currentAction?.Enter();
                ModEntry.SMonitor?.Log(
                    $"[VirtualDirector] Advanced to action: {_currentAction?.GetType().Name}",
                    LogLevel.Debug);
            }
            else
            {
                // 剧本播毕：自动恢复
                ModEntry.SMonitor?.Log("[VirtualDirector] All actions completed, restoring game.", LogLevel.Info);
                Abort();
            }
        }
    }
}
