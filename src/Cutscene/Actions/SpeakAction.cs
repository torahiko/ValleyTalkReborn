using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using StardewValley;

namespace ValleytalkReborn.Cutscene.Actions
{
    /// <summary>
    /// NPC 头顶对白气泡动作（MVP 实现）
    /// </summary>
    public sealed class SpeakAction : IDirectorAction
    {
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
            
            // 动态计算阅读时长：120ms/字符，范围 2000ms~5000ms
            _durationMs = Math.Clamp(text.Length * 120f, 2000f, 5000f);
        }

        public void Enter()
        {
            _elapsedMs = 0f;
            _skipped = false;
            _lastKeyState = Keyboard.GetState();
            _lastMouseState = Mouse.GetState();
            
            _npc?.showTextAboveHead(_text);
        }

        public bool Update(GameTime time)
        {
            _elapsedMs += (float)time.ElapsedGameTime.TotalMilliseconds;

            // 检测跳过输入（Space 或鼠标左键）
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
            // 无需清理，气泡会自动消失
        }
    }
}
