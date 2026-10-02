using Microsoft.Xna.Framework;

namespace ValleytalkReborn.Cutscene
{
    /// <summary>
    /// 导演动作抽象接口：极简状态机，纯帧轮询驱动，零反射。
    /// </summary>
    public interface IDirectorAction
    {
        /// <summary>
        /// 动作开始时触发（初始化寻路、触发表情、播放音效等）
        /// </summary>
        void Enter();

        /// <summary>
        /// 每帧更新。返回 true 代表动作圆满完成，推动下一幕；返回 false 保持当前动作。
        /// </summary>
        bool Update(GameTime time);

        /// <summary>
        /// 动作完成或被强制中断时调用（清理临时控制器、速度重置等）
        /// </summary>
        void Exit();

        /// <summary>
        /// 是否阻塞等待该动作完成。为 false 时立即启动后续动作以实现并发。
        /// </summary>
        bool WaitForCompletion { get; set; }
    }
}
