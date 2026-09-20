using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;

namespace ValleytalkReborn.UI;

/// <summary>
/// Hub 四个 Tab 页的统一视图契约。
/// 每个 Tab 页实现本接口，Hub 通过本接口驱动页面生命周期。
/// </summary>
internal interface IHubTabView
{
    /// <summary>进入本页时调用（列表页在此自清零滚动）。</summary>
    void OnActivated();

    /// <summary>离开本页时调用（关下拉/释放键盘）。</summary>
    void OnDeactivated();

    /// <summary>计算布局矩形。</summary>
    void Layout(Rectangle menuBounds, Rectangle contentBounds);

    /// <summary>每帧更新逻辑。</summary>
    void Update(GameTime time);

    /// <summary>绘制页面内容（含本页底部按钮）。</summary>
    void Draw(SpriteBatch b, int mx, int my);

    /// <summary>关闭按钮之后、提示之前的顶层弹层。</summary>
    void DrawOverlay(SpriteBatch b);

    /// <summary>接收左键点击。返回 true 表示已消费。</summary>
    bool ReceiveLeftClick(int x, int y);

    /// <summary>接收滚轮。返回 true 表示已消费。</summary>
    bool ReceiveScrollWheel(int direction);

    /// <summary>接收按键。返回 true 表示已消费（含 ESC 关下拉分支）。</summary>
    bool ReceiveKeyPress(Keys key);

    /// <summary>左键按住。</summary>
    void LeftClickHeld(int x, int y);

    /// <summary>释放左键。</summary>
    void ReleaseLeftClick(int x, int y);

    /// <summary>Hub.RefreshEntries 转发。</summary>
    void RefreshFromHub();

    /// <summary>Draw 期间写入，Hub 每帧读取绘制。</summary>
    string HoveredTooltip { get; }
}
