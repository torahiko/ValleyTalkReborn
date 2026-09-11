using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;

namespace ValleytalkReborn
{
    public static class TextBoxCursorEnhancer
    {
        private class CursorState
        {
            public int Position = -1; // -1 表示默认位于末尾
        }

        private static readonly ConditionalWeakTable<TextBox, CursorState> States = new();
        private static IModHelper _helper;

        // 反射解析原版 protected 字段，做好 null 回退
        private static readonly FieldInfo FontField = AccessTools.Field(typeof(TextBox), "_font")
                                                   ?? AccessTools.Field(typeof(TextBox), "font");
        private static readonly FieldInfo TextColorField = AccessTools.Field(typeof(TextBox), "_textColor")
                                                        ?? AccessTools.Field(typeof(TextBox), "textColor");

        [ThreadStatic]
        private static bool _wasSelectedBeforeDraw;

        public static SpriteFont GetFont(TextBox box)
        {
            if (box != null && FontField?.GetValue(box) is SpriteFont sf)
            {
                return sf;
            }
            return Game1.smallFont;
        }

        public static Color GetTextColor(TextBox box)
        {
            if (box != null && TextColorField?.GetValue(box) is Color c)
            {
                return c;
            }
            return Game1.textColor;
        }

        public static int GetCursor(TextBox box)
        {
            if (box == null) return 0;
            var state = States.GetOrCreateValue(box);
            int len = box.Text?.Length ?? 0;
            if (state.Position < 0 || state.Position > len)
            {
                state.Position = len;
            }
            return state.Position;
        }

        public static void SetCursor(TextBox box, int pos)
        {
            if (box == null) return;
            var state = States.GetOrCreateValue(box);
            int len = box.Text?.Length ?? 0;
            state.Position = Math.Clamp(pos, 0, len);
        }

        public static void ApplyPatches(Harmony harmony, IModHelper helper)
        {
            _helper = helper;
            helper.Events.Input.ButtonPressed += OnButtonPressed;

            harmony.Patch(
                original: AccessTools.Method(typeof(TextBox), nameof(TextBox.RecieveTextInput), new[] { typeof(char) }),
                prefix: new HarmonyMethod(typeof(TextBoxCursorEnhancer), nameof(Prefix_RecieveTextInput))
            );

            harmony.Patch(
                original: AccessTools.Method(typeof(TextBox), nameof(TextBox.RecieveCommandInput), new[] { typeof(char) }),
                prefix: new HarmonyMethod(typeof(TextBoxCursorEnhancer), nameof(Prefix_RecieveCommandInput))
            );

            harmony.Patch(
                original: AccessTools.Method(typeof(TextBox), nameof(TextBox.Draw), new[] { typeof(SpriteBatch), typeof(bool) }),
                prefix: new HarmonyMethod(typeof(TextBoxCursorEnhancer), nameof(Prefix_Draw)),
                postfix: new HarmonyMethod(typeof(TextBoxCursorEnhancer), nameof(Postfix_Draw))
            );
        }

        private static void OnButtonPressed(object sender, ButtonPressedEventArgs e)
        {
            var subscriber = Game1.keyboardDispatcher?.Subscriber;
            if (subscriber is not TextBox textBox || !textBox.Selected) return;

            string text = textBox.Text ?? string.Empty;
            int cursor = GetCursor(textBox);

            // ★ 修复 1：支持鼠标左键点击定位光标（实现真正的"点选光标"）
            if (e.Button == SButton.MouseLeft)
            {
                var mousePos = Game1.getMousePosition(true);
                var bounds = new Rectangle(textBox.X, textBox.Y, textBox.Width, textBox.Height);
                if (bounds.Contains(mousePos))
                {
                    var font = GetFont(textBox);
                    float clickRelX = mousePos.X - (textBox.X + 16);
                    if (clickRelX <= 0)
                    {
                        SetCursor(textBox, 0);
                        return;
                    }

                    int bestIndex = text.Length;
                    float minDiff = float.MaxValue;
                    for (int i = 0; i <= text.Length; i++)
                    {
                        float w = font.MeasureString(text.Substring(0, i)).X;
                        float diff = Math.Abs(w - clickRelX);
                        if (diff < minDiff)
                        {
                            minDiff = diff;
                            bestIndex = i;
                        }
                    }
                    SetCursor(textBox, bestIndex);
                    return;
                }
            }

            bool ctrl = Game1.input.GetKeyboardState().IsKeyDown(Keys.LeftControl) ||
                        Game1.input.GetKeyboardState().IsKeyDown(Keys.RightControl);

            switch (e.Button)
            {
                case SButton.Left:
                    if (ctrl)
                    {
                        int newPos = cursor - 1;
                        while (newPos > 0 && char.IsWhiteSpace(text[newPos])) newPos--;
                        while (newPos > 0 && !char.IsWhiteSpace(text[newPos - 1])) newPos--;
                        SetCursor(textBox, Math.Max(0, newPos));
                    }
                    else
                    {
                        SetCursor(textBox, cursor - 1);
                    }
                    _helper.Input.Suppress(e.Button);
                    break;

                case SButton.Right:
                    if (ctrl)
                    {
                        int newPos = cursor;
                        while (newPos < text.Length && !char.IsWhiteSpace(text[newPos])) newPos++;
                        while (newPos < text.Length && char.IsWhiteSpace(text[newPos])) newPos++;
                        SetCursor(textBox, Math.Min(text.Length, newPos));
                    }
                    else
                    {
                        SetCursor(textBox, cursor + 1);
                    }
                    _helper.Input.Suppress(e.Button);
                    break;

                case SButton.Home:
                    SetCursor(textBox, 0);
                    _helper.Input.Suppress(e.Button);
                    break;

                case SButton.End:
                    SetCursor(textBox, text.Length);
                    _helper.Input.Suppress(e.Button);
                    break;

                case SButton.Delete:
                    if (cursor < text.Length)
                    {
                        textBox.Text = text.Remove(cursor, 1);
                        SetCursor(textBox, cursor);
                    }
                    _helper.Input.Suppress(e.Button);
                    break;
            }
        }

