using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using StardewValley;

namespace ValleytalkReborn;

internal class StardewEventHistory
{
    private List<Tuple<StardewTime, IHistory>> _eventHistory = new();
    private List<Tuple<StardewTime, IHistory>> _overheardHistory = new();
    private List<Tuple<StardewTime, IHistory>> _dialogueHistory = new();
    private List<Tuple<StardewTime, IHistory>> _conversationHistory = new();

    public List<Tuple<StardewTime, DialogueEventHistory>> EventHistory
    {
        get => _eventHistory.Select(x => new Tuple<StardewTime, DialogueEventHistory>(x.Item1, (DialogueEventHistory)x.Item2)).ToList();
        set => _eventHistory = value.Select(x => new Tuple<StardewTime, IHistory>(x.Item1, x.Item2)).ToList();
    }

    public List<Tuple<StardewTime, DialogueEventOverheard>> OverheardHistory
    {
        get => _overheardHistory.Select(x => new Tuple<StardewTime, DialogueEventOverheard>(x.Item1, (DialogueEventOverheard)x.Item2)).ToList();
        set => _overheardHistory = value.Select(x => new Tuple<StardewTime, IHistory>(x.Item1, x.Item2)).ToList();
    }

    public List<Tuple<StardewTime, DialogueHistory>> DialogueHistory
    {
        get => _dialogueHistory.Select(x => new Tuple<StardewTime, DialogueHistory>(x.Item1, (DialogueHistory)x.Item2)).ToList();
        set => _dialogueHistory = value.Select(x => new Tuple<StardewTime, IHistory>(x.Item1, x.Item2)).ToList();
    }

    public List<Tuple<StardewTime, ConversationHistory>> ConversationHistory
    {
        get => _conversationHistory.Select(x => new Tuple<StardewTime, ConversationHistory>(x.Item1, (ConversationHistory)x.Item2)).ToList();
        set => _conversationHistory = value.Select(x => new Tuple<StardewTime, IHistory>(x.Item1, x.Item2)).ToList();
    }

    public void ClearConversationHistory()
    {
        _conversationHistory.Clear();
        _dialogueHistory.Clear();
        _overheardHistory.Clear();
    }

    [JsonIgnore]
    public IEnumerable<Tuple<StardewTime, IHistory>> AllTypes => 
        _eventHistory.Concat(_overheardHistory)
                    .Concat(_dialogueHistory)
                    .Concat(_conversationHistory);

    internal void Add(StardewTime time, IHistory theEvent)
    {
        if (theEvent == null) return;

        switch (theEvent)
        {
            case DialogueEventHistory eventHist:
                _eventHistory.Add(new(time, eventHist));
                break;
                
            case DialogueEventOverheard overheardHist:
                _overheardHistory.Add(new(time,overheardHist));
                break;

            case DialogueHistory dialogueHist:
                _dialogueHistory.Add(new(time, dialogueHist));
                break;

            case ConversationHistory chEvent:
                _conversationHistory.RemoveAll(x => x.Item2 is ConversationHistory existing && existing.Id == chEvent.Id);
                _conversationHistory.Add(new(time, chEvent));
                break;

            default:
                throw new NotImplementedException($"Unhandled IHistory type: {theEvent.GetType().Name}");
        }
    }

    internal bool Any()
    {
        return _eventHistory.Count > 0 || _overheardHistory.Count > 0 || _dialogueHistory.Count > 0 || _conversationHistory.Count > 0;
    }

    internal Tuple<StardewTime, IHistory> Last()
    {
        Tuple<StardewTime, IHistory> latestItem = null;

        void CheckAndApplyLast(List<Tuple<StardewTime, IHistory>> list)
        {
            if (list.Count == 0) return;
            var candidate = list[list.Count - 1];
            
            if (candidate?.Item1 == null) return;

            if (latestItem == null || candidate.Item1.CompareTo(latestItem.Item1) > 0)
            {
                latestItem = candidate;
            }
        }

        CheckAndApplyLast(_eventHistory);
        CheckAndApplyLast(_overheardHistory);
        CheckAndApplyLast(_dialogueHistory);
        CheckAndApplyLast(_conversationHistory);

        return latestItem;
    }

    internal void RemoveAfter(StardewTime? timeNow)
    {
        if (timeNow == null) return;
        var time = timeNow.Value;

        _eventHistory.RemoveAll(x => x.Item1.After(time));
        _overheardHistory.RemoveAll(x => x.Item1.After(time));
        _dialogueHistory.RemoveAll(x => x.Item1.After(time));
        _conversationHistory.RemoveAll(x => x.Item1.After(time));
    }

    internal void RemoveDialogueOverlapping(List<ConversationElement> chatHistory)
    {
        if (chatHistory == null || chatHistory.Count == 0) return;

        var textsToRemove = chatHistory
            .Where(c => !string.IsNullOrEmpty(c?.Text))
            .Select(c => c.Text)
            .ToHashSet();

        if (textsToRemove.Count == 0) return;

        _dialogueHistory.RemoveAll(x => 
            x.Item2 is DialogueHistory dh && 
            dh.Dialogues != null && 
            dh.Dialogues.Any(z => z.Text != null && textsToRemove.Contains(z.Text)));
    }

    internal void RemoveOverheardOverlapping(string name, List<StardewValley.DialogueLine> overheardDialogue)
    {
        if (overheardDialogue == null || overheardDialogue.Count == 0) return;

        var textsToRemove = overheardDialogue
            .Where(d => !string.IsNullOrEmpty(d.Text))
            .Select(d => d.Text)
            .ToHashSet();

        if (textsToRemove.Count == 0) return;

        _overheardHistory.RemoveAll(x => 
            x.Item2 is DialogueEventOverheard deh && 
            string.Equals(deh.Name, name, StringComparison.Ordinal) && 
            deh.Dialogues != null && 
            deh.Dialogues.Any(z => z.Text != null && textsToRemove.Contains(z.Text)));
    }
}