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

        /// <summary>用户意图或剧本基调提示词</summary>
        public string UserIntent { get; set; } = string.Empty;

        /// <summary>LLM 原始生成的 JSON IR 字符串</summary>
        public string RawJson { get; set; } = string.Empty;

        /// <summary>动作数量</summary>
        public int ActionCount { get; set; }
    }
}
