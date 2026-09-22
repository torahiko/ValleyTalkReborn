using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace ValleytalkReborn.UI;

/// <summary>世界设定子页抽象基类。持有 Hub 引用，定义子页生命周期契约。</summary>
internal abstract class WorldSubPageBase
{
    protected readonly IntegratedHubMenu Hub;

    protected WorldSubPageBase(IntegratedHubMenu hub)
    {
        Hub = hub;
    }

    public abstract void OnShown();
    public abstract void Layout(Rectangle area);
    public abstract void Update(GameTime time);
    public abstract void Draw(SpriteBatch b, Rectangle area, int mx, int my);
    public abstract bool ReceiveLeftClick(int x, int y);

    // ── 鼠标按住拖拽与释放生命周期钩子（供滑动条、滑块拖动等交互使用） ──
    public virtual void LeftClickHeld(int x, int y) { }
    public virtual void ReleaseLeftClick(int x, int y) { }

    public virtual bool ReceiveScrollWheel(int direction) => false;
    public virtual bool ReceiveKeyPress(Keys key) => false;
    public virtual void OnHidden() { }

    /// <summary>将本子页恢复为内容包合成基线：删除本页用户覆盖层文件并重建合并视图。默认无操作。</summary>
    public virtual void ResetToBaseline() { }

    /// <summary>本子页是否存在未提交的表单编辑（基于装载时指纹快照比较）。</summary>
    public virtual bool HasUnsavedChanges => false;
    /// <summary>提交本页未保存的表单编辑。返回 true=已一致或提交成功；实现须调用本页既有保存方法并用 HasUnsavedChanges 复查。</summary>
    public virtual bool TryCommitUnsavedChanges() => true;
}