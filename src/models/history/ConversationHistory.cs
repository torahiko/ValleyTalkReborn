using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace ValleytalkReborn;

internal class ConversationHistory : IHistory
{
    private readonly Guid _id;

    [Obsolete("Use ConversationElements instead")]
    public string[] chatHistory
    {
        get => ConversationElements.Select(ce => ce?.Text ?? string.Empty).ToArray();
        set
        {
            ConversationElements.Clear();
            if (value != null)
            {
                for (int i = 0; i < value.Length; i++)
                {
                    ConversationElements.Add(new ConversationElement(value[i], i % 2 != 0));
                }
            }
        }
    }

    public bool ShouldSerializechatHistory() => false;

    [JsonIgnore]
    public Guid Id => _id;

    public List<ConversationElement> ConversationElements { get; set; } = new List<ConversationElement>();

    [JsonConstructor]
    public ConversationHistory()
    {
        _id = Guid.NewGuid();
    }

    public ConversationHistory(List<ConversationElement> chatHistory)
    {
        ConversationElements = chatHistory ?? new List<ConversationElement>();
        _id = ConversationElements.FirstOrDefault()?.Id ?? Guid.NewGuid();
    }

    public string Format(string npcName)
    {
        if (ConversationElements == null || ConversationElements.Count == 0)
        {
            return Util.GetString("historyConversationFormat", new { builder = string.Empty });
        }

        var builder = new StringBuilder();
        // 优化：将玩家 Label 提取到循环外，避免每次遍历都重复查找翻译字典
        var farmerLabel = Util.GetString("generalFarmerLabel");
        var currentNpcName = string.IsNullOrWhiteSpace(npcName) ? "NPC" : npcName;

        for (int i = 0; i < ConversationElements.Count; i++)
        {
            var elem = ConversationElements[i];
            if (elem == null) continue;

            // 优化：使用 StringBuilder 链式追加，减少临时字符串分配
            builder.Append("- ")
                   .Append(!elem.IsPlayerLine ? currentNpcName : farmerLabel)
                   .Append(": ")
                   .Append(elem.Text)
                   .Append(" --- ");
        }

        return Util.GetString("historyConversationFormat", new { builder = builder.ToString() });
    }
}