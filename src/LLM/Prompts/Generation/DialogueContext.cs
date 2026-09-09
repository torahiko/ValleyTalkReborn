using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Data;
using StardewModdingAPI;
using System.Data.SqlTypes;
using System.Linq;
using StardewValley.Characters;
using ValleytalkReborn;

namespace ValleytalkReborn;

public class DialogueContext
{
    private static readonly string[] singles = new string[] { "Emily", "Haley", "Maru", "Penny", "Sam", "Sebastian", "Shane", "Abigail", "Elliott", "Harvey", "Leah", "Alex", "Krobus" };
    private static readonly int[] heartsOptions = new int[] { 0, 2, 4, 6, 8, 10 };
    private static readonly int[] friendHeartOptions = new int[] { 0, 6, 8, 10 };
    private static readonly Season?[] seasonOptions = new Season?[] { ValleytalkReborn.Season.Spring, ValleytalkReborn.Season.Summer, ValleytalkReborn.Season.Fall, ValleytalkReborn.Season.Winter, null };
    public static readonly string[] locations = new string[] { "Beach", "Desert", "Railroad", "Saloon", "SeedShop", "JojaMart" };
    public static readonly string[] resortTags = new string[] { "Resort", "Resort_Entering", "Resort_Leaving" };
    private static readonly int[] yearOptions = new int[] { 1, 2 };
    public static string[] specialContexts = new string[] { "cc_Boulder", "cc_Bridge", "cc_Bus", "cc_Greenhouse", "cc_Minecart", "cc_Complete", "movieTheater", "pamHouseUpgrade", "pamHouseUpgradeAnonymous", "jojaMartStruckByLightning", "babyBoy", "babyGirl", "wedding", "event_postweddingreception", "luauBest", "luauShorts", "luauPoisoned", "Characters_MovieInvite_Invited", "DumpsterDiveComment", "SpouseStardrop", "FlowerDance_Accept_Spouse", "FlowerDance_Accept", "FlowerDance_Decline", "GreenRain", "GreenRainFinished", "GreenRain_2", "Rainy" };

    public int? Hearts { get; init; }
    public Season? Season { get; init; }
    public int? Year { get; init; }
    public Weekday? Day { get; set; }
    public int? DayOfSeason { get; init; }
    public string Inlaw { get; init; }
    public StardewValley.Object Accept { get; set; }
    public string TimeOfDay { get; init; }
    public RandomAction? RandomAct { get; set; }
    public int? RandomValue { get; init; }
    public SpouseAction? SpouseAct { get; set; }
    public string Spouse { get; init; }
    public string ChatID { get; init; }
    public List<ConversationElement> ChatHistory { get; set; } = new List<ConversationElement>();
    public bool LastLineIsPlayerInput { get; set; } = false; // Tracks if the last line came from the player

    // ── 【新增】是否为当前活跃对话框内的连续交互 ──
    /// <summary>
    /// Indicates whether this is a continuation within the currently active dialogue box.
    /// TRUE = Turn 1+ (player is continuing the conversation within the same dialogue session).
    /// FALSE = Turn 0 (player just opened dialogue / NPC approached).
    /// </summary>
    public bool IsActiveTurn { get; set; } = false;

    // ── 【新增】对话框会话 ID（用于识别同一对话框内的多轮交互） ──
    /// <summary>
    /// Unique identifier for the current dialogue session.
    /// Changes each time the dialogue box is closed and reopened.
    /// Used to track multi-turn continuity within a single session.
    /// </summary>
    public string DialogueSessionId { get; set; }

    // 【新增】用于接收和传递 ContextRouter 动态计算出的路由标志位
    public ContextFlags RoutingFlags { get; set; } = new ContextFlags 
    { 
        IncludeSafetyRules = true, 
        IncludeShortTermContext = true, 
        IncludeMemories = true, 
        IncludeEnvironment = true, 
        IncludeFarmDetails = true, 
        IsSimpleGreeting = false 
    };

    private string[] elements = Array.Empty<string>();
    public bool Married { get; set; } = false;
    public int TargetSamples { get; set; } = 15;

    public DialogueContext(int hearts, Season? season, int? year) : this()
    {
        Hearts = hearts;
        Season = season;
        Year = year;
    }

    public DialogueContext()
    {
    }

