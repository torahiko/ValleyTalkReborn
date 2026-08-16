// InfiniteChatTracker.cs
using System.Collections.Generic;

namespace ValleytalkReborn
{
    internal static class InfiniteChatTracker
    {
        private static readonly HashSet<string> _continuing = new();

        public static void SetContinuing(string npcName) => _continuing.Add(npcName);
        public static bool IsContinuing(string npcName) => _continuing.Contains(npcName);
        public static void Clear(string npcName) => _continuing.Remove(npcName);
    }
}