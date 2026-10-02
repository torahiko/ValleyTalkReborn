using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using StardewValley;

namespace ValleytalkReborn.Cutscene.Actions
{
    /// <summary>
    /// NPC 电影级对白动作：驱动底部沉浸字幕横幅、高清立绘与打字机动效，兼顾原版头顶冒泡
    /// </summary>
    public sealed class SpeakAction : IDirectorAction
    {
        public bool WaitForCompletion { get; set; } = true;

        private readonly NPC _npc;
        private readonly string _text;
        private readonly float _durationMs;
        private float _elapsedMs;
        private bool _skipped;
        private KeyboardState _lastKeyState;
        private MouseState _lastMouseState;

        public SpeakAction(NPC npc, string text)
        {
            _npc = npc ?? throw new ArgumentNullException(nameof(npc));
            _text = text ?? string.Empty;
            
            // 动态计算兜底阅读时长：120ms/字符，范围 2000ms~5000ms
            _durationMs = Math.Clamp(text.Length * 120f, 2000f, 5000f);
        }

        public void Enter()
        {
            _elapsedMs = 0f;
            _skipped = false;
            _lastKeyState = Keyboard.GetState();
            _lastMouseState = Mouse.GetState();
            
            // 1. 头顶简易气泡（兼顾远景观察）
            _npc?.showTextAboveHead(_text);

            // 2. 电影级底部字幕立绘横幅
            VirtualDirector.Instance?.ShowDialogue(_npc, _text);
        }

        public bool Update(GameTime time)
        {
            // 优先由电影级字幕系统掌控推进与跳过
            if (VirtualDirector.Instance != null && VirtualDirector.Instance.IsActive)
            {
                return VirtualDirector.Instance.IsDialogueCompleted;
            }

            // 兜底逻辑：常规计时与按键检测
            _elapsedMs += (float)time.ElapsedGameTime.TotalMilliseconds;

            if (!_skipped)
            {
                var currentKeyState = Keyboard.GetState();
                var currentMouseState = Mouse.GetState();

                bool spacePressed = currentKeyState.IsKeyDown(Keys.Space) && _lastKeyState.IsKeyUp(Keys.Space);
                bool leftClickPressed = currentMouseState.LeftButton == ButtonState.Pressed && 
                                       _lastMouseState.LeftButton == ButtonState.Released;

                if (spacePressed || leftClickPressed)
                {
                    _skipped = true;
                }

                _lastKeyState = currentKeyState;
                _lastMouseState = currentMouseState;
            }

            return _skipped || _elapsedMs >= _durationMs;
        }

        public void Exit()
        {
            VirtualDirector.Instance?.HideDialogue();
        }
    }
}
