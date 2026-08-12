using System.Collections.Generic;
using System.Linq;
using StardewValley;

namespace ValleytalkReborn;

internal class ThirdPartyHistory : IHistory
{
    public Character Character { get; set; }
    public IEnumerable<StardewValley.DialogueLine> FilteredDialogues { get; set; }
    public string FestivalName { get; set; }

    public ThirdPartyHistory(Character character, IEnumerable<StardewValley.DialogueLine> filteredDialogues, string festivalName)
    {
        Character = character;
        FilteredDialogues = filteredDialogues ?? Enumerable.Empty<StardewValley.DialogueLine>();
        FestivalName = festivalName ?? string.Empty;
    }

    public string Format(string npcName)
    {
        var dialogues = FilteredDialogues ?? Enumerable.Empty<StardewValley.DialogueLine>();
        var totalDialogue = string.Join(" : ", dialogues.Where(x => x != null).Select(x => x.Text));
        
        var festivalNameString = string.IsNullOrWhiteSpace(FestivalName) 
            ? string.Empty 
            : Util.GetString("historyThirdPartyFestival", new { festivalName = FestivalName });
            
        var charName = Character?.Name ?? string.Empty;

        return Util.GetString("historyThirdPartyFormat", new 
        { 
            npcName = npcName ?? string.Empty, 
            Name = charName, 
            festivalNameString = festivalNameString, 
            totalDialogue = totalDialogue 
        });
    }
}