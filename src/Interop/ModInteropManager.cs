using System.Collections.Generic;
using System;
using System.Linq;
using StardewModdingAPI;

namespace ValleytalkReborn;

public class ModInteropManager
{
    private ModInteropManager() { }
    private static ModInteropManager _instance;
    public static ModInteropManager Instance => _instance ??= new ModInteropManager();
    
    private Dictionary<string, Dictionary<string, Dictionary<string, string>>> _promptOverrides = new();
    
    // 【新增】线程安全锁，防止异步生成对话时与其他模组注册产生并发修改冲突
    private readonly object _lockObj = new object();

    public void RegisterPromptOverride(string modName, string characterName, string promptElement, string overrideText)
    {
        lock (_lockObj)
        {
            // 【优化】使用 TryGetValue 替代 ContainsKey + 索引器，消除双重哈希计算
            if (!_promptOverrides.TryGetValue(characterName, out var charDict))
            {
                charDict = new Dictionary<string, Dictionary<string, string>>();
                _promptOverrides[characterName] = charDict;
            }

            if (!charDict.TryGetValue(promptElement, out var promptDict))
            {
                promptDict = new Dictionary<string, string>();
                charDict[promptElement] = promptDict;
            }

            promptDict[modName] = overrideText;
        }
    }

    public void ClearPromptOverride(string modName, string characterName, string promptElement)
    {
        lock (_lockObj)
        {
            // 【优化】连续使用 TryGetValue 进行安全解包，既快又安全
            if (_promptOverrides.TryGetValue(characterName, out var charDict) && 
                charDict.TryGetValue(promptElement, out var promptDict))
            {
                promptDict.Remove(modName);
                if (promptDict.Count == 0)
                {
                    charDict.Remove(promptElement);
                    if (charDict.Count == 0)
                    {
                        _promptOverrides.Remove(characterName);
                    }
                }
            }
        }
    }

    public void ClearPromptOverrides(string modName, string characterName = "")
    {
        lock (_lockObj)
        {
            if (string.IsNullOrWhiteSpace(characterName))
            {
                // 使用 ToList() 是为了在遍历时允许安全删除，但在 lock 保护下执行是绝对安全的
                foreach (var charKey in _promptOverrides.Keys.ToList())
                {
                    var charDict = _promptOverrides[charKey];
                    foreach (var promptKey in charDict.Keys.ToList())
                    {
                        var promptDict = charDict[promptKey];
                        promptDict.Remove(modName);
                        
                        if (promptDict.Count == 0)
                        {
                            charDict.Remove(promptKey);
                        }
                    }
                    
                    if (charDict.Count == 0)
                    {
                        _promptOverrides.Remove(charKey);
                    }
                }
            }
            else
            {
                if (_promptOverrides.TryGetValue(characterName, out var charDict))
                {
                    foreach (var promptKey in charDict.Keys.ToList())
                    {
                        var promptDict = charDict[promptKey];
                        promptDict.Remove(modName);
                        
                        if (promptDict.Count == 0)
                        {
                            charDict.Remove(promptKey);
                        }
                    }
                    
                    if (charDict.Count == 0)
                    {
                        _promptOverrides.Remove(characterName);
                    }
                }
            }
        }
    }

    internal Dictionary<string, IEnumerable<string>> GetPromptOverrides(Character character)
    {
        lock (_lockObj)
        {
            if (_promptOverrides.TryGetValue(character.Name, out var overrides))
            {
                // 【关键优化】必须在这里调用 .ToList() 将内部的 Values 实体化！
                // 否则外部在使用返回的 IEnumerable 时，如果恰好有其他模组修改了字典，会导致游戏崩溃。
                return overrides.ToDictionary(
                    kvp => kvp.Key, 
                    kvp => (IEnumerable<string>)kvp.Value.Values.ToList()
                );
            }
            return new Dictionary<string, IEnumerable<string>>();
        }
    }

    /// <summary>
    /// Clears all prompt override data. Called when the game is exiting.
    /// </summary>
    public void Cleanup()
    {
        try
        {
            lock (_lockObj)
            {
                _promptOverrides?.Clear();
                _promptOverrides = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>();
            }
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[ModInteropManager] Error during cleanup: {ex.Message}", LogLevel.Warn);
        }
    }
}