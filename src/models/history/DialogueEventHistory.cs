using System;
using System.Collections.Generic;
using System.Linq;
using StardewValley;

namespace ValleytalkReborn;

internal class DialogueEventHistory : IHistory
{
    public DialogueEventHistory(IEnumerable<NPC> listeners, IEnumerable<StardewValley.DialogueLine> dialogues, string eventName = "")
    {
        Listeners = listeners ?? Enumerable.Empty<NPC>();
        Dialogues = dialogues ?? Enumerable.Empty<StardewValley.DialogueLine>();
        EventName = eventName ?? string.Empty;
    }

    public string Format(string npcName)
    {
        // 修复：StardewValley.DialogueLine 在 1.6 中获取文本使用 x.Text
        var totalDialogue = string.Join(" : ", Dialogues.Where(x => x != null).Select(x => x.Text));
        var allListeners = string.Join(", ", Listeners.Where(x => x != null).Select(x => x.Name));
        var festivalNameString = string.IsNullOrWhiteSpace(EventName) 
            ? string.Empty 
            : Util.GetString("historyThirdPartyFestival", new { festivalName = EventName });

        return Util.GetString("historyDialogueFormat", new 
        { 
            npcName = npcName ?? string.Empty, 
            allListeners = allListeners, 
            festivalNameString = festivalNameString, 
            totalDialogue = totalDialogue 
        });
    }

    public IEnumerable<StardewValley.DialogueLine> Dialogues { get; }
    public IEnumerable<NPC> Listeners { get; }
    public string EventName { get; }
}