using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using StardewValley;

namespace ValleytalkReborn;

internal class DialogueHistory : IHistory
{
    private IEnumerable<StardewValley.DialogueLine> _dialogues = Enumerable.Empty<StardewValley.DialogueLine>();

    [JsonConstructor]
    public DialogueHistory()
    {
        _dialogues = Enumerable.Empty<StardewValley.DialogueLine>();
    }

    public DialogueHistory(IEnumerable<StardewValley.DialogueLine> dialogues)
    {
        _dialogues = dialogues ?? Enumerable.Empty<StardewValley.DialogueLine>();
    }

    public string Format(string npcName)
    {
        var safeDialogues = Dialogues ?? Enumerable.Empty<StardewValley.DialogueLine>();
        var totalDialogue = string.Join(" : ", safeDialogues.Where(x => x != null).Select(x => x.Text));
        
        return Util.GetString("dialogueHistoryFormat", new 
        { 
            npcName = npcName ?? string.Empty, 
            totalDialogue = totalDialogue 
        });
    }

    public IEnumerable<StardewValley.DialogueLine> Dialogues 
    { 
        get => _dialogues; 
        set => _dialogues = value ?? Enumerable.Empty<StardewValley.DialogueLine>(); 
    }
}