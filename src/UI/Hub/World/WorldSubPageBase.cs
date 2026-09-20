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

    public virtual bool ReceiveScrollWheel(int direction) => false;
    public virtual bool ReceiveKeyPress(Keys key) => false;
    public virtual void OnHidden() { }
}