    private void BuildElements()
    {
        var newElements = new List<string>();
        if (ChatID != null)
        {
            elements = new string[] { ChatID };
            return;
        }

        if (Location != null)
        {
            if (Hearts != null && Hearts > 0)
            {
                newElements.Add(Location + Hearts.ToString());
            }
            else
            {
                newElements.Add(Location);
            }
        }

        if (Season != null)
        {
            newElements.Add(Season.ToString().ToLower());
        }
        if (Day != null)
        {
            var dayString = Day.ToString();
            if (Hearts != null && Hearts > 0)
            {
                dayString += Hearts.ToString();
            }
            newElements.Add(dayString);
        }
        else if (DayOfSeason != null)
        {
            newElements.Add(DayOfSeason.ToString());
        }
        else if (Spouse != null && SpouseAct == null && RandomAct == null)
        {
            newElements.Add(Spouse);
        }
        if (Accept != null)
        {
            newElements.Add("AcceptGift");
            newElements.Add($"(O){Accept}");
        }
        if (RandomAct != null)
        {
            newElements.Add(RandomAct.ToString());
            if (TimeOfDay != null)
            {
                newElements.Add(TimeOfDay);
            }
            if (RandomValue != null)
            {
                newElements.Add(RandomValue.ToString());
            }
            else if (Spouse != null)
            {
                newElements.Add(Spouse);
            }
            else
            {
                newElements.Add("");
            }
        }
        if (SpouseAct != null)
        {
            newElements.Add(SpouseAct.ToString());
            newElements.Add(Spouse);
        }
        if (Year != null && Year > 1)
        {
            newElements.Add(Year.ToString());
        }
        if (Inlaw != null)
        {
            newElements.Add("inlaw");
            newElements.Add(Inlaw);
        }
        elements = newElements.ToArray();
    }

