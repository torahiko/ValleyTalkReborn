using StardewModdingAPI.Events;

namespace ValleytalkReborn;

/// <summary>
/// A2A（Agent-to-Agent）模块：负责 NPC 间多人对话的生命周期管理。
/// 持有独立的 A2ASessionManager 实例。
/// 不访问 Ambient Bark 私有状态。
/// </summary>
internal sealed class A2AModule : IDialogueModule
{
    private readonly A2ASessionManager _sessionManager;

    internal A2AModule(A2ASessionManager sessionManager)
    {
        _sessionManager = sessionManager;
    }

    /// <summary>
    /// 游戏启动时调用。初始化 PolyamorySweetLove 兼容桥接。
    /// </summary>
    public void OnGameLaunched()
    {
        PolyamorySweetLoveBridge.TryInitialize();
    }

    /// <summary>
    /// 新一天开始时调用。
    /// </summary>
    public void OnDayStarted()
    {
        _sessionManager.OnDayStarted();
    }

    /// <summary>
    /// 返回标题画面时调用。
    /// </summary>
    public void OnReturnedToTitle()
    {
        _sessionManager.OnReturnedToTitle();
    }

    /// <summary>
    /// 内部管理器引用。
    /// </summary>
    internal A2ASessionManager SessionManager => _sessionManager;
}