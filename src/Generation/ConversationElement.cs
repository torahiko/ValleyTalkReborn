using System;

namespace ValleytalkReborn
{
    /// <summary>
    /// 表示单条对话元素（不可变数据记录）
    /// </summary>
    public record ConversationElement(string Text, bool IsPlayerLine)
    {
        public Guid Id { get; init; } = Guid.NewGuid();
    }
}