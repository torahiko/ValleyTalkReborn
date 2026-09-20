using System;
using System.Collections.Generic;
using System.IO;
using StardewModdingAPI;
using StardewModdingAPI.Framework.Logging;
using ValleytalkReborn;
using ValleytalkReborn.Services;
using ValleytalkReborn.Services.Overlays;
using Xunit;

// 覆盖层存储基类的纯逻辑测试：用子类覆写 RootDirectory 指向临时目录，
// 不触碰 Game1 / ModEntry.SHelper 真实路径。
public class OverlayStorageServiceBaseTests
{
    // ── 最小 IMonitor 假实现 ─────────────────────────────────────────
    private sealed class FakeMonitor : IMonitor
    {
        public List<string> Lines = new();
        public bool IsVerbose => false;
        public void Log(string message, LogLevel level) => Lines.Add($"[{level}] {message}");
        public void LogOnce(string message, LogLevel level) => Lines.Add($"[{level}] {message}");
        public void VerboseLog(string message) => Lines.Add($"[Verbose] {message}");
        public void VerboseLog(ref VerboseLogStringHandler message) { }
    }

    // ── 测试子类：覆写 RootDirectory 指向临时目录 ─────────────────────
    private sealed class TestOverlayService : OverlayStorageServiceBase<DateLocationOverlayFile>
    {
        private readonly string _root;
        public TestOverlayService(string root, IMonitor monitor) : base(null!, monitor, "custom_overlays")
        {
            _root = root;
        }
        protected override string FileName => "date_locations.json";
        protected override string RootDirectory => _root;
    }

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "OverlayStorageTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ── AC3：LoadOrNull 对不存在的文件返回 null 且不创建目录 ──────────
    [Fact]
    public void AC3_LoadOrNull_MissingFile_ReturnsNull_AndDoesNotCreateDir()
    {
        string root = NewTempDir();
        var mon = new FakeMonitor();
        var svc = new TestOverlayService(root, mon);

        Assert.False(svc.HasOverlay);
        DateLocationOverlayFile? loaded = svc.LoadOrNull();
        Assert.Null(loaded);

        // 覆盖层子目录不应被 LoadOrNull 创建。
        string overlayDir = Path.Combine(root, "saves", "custom_overlays", "custom_overlays");
        Assert.False(Directory.Exists(overlayDir), "LoadOrNull must not create directory");
    }

    // ── AC4：Save→LoadOrNull 往返数据相等 ────────────────────────────
    [Fact]
    public void AC4_Save_Then_LoadOrNull_RoundTrips()
    {
        string root = NewTempDir();
        var mon = new FakeMonitor();
        var svc = new TestOverlayService(root, mon);

        DateLocationOverlayFile original = new()
        {
            Entries =
            {
                ["Saloon"] = new DateLocationInfo
                {
                    LocationId = "Saloon",
                    TargetMap = "Saloon",
                    DisplayNameZh = "星之果实酒吧",
                    DisplayNameEn = "The Stardrop Saloon",
                    RequiredHearts = 4,
                    AllowRainyDays = true,
                    TimeWindow = "1800-2130"
                }
            },
            RemovedLocationIds = { "GhostLocation" }
        };

        Assert.True(svc.Save(original, out string err), $"Save failed: {err}");
        Assert.True(svc.HasOverlay);

        DateLocationOverlayFile? loaded = svc.LoadOrNull();
        Assert.NotNull(loaded);
        Assert.True(loaded.Entries.TryGetValue("Saloon", out DateLocationInfo? saloon));
        Assert.NotNull(saloon);
        Assert.Equal("Saloon", saloon.LocationId);
        Assert.Equal("Saloon", saloon.TargetMap);
        Assert.Equal("星之果实酒吧", saloon.DisplayNameZh);
        Assert.Equal(4, saloon.RequiredHearts);
        Assert.Equal("1800-2130", saloon.TimeWindow);
        Assert.Contains("GhostLocation", loaded.RemovedLocationIds);

        // 序列化往返后 JSON 文本相等（确定性校验）。
        string json1 = Newtonsoft.Json.JsonConvert.SerializeObject(original, Newtonsoft.Json.Formatting.Indented);
        string json2 = Newtonsoft.Json.JsonConvert.SerializeObject(loaded, Newtonsoft.Json.Formatting.Indented);
        Assert.Equal(json1, json2);
    }

