using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// Static helper class for cleaning and normalizing dialogue text.
/// Extracted from Character to avoid repeated allocations and improve testability.
/// </summary>
public static class DialogueCleaner
{
    private static readonly char[] SuffixCharacters = { '·', '•', '-' };

    // Static trim chars for CommonCleanup — excludes '%' to protect structural tokens
    private static readonly char[] TrimChars = { '-', ' ', '"' };

    // Pre-compiled regex to avoid per-call allocation overhead
    private static readonly Regex InvalidTagRegex = new(@"\$([a-zA-Z]+)", RegexOptions.Compiled);

    // Sentence-preferred chunk splitting (DialogueLineCleanup step 4): sentence enders first, commas as fallback.
    private static readonly char[] SentenceSplitChars = { '.', '!', '?', '。', '！', '？', '—', '…' };
    private static readonly char[] CommaSplitChars = { '，', ',' };

    /// <summary>
    /// Returns the preferred split index within window (already validated as a safe cut point), or -1 if none.
    /// Preference: last valid sentence ender; fallback to last valid comma; else -1 (caller hard-cuts).
    /// "Valid" means idx > 0 and idx < window.Length - 1 (no empty head, no trailing-punctuation degenerate cut).
    /// </summary>
    private static int FindPreferredSplitIndex(string window)
    {
        int idx = window.LastIndexOfAny(SentenceSplitChars);
        if (idx > 0 && idx < window.Length - 1)
            return idx;

        idx = window.LastIndexOfAny(CommaSplitChars);
        if (idx > 0 && idx < window.Length - 1)
            return idx;

        return -1;
    }

    /// <summary>
    /// Removes trailing dot-style suffix characters from a name.
    /// </summary>
    public static string RemoveDotSuffixes(string name)
    {
        return name.TrimEnd(SuffixCharacters);
    }

    /// <summary>
    /// Performs common cleanup on a dialogue or response line:
    /// trims symbols, removes #$b# / #$e# wrappers, and strips quotes.
    /// </summary>
    public static string CommonCleanup(string line)
    {
        if (string.IsNullOrEmpty(line))
            return string.Empty;
        line = line.Trim();

        // Only strip a single structural prefix (- or %); never use TrimStart to avoid eating all leading chars
        if (line.StartsWith("- ")) line = line.Substring(2);
        else if (line.StartsWith("-")) line = line.Substring(1);
        else if (line.StartsWith("% ")) line = line.Substring(2);
        else if (line.StartsWith("%")) line = line.Substring(1);

        // Strip surrounding quotes
        if (line.StartsWith("\"")) line = line.Substring(1);
        if (line.EndsWith("\"")) line = line.Substring(0, line.Length - 1);

        // Safely remove #$b# or #$e# prefix/suffix
        if (line.StartsWith("#$b#") && line.Length >= 4)
            line = line[4..];
        if (line.EndsWith("#$b#") && line.Length >= 4)
            line = line[..^4];
        if (line.StartsWith("#$e#") && line.Length >= 4)
            line = line[4..];
        if (line.EndsWith("#$e#") && line.Length >= 4)
            line = line[..^4];

        // Remove all quotes
        line = line.Replace("\"", "");
        return line;
    }