    public DialogueContext(string value)
    {
        Value = value;
        if (string.IsNullOrWhiteSpace(value)) return;

        var parts = value.Split('_');
        var partsLength = parts.Length;
        var cursor = 0;

        // Skip empty elements from the start
        while (cursor < partsLength && parts[cursor] == "")
        {
            cursor++;
        }

        if (cursor >= partsLength) return;

        // If the current element is "M" then mark married and advance
        if (parts[cursor] == "M")
        {
            Married = true;
            cursor++;
        }

        if (cursor < partsLength && parts[cursor] == "B")
        {
            Birthday = true;
            cursor++;
        }

        if (cursor >= partsLength) return;

        // Check if the current element is a season
        if (!int.TryParse(parts[cursor], out _) && Enum.TryParse<Season>(parts[cursor], true, out var season))
        {
            Season = season;
            cursor++;
        }

        if (cursor >= partsLength) return;

        // Check if the current element is a valid GUID
        if (cursor + 1 < partsLength && Guid.TryParse(parts[cursor], out _))
        {
            ChatID = $"{parts[cursor]}_{parts[cursor + 1]}";
            cursor += 2;
        }
        else if (locations.Any(x => value.StartsWith(x, StringComparison.OrdinalIgnoreCase)))
        {
            Location = locations.First(x => value.StartsWith(x, StringComparison.OrdinalIgnoreCase));
            var rest = value.Substring(Location.Length);
            if (int.TryParse(rest, out var hearts))
            {
                Hearts = hearts;
            }
            else
            {
                Hearts = 0;
            }
            cursor++;
            if (cursor >= partsLength) return;
        }
        else if (specialContexts.Any(x => value.StartsWith(x, StringComparison.OrdinalIgnoreCase)) && !value.Contains("_Day") && !value.Contains("_Night"))
        {
            ChatID = specialContexts.First(x => value.StartsWith(x, StringComparison.OrdinalIgnoreCase));
            var rest = value.Substring(ChatID.Length);
            if (int.TryParse(rest, out var hearts))
            {
                Hearts = hearts;
            }
            else
            {
                Hearts = 0;
            }
            cursor++;
            if (cursor >= partsLength) return;
        }
        // Check if the current element is a day of the week followed by a number
        else if (parts[cursor].Length >= 3 && Enum.TryParse<Weekday>(parts[cursor].Substring(0, 3), true, out var day))
        {
            Day = day;
            if (parts[cursor].Length > 3)
            {
                // 修复：原来是 int.Parse，读档后部分 key 后缀非数字会抛 FormatException
                if (!int.TryParse(parts[cursor].Substring(3), out var heartsVal))
                    heartsVal = 0;
                Hearts = heartsVal;
            }
            else
            {
                Hearts = 0;
            }
            cursor++;
        }
        else if (int.TryParse(parts[cursor], out var dayOfSeason))
        {
            DayOfSeason = dayOfSeason;
            cursor++;
        }
        else if (parts[cursor].StartsWith("Accept", StringComparison.OrdinalIgnoreCase))
        {
            if (cursor + 1 < partsLength)
            {
                var gift = parts[cursor + 1];
                while (gift.StartsWith("(O)", StringComparison.Ordinal))
                {
                    gift = gift.Substring(3);
                }
                // Construct the gift Object using item ID and a quantity of 1
                Accept = new StardewValley.Object(gift, 1);
                cursor += 2;
            }
            else
            {
                cursor++;
            }
        }
        else if (Enum.TryParse<RandomAction>(parts[cursor], true, out var randomAction))
        {
            RandomAct = randomAction;
            if ((randomAction == RandomAction.Rainy || randomAction == RandomAction.Indoor) && cursor + 1 < partsLength)
            {
                TimeOfDay = parts[cursor + 1];
                cursor += 2;
            }
            else
            {
                cursor++;
            }
            if (cursor < partsLength && int.TryParse(parts[cursor], out var randomValue))
            {
                RandomValue = randomValue;
                cursor++;
            }
        }
        else if (cursor + 1 < partsLength && Enum.TryParse<SpouseAction>(parts[cursor], true, out var spouseAction))
        {
            SpouseAct = spouseAction;
            Spouse = parts[cursor + 1];
            cursor += 2;
        }
        else
        {
            ChatID = value;
            return;
        }

        // Guard: if cursor has exhausted parts, exit
        if (cursor >= partsLength) return;

        // If the current element is a number, set the year and advance
        if (int.TryParse(parts[cursor], out var year))
        {
            Year = year;
            cursor++;
        }

        // If there are two or more remaining elements, check if the next says "inlaw"
        if (cursor + 1 < partsLength && parts[cursor] == "inlaw")
        {
            Inlaw = parts[cursor + 1];
            cursor += 2;
        }

        // Store remaining elements for any downstream consumers
        if (cursor < partsLength)
        {
            elements = parts.Skip(cursor).ToArray();
        }
    }
    /// <summary>
    /// Safely parse a context value without throwing. Returns an empty <see cref="DialogueContext"/>
    /// if the input is null/blank, and logs + returns empty on any parse exception.
    /// Use this at all external call sites instead of <c>new DialogueContext(string)</c>.
    /// </summary>
    public static DialogueContext SafeParse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return new DialogueContext();
        try
        {
            return new DialogueContext(value);
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[DialogueContext] Failed to parse '{value}': {ex.Message}", LogLevel.Warn);
            return new DialogueContext();
        }
    }

    public DialogueContext(DialogueContext context)
    {
        Hearts = context.Hearts;
        Season = context.Season;
        Year = context.Year;
        Day = context.Day;
        DayOfSeason = context.DayOfSeason;
        Inlaw = context.Inlaw;
        Accept = context.Accept;
        TimeOfDay = context.TimeOfDay;
        RandomAct = context.RandomAct;
        RandomValue = context.RandomValue;
        SpouseAct = context.SpouseAct;
        Spouse = context.Spouse;
        ChatID = context.ChatID;
        ChatHistory = context.ChatHistory;
        Married = context.Married;
        TargetSamples = context.TargetSamples;
        Location = context.Location;
        LastLineIsPlayerInput = context.LastLineIsPlayerInput;
        
        // 【新增】在克隆时同步路由标志位
        RoutingFlags = context.RoutingFlags;
    }

    private string _value;
    public string Value
    {
        get
        {
            BuildElements();
            _value = string.Join('_', elements);
            return _value;
        }
        private set
        {
            _value = value;
        }
    }
    public string[] Elements
    {
        get
        {
            if (elements.Length == 0)
            {
                BuildElements();
                _value = string.Join("_", elements);
            }
            return elements;
        }
    }

    public string Gender { get; internal set; }
    public string Location { get; internal set; }
    public bool Birthday { get; internal set; } = false;
    public bool MaleFarmer { get; internal set; }
    public List<ChildDescription> Children { get; internal set; }
    public int GiftTaste { get; internal set; }
    public List<string> Weather { get; internal set; }
    public string ScheduleLine { get; internal set; }
    public bool CanGiveGift { get; internal set; } = false;

    // Add a method to return a value representing how different two contexts are - this will be used to find the most similar context to the current context
    public int CompareTo(DialogueContext other)
    {
        var difference = 0;
        // If all elements of other are null, they are very different
        if (other == null || (other.Hearts == null && other.Season == null && other.Year == null && other.Day == null && other.DayOfSeason == null && other.Inlaw == null && other.Accept == null && other.TimeOfDay == null && other.RandomAct == null && other.SpouseAct == null && other.ChatID == null && other.Married == false && other.Location == null && other.Birthday == false))
        {
            return 10000;
        }
        // If they are both hearts based, favour hearts that are similar.  If only one is hearts based, they are very different.
        difference += Math.Abs((Hearts ?? 0) - (other.Hearts ?? 0)) * 100;

        if (Season != other.Season)
        {
            difference += 50;
        }
        if (Day != other.Day)
        {
            difference++;
        }
        if (DayOfSeason != other.DayOfSeason)
        {
            difference += 200;
        }
        // If only one is a gift acceptance, they are very different
        if ((Accept == null) ^ (other.Accept == null))
        {
            difference += 2000;
        }
        if (TimeOfDay != other.TimeOfDay)
        {
            difference += 20;
        }
        difference += CompareValues(RandomAct, other.RandomAct, 0, 200, 2000);
        difference += CompareValues(SpouseAct, other.SpouseAct, 0, 200, 2000);
        difference += CompareValuesNull(Spouse, other.Spouse, 0, 10000, 2000);
        difference += CompareValues(Year, other.Year, 0, 200, 200);
        difference += CompareValuesNull(Inlaw, other.Inlaw, 0, 500, 1000);
        // Use a deterministic string ordinal comparison as tie-breaker instead of
        // Location.GetHashCode(), which is randomized per-process on .NET Core and
        // would break the IComparable symmetry contract (a.CompareTo(b) vs b.CompareTo(a)).
        if (Location != null && other?.Location != null)
        {
            difference += Math.Abs(string.Compare(Location, other.Location, StringComparison.Ordinal) % 10);
        }

        return difference;
    }

    private int CompareValues<T>(Nullable<T> item1, Nullable<T> item2, int ifEqual, int ifDifferent, int ifOneUndefined) where T : struct
    {
        if (item1 == null ^ item2 == null)
        {
            return ifOneUndefined;
        }
        if (item1 == null && item2 == null)
        {
            return ifEqual;
        }

        var i1v = (T)item1;
        var i2v = (T)item2;

        if (EqualityComparer<T>.Default.Equals(i1v, i2v))
        {
            return ifEqual;
        }

        return ifDifferent;
    }

    private int CompareValuesNull<T>(T item1, T item2, int ifEqual, int ifDifferent, int ifOneUndefined) where T : class
    {
        if (item1 == null ^ item2 == null)
        {
            return ifOneUndefined;
        }
        if (item1 == null && item2 == null)
        {
            return ifEqual;
        }

        var i1v = (T)item1;
        var i2v = (T)item2;

        if (EqualityComparer<T>.Default.Equals(i1v, i2v))
        {
            return ifEqual;
        }

        return ifDifferent;
    }

    internal static bool IsSpecialContext(string chatID)
    {
        return specialContexts.Contains(chatID);
    }

    // Override the equals method to compare the value of the context
    public override bool Equals(object obj)
    {
        if (obj is DialogueContext other)
        {
            // Check all the properties from the clone constructor

            return Hearts == other.Hearts &&
                Season == other.Season &&
                Year == other.Year &&
                Day == other.Day &&
                DayOfSeason == other.DayOfSeason &&
                Inlaw == other.Inlaw &&
                Accept == other.Accept &&
                TimeOfDay == other.TimeOfDay &&
                RandomAct == other.RandomAct &&
                SpouseAct == other.SpouseAct &&
                ChatID == other.ChatID &&
                Married == other.Married &&
                Location == other.Location &&
                Birthday == other.Birthday;

        }
        return false;
    }
    
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Hearts);
        hash.Add(Season);
        hash.Add(Year);
        hash.Add(Day);
        hash.Add(DayOfSeason);
        hash.Add(Inlaw);
        hash.Add(Accept);
        hash.Add(TimeOfDay);
        hash.Add(RandomAct);
        hash.Add(SpouseAct);
        hash.Add(ChatID);
        hash.Add(Married);
        hash.Add(Location);
        hash.Add(Birthday);
        return hash.ToHashCode();
    }
}
