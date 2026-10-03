#nullable enable
using System;
using System.Collections.Generic;

namespace ValleytalkReborn.Cutscene.Storage
{
    /// <summary>
    /// 归档剧本持久化模型
    /// </summary>
    public sealed class ArchivedCutscene
    {
        /// <summary>唯一标识</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>剧本标题</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>创建/开演时间</summary>
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        /// <summary>上演场景名称（地图名）</summary>
        public string LocationName { get; set; } = string.Empty;

        /// <summary>参演 NPC 内部名列表</summary>
        public List<string> ActorNames { get; set; } = new();

        /// <summary>
        /// 录制时各演员的站位（瓦片坐标与朝向）。
        /// 回放时据此在录制现场复现开场队形：回放回归录制地图，按录制坐标落位。
        /// </summary>
        public List<ArchivedActorStance> ActorStances { get; set; } = new();

        /// <summary>
        /// 录制时农夫自身的站位与朝向：回放时农夫经此落回录制现场。
        /// TileX/TileY 双零视为旧归档缺记录，回放时走首位演员站位兜底。
        /// </summary>
        public ArchivedActorStance PlayerStance { get; set; } = new();

        /// <summary>用户意图或剧本基调提示词</summary>
        public string UserIntent { get; set; } = string.Empty;

        /// <summary>LLM 原始生成的 JSON IR 字符串</summary>
        public string RawJson { get; set; } = string.Empty;

        /// <summary>动作数量</summary>
        public int ActionCount { get; set; }
    }

    /// <summary>
    /// 归档演员站位记录：回放时的落位锚点
    /// </summary>
    public sealed class ArchivedActorStance
    {
        /// <summary>NPC 内部名</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>录制时的瓦片 X 坐标</summary>
        public int TileX { get; set; }

        /// <summary>录制时的瓦片 Y 坐标</summary>
        public int TileY { get; set; }

        /// <summary>录制时的朝向 (0=上, 1=右, 2=下, 3=左)</summary>
        public int Facing { get; set; }
    }
}