        private static bool Prefix_RecieveTextInput(TextBox __instance, char inputChar)
        {
            if (__instance.numbersOnly && !char.IsDigit(inputChar)) return false;
            if (__instance.textLimit != -1 && (__instance.Text?.Length ?? 0) >= __instance.textLimit) return false;

            string text = __instance.Text ?? string.Empty;
            int cursor = GetCursor(__instance);

            __instance.Text = text.Insert(cursor, inputChar.ToString());
            SetCursor(__instance, cursor + 1);
            return false;
        }

        private static bool Prefix_RecieveCommandInput(TextBox __instance, char command)
        {
            if (command != '\b') return true;

            string text = __instance.Text ?? string.Empty;
            int cursor = GetCursor(__instance);

            if (cursor > 0 && text.Length > 0)
            {
                __instance.Text = text.Remove(cursor - 1, 1);
                SetCursor(__instance, cursor - 1);
            }
            return false;
        }

        // Prefix：临时将 Selected 设为 false，以阻止原版 Draw 内部在字符串最末尾画死光标
        private static void Prefix_Draw(TextBox __instance)
        {
            _wasSelectedBeforeDraw = __instance.Selected;
            if (_wasSelectedBeforeDraw)
            {
                __instance.Selected = false;
            }
        }

        // Postfix：还原 Selected 状态，并在正确的虚拟光标位置绘制闪烁光标
        private static void Postfix_Draw(TextBox __instance, SpriteBatch spriteBatch)
        {
            if (!_wasSelectedBeforeDraw) return;
            __instance.Selected = true;

            if (DateTime.UtcNow.Millisecond % 1000 < 500) return;

            try
            {
                var font = GetFont(__instance);
                var color = GetTextColor(__instance);

                string fullText = __instance.Text ?? string.Empty;
                if (__instance.PasswordBox)
                {
                    fullText = new string('•', fullText.Length);
                }

                // 与原版 Draw 保持一致的超长字符左侧剔除计算
                string visibleText = fullText;
                Vector2 size = font.MeasureString(visibleText);
                int trimmedCount = 0;
                while (size.X > (__instance.Width - 16) && visibleText.Length > 0)
                {
                    visibleText = visibleText.Substring(1);
                    trimmedCount++;
                    size = font.MeasureString(visibleText);
                }

                int cursor = GetCursor(__instance);
                int visibleCursor = cursor - trimmedCount;

                // ★ 修复 4：光标越界或长文本移动时强制在边界可见，避免左移时视觉丢失
                visibleCursor = Math.Clamp(visibleCursor, 0, visibleText.Length);
                string sub = visibleText.Substring(0, visibleCursor);
                float caretXOffset = font.MeasureString(sub).X;

                int caretY = __instance.Y + ((font == Game1.dialogueFont) ? 8 : 12);
                int caretHeight = (int)font.MeasureString("W").Y;

                spriteBatch.Draw(
                    Game1.staminaRect,
                    new Rectangle((int)(__instance.X + 16 + caretXOffset), caretY, 2, Math.Max(24, caretHeight)),
                    color
                );
            }
            catch
            {
                // 静默兜底，避免渲染阶段异常抛出导致游戏黑屏
            }
        }
    }
}
