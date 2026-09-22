#nullable enable
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;

namespace ValleytalkReborn.UI;

/// <summary>
/// Tab 页抽象基类：持有 Hub 引用与布局边界，并提供接口的空默认实现。
/// </summary>
internal abstract class HubTabViewBase : IHubTabView
{
    protected readonly IntegratedHubMenu Hub;
    protected Rectangle MenuBounds;
    protected Rectangle ContentBounds;

    private string? _hoveredTooltip;

    protected HubTabViewBase(IntegratedHubMenu hub)
    {
        Hub = hub;
    }

    public virtual string HoveredTooltip => _hoveredTooltip ?? "";

    /// <summary>派生类在 Draw 期间调用以更新悬停提示。</summary>
    protected void SetHoveredTooltip(string? text) => _hoveredTooltip = text;

    /// <summary>本 Tab 是否存在未保存修改；无表单态的 Tab 恒 false。RulesTabView 直实现 IHubTabView、不经本基类，天然不受影响。</summary>
    public virtual bool HasUnsavedChanges => false;

    public virtual void OnActivated() { }
    public virtual void OnDeactivated() { }
    public virtual void Layout(Rectangle menuBounds, Rectangle contentBounds)
    {
        MenuBounds = menuBounds;
        ContentBounds = contentBounds;
    }
    public virtual void Update(GameTime time) { }
    public virtual void Draw(SpriteBatch b, int mx, int my) { }
    public virtual void DrawOverlay(SpriteBatch b) { }
    public virtual bool ReceiveLeftClick(int x, int y) => false;
    public virtual bool ReceiveScrollWheel(int direction) => false;
    public virtual bool ReceiveKeyPress(Keys key) => false;
    public virtual void LeftClickHeld(int x, int y) { }
    public virtual void ReleaseLeftClick(int x, int y) { }
    public virtual void OnReturnedFromChild() { }
    public virtual void RefreshFromHub() { }

    /// <summary>绘制悬停提示（委托到 HubUi）。</summary>
    protected static void DrawHoverTextCustom(SpriteBatch b, string text) => HubUi.DrawHoverTextCustom(b, text);
}