    // ── AC5：写入损坏 JSON 后 LoadOrNull 返回 null 且原文件保留 ────────
    [Fact]
    public void AC5_CorruptedJson_LoadOrNull_ReturnsNull_FilePreserved()
    {
        string root = NewTempDir();
        var mon = new FakeMonitor();
        var svc = new TestOverlayService(root, mon);

        // 先合法保存，再人为写坏。
        Assert.True(svc.Save(new DateLocationOverlayFile(), out _));
        string path = Path.Combine(root, "saves", "custom_overlays", "custom_overlays", "date_locations.json");
        Assert.True(File.Exists(path));
        File.WriteAllText(path, "{ this is : [ not valid json");

        DateLocationOverlayFile? loaded = svc.LoadOrNull();
        Assert.Null(loaded);
        // 损坏文件必须保留（不得静默删除）。
        Assert.True(File.Exists(path), "corrupted file must be preserved");
        Assert.Contains(mon.Lines, l => l.Contains("[Warn]") && l.Contains("覆盖层损坏"));
    }

    // ── AC6：PoiOverlayFile 中含 MapName 空白的 CustomPois 条目时剔除该条并保留其余 ──
    [Fact]
    public void AC6_PoiOverlayFile_BlankMapName_Stripped_OthersKept()
    {
        string root = NewTempDir();
        var mon = new FakeMonitor();
        var poiSvc = new TestPoiService(root, mon);

        PoiOverlayFile original = new()
        {
            CustomPois =
            {
                ["bad_blank"] = new PoiAsset { MapName = "", TargetTile = new PoiTile { X = 5, Y = 5 } },
                ["bad_nullname"] = new PoiAsset { MapName = null!, TargetTile = new PoiTile { X = 1, Y = 1 } },
                ["bad_negative"] = new PoiAsset { MapName = "Farm", TargetTile = new PoiTile { X = -1, Y = 3 } },
                ["good"] = new PoiAsset { MapName = "Town", TargetTile = new PoiTile { X = 10, Y = 20 }, StayMinutes = 90 }
            }
        };

        Assert.True(poiSvc.Save(original, out string err), $"Save failed: {err}");
        PoiOverlayFile? loaded = poiSvc.LoadOrNull();
        Assert.NotNull(loaded);
        Assert.Single(loaded.CustomPois);
        Assert.True(loaded.CustomPois.ContainsKey("good"));
        Assert.Equal("Town", loaded.CustomPois["good"].MapName);
        Assert.Equal(10, loaded.CustomPois["good"].TargetTile.X);
        Assert.Contains(mon.Lines, l => l.Contains("[Warn]") && l.Contains("剔除 3 条非法自定义兴趣点"));
    }

    // ── 幂等删除：不存在也返回 true ──────────────────────────────────
    [Fact]
    public void DeleteOverlay_Missing_IsIdempotent()
    {
        string root = NewTempDir();
        var mon = new FakeMonitor();
        var svc = new TestOverlayService(root, mon);

        Assert.True(svc.DeleteOverlay(out string err), $"unexpected: {err}");
        Assert.Empty(err);
    }

    // ── Poi 测试子类 ──────────────────────────────────────────────────
    private sealed class TestPoiService : OverlayStorageServiceBase<PoiOverlayFile>
    {
        private readonly string _root;
        public TestPoiService(string root, IMonitor monitor) : base(null!, monitor, "custom_overlays")
        {
            _root = root;
        }
        protected override string FileName => "poi_preferences.json";
        protected override string RootDirectory => _root;
    }
}
