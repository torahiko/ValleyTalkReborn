using System;
using System.Linq;
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
        private sealed class CursorState
        {
            public int Position = -1; // -1 表示默认位于末尾
        }

        // 状态表键类型泛化为 object：vanilla TextBox 与 Spacebox 共用同一张表（实例互不相同，无冲突）
        private static readonly ConditionalWeakTable<object, CursorState> States = new();
        private static IModHelper _helper;

        // 常规生命周期/跟踪日志
        private static void LogTrace(string message)
        {
            if (ModEntry.Config?.Debug == true)
            {
                ModEntry.SMonitor.Log("[TBCursor] " + message, LogLevel.Trace);
            }
        }

        // 异常诊断日志：提升为 Debug 级别，便于调试模式下排查错误
        private static void LogDebug(string message)
        {
            if (ModEntry.Config?.Debug == true)
            {
                ModEntry.SMonitor.Log("[TBCursor] " + message, LogLevel.Debug);
            }
        }

        // 反射解析原版 protected 字段，做好 null 回退
        private static readonly FieldInfo FontField = AccessTools.Field(typeof(TextBox), "_font")
                                                   ?? AccessTools.Field(typeof(TextBox), "font");
        private static readonly FieldInfo TextColorField = AccessTools.Field(typeof(TextBox), "_textColor")
                                                        ?? AccessTools.Field(typeof(TextBox), "textColor");

        [ThreadStatic]
        private static bool _wasSelectedBeforeDraw;

        // ════════════════════════════════════════════════════════════════════════════
        // GMCM SpaceShared.UI.Textbox 运行时反射缓存（ApplyPatches 期解析一次，概念只读）
        // ════════════════════════════════════════════════════════════════════════════
        private const float SpaceboxTextareaWidth = 192f; // Draw trims while measured width > 192f (decompiled GMCM 1.16.0)

        private static Type SpaceboxType;
        private static PropertyInfo SpaceboxStringProperty;
        private static FieldInfo SpaceboxSelectedImplField;
        private static PropertyInfo SpaceboxCallbackProperty; // 实为属性（Action<Element>），非字段
        private static MemberInfo SpaceboxPositionMember; // PropertyInfo 优先，FieldInfo 兜底
        private static Type ElementType; // SpaceboxType.BaseType 链上首个 SpaceShared.UI 命名空间类型

        [ThreadStatic]
        private static bool _spaceboxWasSelected;

        private static int GetCursorCore(object box, int textLength)
        {
            if (box == null) return 0;
            var state = States.GetOrCreateValue(box);
            if (state.Position < 0 || state.Position > textLength)
            {
                state.Position = textLength;
            }
            return state.Position;
        }

        private static void SetCursorCore(object box, int pos, int textLength)
        {
            if (box == null) return;
            var state = States.GetOrCreateValue(box);
            state.Position = Math.Clamp(pos, 0, textLength);
        }

        private static string SpaceboxGetString(object box)
        {
            return SpaceboxStringProperty.GetValue(box) as string ?? string.Empty;
        }

        private static void FireSpaceboxCallback(object box)
        {
            object cb = SpaceboxCallbackProperty.GetValue(box);
            if (cb is Delegate d) d.DynamicInvoke(box);
        }

        private static Vector2 GetSpaceboxPosition(object box)
        {
            object val = SpaceboxPositionMember is PropertyInfo pi ? pi.GetValue(box)
                        : (SpaceboxPositionMember is FieldInfo fi ? fi.GetValue(box) : null);
            return val is Vector2 v ? v : Vector2.Zero;
        }

        // 与 vanilla Postfix_Draw 完全一致的左剔除 + 前缀测宽算法
        private static float ComputeCaretOffset(SpriteFont font, string fullText, int cursor, float maxTextWidth, out int trimmedCount)
        {
            string visibleText = fullText;
            Vector2 size = font.MeasureString(visibleText);
            trimmedCount = 0;
            while (size.X > maxTextWidth && visibleText.Length > 0)
            {
                visibleText = visibleText.Substring(1);
                trimmedCount++;
                size = font.MeasureString(visibleText);
            }
            int visibleCursor = cursor - trimmedCount;
            visibleCursor = Math.Clamp(visibleCursor, 0, visibleText.Length);
            string sub = visibleText.Substring(0, visibleCursor);
            return font.MeasureString(sub).X;
        }

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
            return GetCursorCore(box, box?.Text?.Length ?? 0);
        }

        public static void SetCursor(TextBox box, int pos)
        {
            SetCursorCore(box, pos, box?.Text?.Length ?? 0);
        }

        public static void ApplyPatches(Harmony harmony, IModHelper helper)
        {
            _helper = helper;
            helper.Events.Input.ButtonPressed += OnButtonPressed;

            // 1. 原版 TextBox 输入补丁（char 与 string 重载均覆盖）
            harmony.Patch(
                original: AccessTools.Method(typeof(TextBox), nameof(TextBox.RecieveTextInput), new[] { typeof(char) }),
                prefix: new HarmonyMethod(typeof(TextBoxCursorEnhancer), nameof(Prefix_RecieveTextInput))
            );

            harmony.Patch(
                original: AccessTools.Method(typeof(TextBox), nameof(TextBox.RecieveTextInput), new[] { typeof(string) }),
                prefix: new HarmonyMethod(typeof(TextBoxCursorEnhancer), nameof(Prefix_RecieveTextInputString))
            );

            harmony.Patch(
                original: AccessTools.Method(typeof(TextBox), nameof(TextBox.RecieveCommandInput), new[] { typeof(char) }),
                prefix: new HarmonyMethod(typeof(TextBoxCursorEnhancer), nameof(Prefix_RecieveCommandInput))
            );

            // 2. 原版 TextBox 绘制补丁（追加 Finalizer 兜底保证 Selected 必恢复）
            harmony.Patch(
                original: AccessTools.Method(typeof(TextBox), nameof(TextBox.Draw), new[] { typeof(SpriteBatch), typeof(bool) }),
                prefix: new HarmonyMethod(typeof(TextBoxCursorEnhancer), nameof(Prefix_Draw)),
                postfix: new HarmonyMethod(typeof(TextBoxCursorEnhancer), nameof(Postfix_Draw)),
                finalizer: new HarmonyMethod(typeof(TextBoxCursorEnhancer), nameof(Finalizer_Draw))
            );

            ApplySpaceboxPatches(harmony);
        }

        private static void ApplySpaceboxPatches(Harmony harmony)
        {
            // 多程序集消歧 —— 收集全名 "SpaceShared.UI.Textbox" 的类型
            Type match = null;
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
                catch { continue; }
                foreach (Type t in types)
                {
                    if (t == null || t.FullName != "SpaceShared.UI.Textbox") continue;
                    if (match == null)
                    {
                        match = t;
                    }
                    else if (asm.GetName().Name.IndexOf("GenericModConfigMenu", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        match = t;
                    }
                }
            }
            SpaceboxType = match;
            if (SpaceboxType == null)
            {
                LogTrace("SpaceShared.UI.Textbox not found — spacebox cursor enhancement bypassed.");
                return;
            }

            SpaceboxStringProperty = AccessTools.Property(SpaceboxType, "String");
            SpaceboxSelectedImplField = AccessTools.Field(SpaceboxType, "SelectedImpl");
            SpaceboxCallbackProperty = AccessTools.Property(SpaceboxType, "Callback");

            for (Type t = SpaceboxType; t != null; t = t.BaseType)
            {
                MemberInfo mi = (MemberInfo)AccessTools.Property(t, "Position") ?? AccessTools.Field(t, "Position");
                if (mi != null)
                {
                    SpaceboxPositionMember = mi;
                    break;
                }
            }

            for (Type t = SpaceboxType.BaseType; t != null; t = t.BaseType)
            {
                if (t.Namespace == "SpaceShared.UI")
                {
                    ElementType = t;
                    break;
                }
            }

            bool hasInput = SpaceboxStringProperty != null && SpaceboxSelectedImplField != null && SpaceboxCallbackProperty != null;
            bool hasDraw = SpaceboxPositionMember != null;
            if (!hasInput)
            {
                string missing = (SpaceboxStringProperty == null ? "String " : "") +
                                 (SpaceboxSelectedImplField == null ? "SelectedImpl " : "") +
                                 (SpaceboxCallbackProperty == null ? "Callback" : "");
                LogTrace($"Spacebox input members missing ({missing.TrimEnd()}) — spacebox cursor disabled.");
                return;
            }
            if (!hasDraw)
            {
                LogTrace("Spacebox Position member unavailable — caret will be invisible; input patches still mounted.");
            }

            string asmName = SpaceboxType.Assembly.GetName().Name;
            string elementName = ElementType?.Name ?? "(unknown)";

            harmony.Patch(
                original: AccessTools.Method(SpaceboxType, "RecieveTextInput", new[] { typeof(char) }),
                prefix: new HarmonyMethod(typeof(TextBoxCursorEnhancer), nameof(SpaceboxPrefix_RecieveTextInputChar))
            );
            harmony.Patch(
                original: AccessTools.Method(SpaceboxType, "RecieveTextInput", new[] { typeof(string) }),
                prefix: new HarmonyMethod(typeof(TextBoxCursorEnhancer), nameof(SpaceboxPrefix_RecieveTextInputString))
            );
            harmony.Patch(
                original: AccessTools.Method(SpaceboxType, "RecieveCommandInput", new[] { typeof(char) }),
                prefix: new HarmonyMethod(typeof(TextBoxCursorEnhancer), nameof(SpaceboxPrefix_RecieveCommandInput))
            );

            if (hasDraw)
            {
                harmony.Patch(
                    original: AccessTools.Method(SpaceboxType, "Draw", new[] { typeof(SpriteBatch) }),
                    prefix: new HarmonyMethod(typeof(TextBoxCursorEnhancer), nameof(SpaceboxPrefix_Draw)),
                    postfix: new HarmonyMethod(typeof(TextBoxCursorEnhancer), nameof(SpaceboxPostfix_Draw)),
                    finalizer: new HarmonyMethod(typeof(TextBoxCursorEnhancer), nameof(SpaceboxFinalizer_Draw))
                );
            }

            LogTrace($"Spacebox patches mounted: RecieveTextInput(char)->{SpaceboxType.Name}, RecieveTextInput(string)->{SpaceboxType.Name}, RecieveCommandInput(char)->{SpaceboxType.Name}, Draw(SpriteBatch)->{(hasDraw ? SpaceboxType.Name : "SKIPPED")} (asm={asmName}, base={elementName})");
        }

        private static void OnButtonPressed(object sender, ButtonPressedEventArgs e)
        {
            var subscriber = Game1.keyboardDispatcher?.Subscriber;

            // GMCM Spacebox 分支：精确类型 + SelectedImpl 命中才进入，命中即消费
            if (subscriber != null && SpaceboxType != null
                && subscriber.GetType() == SpaceboxType
                && SpaceboxSelectedImplField.GetValue(subscriber) is true)
            {
                if (TryHandleSpaceboxButton(subscriber, e.Button))
                {
                    _helper.Input.Suppress(e.Button);
                }
                return;
            }

            if (subscriber is not TextBox textBox || !textBox.Selected) return;

            string text = textBox.Text ?? string.Empty;
            int cursor = GetCursor(textBox);

            // 鼠标左键点击定位光标
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

        private static bool Prefix_RecieveTextInputString(TextBox __instance, string text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            if (__instance.numbersOnly)
            {
                text = new string(text.Where(char.IsDigit).ToArray());
                if (text.Length == 0) return false;
            }

            string cur = __instance.Text ?? string.Empty;
            if (__instance.textLimit != -1 && cur.Length + text.Length > __instance.textLimit)
            {
                int allowed = __instance.textLimit - cur.Length;
                if (allowed <= 0) return false;
                text = text.Substring(0, allowed);
            }

            int cursor = GetCursor(__instance);
            __instance.Text = cur.Insert(cursor, text);
            SetCursor(__instance, cursor + text.Length);
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

        private static void Prefix_Draw(TextBox __instance)
        {
            _wasSelectedBeforeDraw = __instance.Selected;
            if (_wasSelectedBeforeDraw)
            {
                __instance.Selected = false;
            }
        }

        private static void Postfix_Draw(TextBox __instance, SpriteBatch spriteBatch)
        {
            if (!_wasSelectedBeforeDraw) return;
            __instance.Selected = true;
            _wasSelectedBeforeDraw = false;

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

                int trimmedCount;
                float caretXOffset = ComputeCaretOffset(font, fullText, GetCursor(__instance), __instance.Width - 16, out trimmedCount);

                int caretY = __instance.Y + ((font == Game1.dialogueFont) ? 8 : 12);
                int caretHeight = (int)font.MeasureString("W").Y;

                spriteBatch.Draw(
                    Game1.staminaRect,
                    new Rectangle((int)(__instance.X + 16 + caretXOffset), caretY, 2, Math.Max(24, caretHeight)),
                    color
                );
            }
            catch (Exception ex)
            {
                LogDebug($"Vanilla TextBox Postfix_Draw error: {ex.Message}");
            }
        }

        private static Exception Finalizer_Draw(TextBox __instance, Exception __exception)
        {
            if (__exception != null && _wasSelectedBeforeDraw && __instance != null)
            {
                __instance.Selected = true;
                LogDebug($"TextBox Draw error: {__exception.Message}");
            }
            _wasSelectedBeforeDraw = false;
            return null;
        }

        // ════════════════════════════════════════════════════════════════════════════
        // SpaceShared.UI.Textbox（GMCM 内嵌）光标支持
        // ════════════════════════════════════════════════════════════════════════════

        private static bool SpaceboxPrefix_RecieveTextInputChar(object __instance, [HarmonyArgument(0)] char inputChar)
        {
            if (__instance.GetType() != SpaceboxType) return true;
            if (SpaceboxSelectedImplField.GetValue(__instance) is not true) return true;
            try
            {
                if (char.IsControl(inputChar)) return true;
                string text = SpaceboxGetString(__instance);
                int cursor = GetCursorCore(__instance, text.Length);
                string result = text.Insert(cursor, inputChar.ToString());
                SpaceboxStringProperty.SetValue(__instance, result);
                SetCursorCore(__instance, cursor + 1, result.Length);
                FireSpaceboxCallback(__instance);
                return false;
            }
            catch (Exception ex)
            {
                LogDebug($"SpaceboxPrefix_RecieveTextInputChar error: {ex.Message}");
                return true;
            }
        }

        private static bool SpaceboxPrefix_RecieveTextInputString(object __instance, [HarmonyArgument(0)] string text)
        {
            if (__instance.GetType() != SpaceboxType) return true;
            if (SpaceboxSelectedImplField.GetValue(__instance) is not true) return true;
            try
            {
                string cur = SpaceboxGetString(__instance);
                int cursor = GetCursorCore(__instance, cur.Length);
                string insert = text ?? string.Empty;
                string result = cur.Insert(cursor, insert);
                SpaceboxStringProperty.SetValue(__instance, result);
                SetCursorCore(__instance, cursor + insert.Length, result.Length);
                FireSpaceboxCallback(__instance);
                return false;
            }
            catch (Exception ex)
            {
                LogDebug($"SpaceboxPrefix_RecieveTextInputString error: {ex.Message}");
                return true;
            }
        }

        private static bool SpaceboxPrefix_RecieveCommandInput(object __instance, [HarmonyArgument(0)] char command)
        {
            if (__instance.GetType() != SpaceboxType) return true;
            if (SpaceboxSelectedImplField.GetValue(__instance) is not true) return true;
            try
            {
                if (command != '\b') return true;
                string text = SpaceboxGetString(__instance);
                int cursor = GetCursorCore(__instance, text.Length);
                if (cursor > 0)
                {
                    string result = text.Remove(cursor - 1, 1);
                    SpaceboxStringProperty.SetValue(__instance, result);
                    SetCursorCore(__instance, cursor - 1, result.Length);
                    FireSpaceboxCallback(__instance);
                }
                return false;
            }
            catch (Exception ex)
            {
                LogDebug($"SpaceboxPrefix_RecieveCommandInput error: {ex.Message}");
                return true;
            }
        }

        private static void SpaceboxPrefix_Draw(object __instance)
        {
            if (__instance.GetType() != SpaceboxType) return;
            if (SpaceboxSelectedImplField.GetValue(__instance) is not true) return;
            _spaceboxWasSelected = true;
            SpaceboxSelectedImplField.SetValue(__instance, false);
        }

        private static void SpaceboxPostfix_Draw(object __instance, [HarmonyArgument(0)] SpriteBatch b)
        {
            if (!_spaceboxWasSelected) return;                       // 1. 先判标志
            try
            {
                SpaceboxSelectedImplField.SetValue(__instance, true); // 2. 恢复 SelectedImpl
                if (DateTime.UtcNow.Millisecond < 500) return;        // 3. 闪烁节奏

                // 4. 位置
                Vector2 pos = GetSpaceboxPosition(__instance);
                // 5. 文本 + 光标
                string text = SpaceboxGetString(__instance);
                int cursor = GetCursorCore(__instance, text.Length);
                // 6. 偏移
                float offset = ComputeCaretOffset(Game1.smallFont, text, cursor, SpaceboxTextareaWidth, out _);

                int rectX = (int)pos.X + 16 + (int)offset + 2;        // 7. 绘制光标竖线
                int rectY = (int)pos.Y + 8;

                // 使用 GMCM 传入当前正在 Begin 周期内的 SpriteBatch，回退使用 Game1.spriteBatch
                SpriteBatch targetBatch = b ?? Game1.spriteBatch;
                targetBatch.Draw(
                    Game1.staminaRect,
                    new Rectangle(rectX, rectY, 4, 32),
                    Game1.textColor
                );
            }
            catch (Exception ex)
            {
                LogDebug($"Spacebox caret draw failed: {ex.Message}");
            }
            finally
            {
                _spaceboxWasSelected = false;
            }
        }

        private static Exception SpaceboxFinalizer_Draw(object __instance, Exception __exception)
        {
            if (__exception != null && _spaceboxWasSelected)
            {
                SpaceboxSelectedImplField.SetValue(__instance, true);
                LogDebug($"Spacebox Draw error: {__exception.Message}");
            }
            _spaceboxWasSelected = false;
            return null;
        }

        private static bool TryHandleSpaceboxButton(object subscriber, SButton button)
        {
            string text = SpaceboxGetString(subscriber);
            int len = text.Length;
            int cursor = GetCursorCore(subscriber, len);

            bool ctrl = Game1.input.GetKeyboardState().IsKeyDown(Keys.LeftControl) ||
                        Game1.input.GetKeyboardState().IsKeyDown(Keys.RightControl);

            switch (button)
            {
                case SButton.MouseLeft:
                    {
                        Vector2 pos = GetSpaceboxPosition(subscriber);
                        var mousePos = Game1.getMousePosition(true);
                        var bounds = new Rectangle((int)pos.X, (int)pos.Y, 192, 48);
                        if (!bounds.Contains(mousePos)) return false;
                        var font = Game1.smallFont;
                        float clickRelX = mousePos.X - (pos.X + 16);
                        if (clickRelX <= 0)
                        {
                            SetCursorCore(subscriber, 0, len);
                            return false;
                        }
                        int bestIndex = len;
                        float minDiff = float.MaxValue;
                        for (int i = 0; i <= len; i++)
                        {
                            float w = font.MeasureString(text.Substring(0, i)).X;
                            float diff = Math.Abs(w - clickRelX);
                            if (diff < minDiff)
                            {
                                minDiff = diff;
                                bestIndex = i;
                            }
                        }
                        int trimmedCount;
                        ComputeCaretOffset(font, text, len, SpaceboxTextareaWidth, out trimmedCount);
                        SetCursorCore(subscriber, bestIndex + trimmedCount, len);
                        return false;
                    }

                case SButton.Left:
                    if (ctrl)
                    {
                        int newPos = cursor - 1;
                        while (newPos > 0 && char.IsWhiteSpace(text[newPos])) newPos--;
                        while (newPos > 0 && !char.IsWhiteSpace(text[newPos - 1])) newPos--;
                        SetCursorCore(subscriber, Math.Max(0, newPos), len);
                    }
                    else
                    {
                        SetCursorCore(subscriber, cursor - 1, len);
                    }
                    return true;

                case SButton.Right:
                    if (ctrl)
                    {
                        int newPos = cursor;
                        while (newPos < len && !char.IsWhiteSpace(text[newPos])) newPos++;
                        while (newPos < len && char.IsWhiteSpace(text[newPos])) newPos++;
                        SetCursorCore(subscriber, Math.Min(len, newPos), len);
                    }
                    else
                    {
                        SetCursorCore(subscriber, cursor + 1, len);
                    }
                    return true;

                case SButton.Home:
                    SetCursorCore(subscriber, 0, len);
                    return true;

                case SButton.End:
                    SetCursorCore(subscriber, len, len);
                    return true;

                case SButton.Delete:
                    if (cursor < len)
                    {
                        string result = text.Remove(cursor, 1);
                        SpaceboxStringProperty.SetValue(subscriber, result);
                        SetCursorCore(subscriber, cursor, result.Length);
                        FireSpaceboxCallback(subscriber);
                    }
                    return true;

                default:
                    return false;
            }
        }
    }
}