    /// <summary>
    /// Cleans up a dialogue line: normalizes portrait tags, removes illegal $ markers,
    /// handles long text splitting, and fixes punctuation.
    /// </summary>
    /// <param name="line">The raw dialogue line.</param>
    /// <param name="validPortraits">List of valid portrait identifiers (e.g. "h", "s", "l", "a").</param>
    /// <param name="fixPunctuation">Whether to append missing sentence-ending punctuation.</param>
    /// <param name="relaxedValidation">If true, skips the 200-char length limit check.</param>
    public static string DialogueLineCleanup(string line, List<string> validPortraits, bool fixPunctuation, bool relaxedValidation = false)
    {
        if (string.IsNullOrWhiteSpace(line)) return string.Empty;

        // 1. Normalize base format: safely convert all $e / #$e / ##$e variants to page-break #$b#
        // to prevent the vanilla dialogue box from forcefully truncating dialogue at #$e.
        line = line.Replace("##$e", "#$b#")
                   .Replace("#$e#", "#$b#")
                   .Replace("#$e", "#$b#")
                   .Replace("$e", "#$b#");

        line = line.Replace("##$b", "#$b#")
                   .Replace("$b", "#$b#");

        line = line.Replace("#$c .5#", "");
        line = line.Replace("@@", "@");

        // 2. Clean up legal portrait tags (e.g. #$h -> $h)
        if (validPortraits != null)
        {
            foreach (var indicator in validPortraits)
            {
                line = line.Replace($"#{indicator}", $"{indicator}");
            }
        }

        // 3. [Optimized] Use pre-compiled regex to clean illegal '$' markers (only when followed by letters)
        try
        {
            // Only match $ followed by letters to avoid accidentally removing $c, $e, $b etc.
            line = InvalidTagRegex.Replace(line, match =>
            {
                string val = match.Groups[1].Value;
                // Preserve legal ones: e, c, b and values in validPortraits
                if (val == "e" || val == "c" || val == "b" || (validPortraits != null && validPortraits.Contains(val)))
                {
                    return match.Value; // Keep legal
                }
                return ""; // Remove illegal
            });
        }
        catch (Exception ex)
        {
            Log.Warning($"Regex cleanup error in DialogueLineCleanup: {ex.Message}");
        }

        line = line.Trim();
        var elements = line.Split('#');

        // 4. Handle long text
        if (elements.Any(x => x.Length > 200 && !relaxedValidation))
        {
            List<string> newElements = new();
            foreach (var element in elements)
            {
                if (element.Length <= 200)
                {
                    newElements.Add(element);
                }
                else
                {
                    string remainder = element;
                    string indicator = "";

                    // Extract ALL valid portrait tags (not just the last one)
                    if (validPortraits != null && remainder.Length > 2)
                    {
                        var sb = new System.Text.StringBuilder();
                        int searchFrom = 0;
                        while (searchFrom < remainder.Length)
                        {
                            int dollarIdx = remainder.IndexOf('$', searchFrom);
                            if (dollarIdx < 0 || dollarIdx >= remainder.Length - 1) break;

                            bool matched = false;
                            foreach (var portrait in validPortraits)
                            {
                                if (dollarIdx + 1 + portrait.Length <= remainder.Length &&
                                    remainder.Substring(dollarIdx + 1, portrait.Length) == portrait)
                                {
                                    sb.Append($"${portrait}");
                                    searchFrom = dollarIdx + 1 + portrait.Length;
                                    matched = true;
                                    break;
                                }
                            }
                            if (!matched) searchFrom = dollarIdx + 1;
                        }
                        indicator = sb.ToString();

                        // Remove collected portrait tags from remainder to avoid duplication
                        foreach (var portrait in validPortraits)
                        {
                            remainder = remainder.Replace($"${portrait}", "");
                        }
                        remainder = remainder.Trim();
                    }

                    // Guard against negative maxChunkLen when indicator is very long
                    int maxChunkLenBase = Math.Max(200 - indicator.Length, 10);

                    while (remainder.Length > maxChunkLenBase)
                    {
                        int maxChunkLen = Math.Min(maxChunkLenBase, remainder.Length);
                        var elementStart = remainder.Substring(0, maxChunkLen);

                        // Support Chinese punctuation — sentence enders preferred, commas as fallback
                        int lastPeriod = FindPreferredSplitIndex(elementStart);

                        if (lastPeriod > 0 && lastPeriod < elementStart.Length - 1)
                        {
                            newElements.Add(remainder.Substring(0, lastPeriod + 1) + indicator);
                            remainder = remainder.Substring(lastPeriod + 1).Trim();
                        }
                        else
                        {
                            // Force truncation at max safe length
                            newElements.Add(elementStart + indicator);
                            remainder = remainder.Length > maxChunkLen ? remainder.Substring(maxChunkLen).Trim() : string.Empty;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(remainder))
                    {
                        newElements.Add(remainder + indicator);
                    }
                }
            }

            if (newElements.Any(x => x.Length > 200 && !relaxedValidation))
            {
                // Degradation: force-truncate oversized chunks instead of discarding the entire line
                newElements = newElements.Select(e =>
                    e.Length > 200 ? e.Substring(0, 200) : e
                ).ToList();
            }
            elements = newElements.ToArray();
        }

        // 5. Fix trailing punctuation (support Chinese)
        if (fixPunctuation)
        {
            for (int i = 0; i < elements.Length; i++)
            {
                var element = elements[i];
                var dollarIndex = element.IndexOf('$');
                // [Optimized] Use safer split approach
                string upToDollar;
                string rest = "";
                if (dollarIndex >= 0 && dollarIndex < element.Length)
                {
                    upToDollar = element.Substring(0, dollarIndex);
                    rest = element.Substring(dollarIndex);
                }
                else
                {
                    upToDollar = element;
                }

                upToDollar = upToDollar.Trim();
                if (upToDollar.Length > 0 &&
                    !upToDollar.EndsWith(".") && !upToDollar.EndsWith("!") && !upToDollar.EndsWith("?") &&
                    !upToDollar.EndsWith("。") && !upToDollar.EndsWith("！") && !upToDollar.EndsWith("？") &&
                    !upToDollar.EndsWith("——") && !upToDollar.EndsWith("—") &&
                    !upToDollar.EndsWith("…") && !upToDollar.EndsWith("..."))
                {
                    elements[i] = upToDollar + "." + rest;
                }
            }
            line = string.Join("#", elements);
        }

        return line;
    }

    /// <summary>
    /// Cleans up a response/option line: removes hashes, strips $ commands,
    /// replaces @ with farmer name, trims, and fixes punctuation.
    /// </summary>
    /// <param name="line">The raw response line.</param>
    /// <param name="fixPunctuation">Whether to append missing sentence-ending punctuation.</param>
    public static string ResponseLineCleanup(string line, bool fixPunctuation)
    {
        // Remove any hashes
        line = line.Replace("#", "");

        // Remove all $ commands using regex (safe one-pass, no index skip bug)
        line = Regex.Replace(line, @"\$.", "");

        if (line.Contains('@'))
        {
            var farmerName = Game1.player.Name;
            line = line.Replace("@", farmerName);
        }
        line = line.Trim();
        // If the line doesn't end with a sentence end punctuation, add a period
        if (fixPunctuation && !line.EndsWith(".") && !line.EndsWith("!") && !line.EndsWith("?"))
        {
            line += ".";
        }
        if (line.Length > 90)
        {
            //Log.Debug("Long line detected in AI response.  Returning nothing.");
            return string.Empty;
        }
        return line;
    }
}
