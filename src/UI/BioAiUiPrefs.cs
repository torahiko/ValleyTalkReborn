namespace ValleytalkReborn
{
    /// <summary>
    /// 跨菜单共享的 Bio-Ai UI 轻量状态（Memory 作用域）。
    /// 当前承载"深度思考"胶囊记忆：问卷屏与单独润色弹窗共用同一源，
    /// 避免 BioAiPromptDialog._enableThinking 私有静态字段与问卷屏状态分裂。
    /// </summary>
    internal static class BioAiUiPrefs
    {
        public static bool EnableThinking;
    }
}
