using System;
using System.Collections.Generic;
using System.Linq;
using StardewValley;

namespace ValleytalkReborn;

internal class DialogueEventOverheard : IHistory
{
    public string Name { get; }
    // 明确使用 StardewValley.DialogueLine
    public IEnumerable<StardewValley.DialogueLine> Dialogues { get; }

    public DialogueEventOverheard(string name, IEnumerable<StardewValley.DialogueLine> filteredDialogues)
    {
        Name = name ?? string.Empty;
        Dialogues = filteredDialogues ?? Enumerable.Empty<StardewValley.DialogueLine>();
    }

    public string Format(string npcName)
    {
        var totalDialogue = string.Join(" : ", Dialogues.Where(x => x != null).Select(x => x.Text));
        
        return Util.GetString("historyOverheardFormat", new 
        { 
            name = Name, 
            totalDialogue = totalDialogue 
        });
    }
}