using StardewModdingAPI.Events;

namespace ValleytalkReborn;

/// <summary>
/// 对话模块接口：所有对话子系统（Ambient Bark、A2A）必须实现此接口，
/// 由 DialogueCoordinator 统一订阅和管理 SMAPI 游戏循环事件。
/// </summary>
internal interface IDialogueModule
{
    /// <summary>
    /// 游戏启动时调用（SMAPI GameLaunched）。
    /// </summary>
    void OnGameLaunched();

    /// <summary>
    /// 新一天开始时调用（SMAPI DayStarted）。
    /// </summary>
    void OnDayStarted();

    /// <summary>
    /// 返回标题画面时调用（SMAPI ReturnedToTitle）。
    /// </summary>
    void OnReturnedToTitle();
}
