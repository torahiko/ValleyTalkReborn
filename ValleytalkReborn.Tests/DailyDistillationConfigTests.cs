using System.Collections.Generic;
using Newtonsoft.Json;
using StardewModdingAPI;
using ValleytalkReborn;
using Xunit;

namespace ValleytalkReborn.Tests
{
    /// <summary>
    /// DD401-CONFIG-MIGRATION：日记蒸馏配置迁移与规范化。
    /// 覆盖：旧 bool true/false 迁移、新旧字段两种 JSON 排列、新值优先、缺省 Intraday、
    /// 非法/空模式回退 Disabled、阈值/预算边界 clamp、序列化再读取（旧开关不再输出）与日志可见性。
    /// 纯 JSON/配置对象测试，不触碰 Game1 静态，可并行。
    /// </summary>
    public class DailyDistillationConfigTests
    {
        // ── 最小 IMonitor 假实现：记录 (级别, 消息) 用于日志可见性断言 ──
        private sealed class RecordingMonitor : IMonitor
        {
            public readonly List<(LogLevel Level, string Message)> Entries = new();

            public bool IsVerbose => false;

            public void Log(string message, LogLevel level) => Entries.Add((level, message ?? string.Empty));

            public void LogOnce(string message, LogLevel level) => Log(message, level);

            public void VerboseLog(string message) { }

            public void VerboseLog(ref StardewModdingAPI.Framework.Logging.VerboseLogStringHandler handler) { }

            public bool HasWarnContaining(string fragment) =>
                Entries.Exists(e => e.Level == LogLevel.Warn && e.Message.Contains(fragment));
        }

        private static ModConfig Deserialize(string json) =>
            JsonConvert.DeserializeObject<ModConfig>(json);

        private static string Serialize(ModConfig config) =>
            JsonConvert.SerializeObject(config, Formatting.Indented);

        // ── 缺省：两者都不存在 → Intraday ──

        [Fact]
        public void MissingBoth_DefaultsToIntraday()
        {
            var cfg = Deserialize("{}");

            Assert.Equal("Intraday", cfg.DailyDistillMode);
            Assert.Equal(3, cfg.DailyDistillThreshold);
            Assert.Equal(2, cfg.DailyMaxRequestsPerNpc);
            Assert.True(cfg.AutoSummarizeDaily);
        }

        [Fact]
        public void FreshInstance_DefaultsToIntraday()
        {
            var cfg = new ModConfig();

            Assert.Equal("Intraday", cfg.DailyDistillMode);
            Assert.True(cfg.AutoSummarizeDaily);
        }

        // ── 旧 bool 迁移 ──

        [Fact]
        public void LegacyTrue_MigratesToOvernight()
        {
            var cfg = Deserialize("{\"AutoSummarizeDaily\": true}");

            Assert.Equal("Overnight", cfg.DailyDistillMode);
            Assert.True(cfg.AutoSummarizeDaily);
        }

        [Fact]
        public void LegacyFalse_MigratesToDisabled()
        {
            var cfg = Deserialize("{\"AutoSummarizeDaily\": false}");

            Assert.Equal("Disabled", cfg.DailyDistillMode);
            Assert.False(cfg.AutoSummarizeDaily);
        }

        // ── 新值优先 + 字段排列顺序无关 ──

        [Fact]
        public void NewModeTakesPrecedence_LegacyFieldFirst()
        {
            var cfg = Deserialize("{\"AutoSummarizeDaily\": false, \"DailyDistillMode\": \"Intraday\"}");

            Assert.Equal("Intraday", cfg.DailyDistillMode);
            Assert.True(cfg.AutoSummarizeDaily);
        }

        [Fact]
        public void NewModeTakesPrecedence_ModeFieldFirst()
        {
            var cfg = Deserialize("{\"DailyDistillMode\": \"Disabled\", \"AutoSummarizeDaily\": true}");

            Assert.Equal("Disabled", cfg.DailyDistillMode);
            Assert.False(cfg.AutoSummarizeDaily);
        }

        // ── 序列化再读取：只写新模式/阈值/预算，旧开关仅兼容读取 ──

        [Fact]
        public void Serialize_WritesOnlyNewFields_OmitsLegacySwitch()
        {
            var cfg = Deserialize("{\"AutoSummarizeDaily\": true}");
            string json = Serialize(cfg);

            Assert.Contains("\"DailyDistillMode\": \"Overnight\"", json);
            Assert.Contains("\"DailyDistillThreshold\": 3", json);
            Assert.Contains("\"DailyMaxRequestsPerNpc\": 2", json);
            Assert.DoesNotContain("AutoSummarizeDaily", json);
        }

        [Fact]
        public void RoundTrip_LegacyJson_IsIdempotent()
        {
            var first = Deserialize("{\"AutoSummarizeDaily\": true}");
            string firstJson = Serialize(first);

            var second = Deserialize(firstJson);
            string secondJson = Serialize(second);

            Assert.Equal("Overnight", second.DailyDistillMode);
            // 整份 JSON 字节比较会受既有 GetActiveProfile 序列化副作用影响（与日记迁移无关），
            // 幂等性聚焦日记字段：两次序列化的日记字段值一致。
            var firstObj = Newtonsoft.Json.Linq.JObject.Parse(firstJson);
            var secondObj = Newtonsoft.Json.Linq.JObject.Parse(secondJson);
            Assert.Equal((string)firstObj["DailyDistillMode"], (string)secondObj["DailyDistillMode"]);
            Assert.Equal((int)firstObj["DailyDistillThreshold"], (int)secondObj["DailyDistillThreshold"]);
            Assert.Equal((int)firstObj["DailyMaxRequestsPerNpc"], (int)secondObj["DailyMaxRequestsPerNpc"]);
        }

        [Fact]
        public void RoundTrip_BothFieldOrders_ResolveIdentically()
        {
            var legacyFirst = Deserialize("{\"AutoSummarizeDaily\": true, \"DailyDistillMode\": \"Overnight\"}");
            var modeFirst = Deserialize("{\"DailyDistillMode\": \"Overnight\", \"AutoSummarizeDaily\": true}");

            Assert.Equal(legacyFirst.DailyDistillMode, modeFirst.DailyDistillMode);
            Assert.Equal(legacyFirst.AutoSummarizeDaily, modeFirst.AutoSummarizeDaily);
        }

        // ── 规范化：非法/空模式 → Disabled + 配置错误日志 ──

        [Fact]
        public void UnknownMode_FallsBackToDisabled_AndWarns()
        {
            var cfg = Deserialize("{\"DailyDistillMode\": \"Turbo\"}");
            var monitor = new RecordingMonitor();

            cfg.NormalizeDailyDistillationConfig(monitor);

            Assert.Equal("Disabled", cfg.DailyDistillMode);
            Assert.True(monitor.HasWarnContaining("Turbo"));
        }

        [Fact]
        public void EmptyMode_FallsBackToDisabled()
        {
            var cfg = Deserialize("{\"DailyDistillMode\": \"\"}");
            var monitor = new RecordingMonitor();

            cfg.NormalizeDailyDistillationConfig(monitor);

            Assert.Equal("Disabled", cfg.DailyDistillMode);
            Assert.False(cfg.AutoSummarizeDaily);
        }

        [Theory]
        [InlineData("overnight", "Overnight")]
        [InlineData("INTRADAY", "Intraday")]
        [InlineData(" disabled ", "Disabled")]
        public void CaseInsensitive_MapsToCanonicalValue(string raw, string expected)
        {
            var cfg = Deserialize("{\"DailyDistillMode\": \"" + raw + "\"}");
            var monitor = new RecordingMonitor();

            cfg.NormalizeDailyDistillationConfig(monitor);

            Assert.Equal(expected, cfg.DailyDistillMode);
        }

        // ── 边界数值 clamp ──

        [Theory]
        [InlineData(0, 2)]
        [InlineData(1, 2)]
        [InlineData(2, 2)]
        [InlineData(3, 3)]
        [InlineData(10, 10)]
        [InlineData(11, 10)]
        [InlineData(100, 10)]
        public void Threshold_ClampedToBounds(int raw, int expected)
        {
            var cfg = Deserialize("{\"DailyDistillThreshold\": " + raw + "}");
            var monitor = new RecordingMonitor();

            cfg.NormalizeDailyDistillationConfig(monitor);

            Assert.Equal(expected, cfg.DailyDistillThreshold);
        }

        [Theory]
        [InlineData(0, 1)]
        [InlineData(1, 1)]
        [InlineData(2, 2)]
        [InlineData(5, 5)]
        [InlineData(6, 5)]
        [InlineData(99, 5)]
        public void Budget_ClampedToBounds(int raw, int expected)
        {
            var cfg = Deserialize("{\"DailyMaxRequestsPerNpc\": " + raw + "}");
            var monitor = new RecordingMonitor();

            cfg.NormalizeDailyDistillationConfig(monitor);

            Assert.Equal(expected, cfg.DailyMaxRequestsPerNpc);
        }

        [Fact]
        public void Clamp_LogsOriginalAndNewValues()
        {
            var cfg = Deserialize("{\"DailyDistillThreshold\": 20, \"DailyMaxRequestsPerNpc\": 0}");
            var monitor = new RecordingMonitor();

            cfg.NormalizeDailyDistillationConfig(monitor);

            Assert.True(monitor.HasWarnContaining("DailyDistillThreshold 20"));
            Assert.True(monitor.HasWarnContaining("clamped to 10"));
            Assert.True(monitor.HasWarnContaining("DailyMaxRequestsPerNpc 0"));
            Assert.True(monitor.HasWarnContaining("clamped to 1"));
        }

        // ── 其他配置保留 + 幂等 ──

        [Fact]
        public void Normalize_PreservesOtherConfig_AndIsIdempotent()
        {
            var cfg = Deserialize("{\"EnableMod\": false, \"AutoSummarizeWeekly\": false, \"DailyDistillMode\": \"Bogus\"}");
            var monitor = new RecordingMonitor();

            cfg.NormalizeDailyDistillationConfig(monitor);
            Assert.Equal("Disabled", cfg.DailyDistillMode);
            Assert.False(cfg.EnableMod);
            Assert.False(cfg.AutoSummarizeWeekly);

            monitor.Entries.Clear();
            cfg.NormalizeDailyDistillationConfig(monitor);

            Assert.Equal("Disabled", cfg.DailyDistillMode);
            Assert.Empty(monitor.Entries);
        }

        // ── 兼容代理 setter 语义 ──

        [Fact]
        public void LegacyProxySetter_MapsToMode()
        {
            var off = Deserialize("{\"DailyDistillMode\": \"Disabled\"}");
            off.AutoSummarizeDaily = true;
            Assert.Equal("Overnight", off.DailyDistillMode);

            var intraday = Deserialize("{\"DailyDistillMode\": \"Intraday\"}");
            intraday.AutoSummarizeDaily = true;
            Assert.Equal("Intraday", intraday.DailyDistillMode);

            var on = Deserialize("{\"DailyDistillMode\": \"Overnight\"}");
            on.AutoSummarizeDaily = false;
            Assert.Equal("Disabled", on.DailyDistillMode);
        }

        // ── GMCM 保存流模拟：setValue 链 → Normalize → 写盘 → 再读取 ──

        [Fact]
        public void GmcmSaveFlow_NormalizeBeforeWrite_PersistsNewFields()
        {
            // 读入旧配置（旧开关开启 → Overnight）
            var cfg = Deserialize("{\"AutoSummarizeDaily\": true}");

            // 模拟 GMCM 三选一/数值控件写入
            cfg.DailyDistillMode = "Intraday";
            cfg.DailyDistillThreshold = 5;
            cfg.DailyMaxRequestsPerNpc = 4;

            // GMCM save 钩子：WriteConfig 前规范化
            cfg.NormalizeDailyDistillationConfig(new RecordingMonitor());
            string json = Serialize(cfg);

            Assert.Contains("\"DailyDistillMode\": \"Intraday\"", json);
            Assert.DoesNotContain("AutoSummarizeDaily", json);

            // 写盘后重新读取：得到相同结果
            var reloaded = Deserialize(json);
            Assert.Equal("Intraday", reloaded.DailyDistillMode);
            Assert.Equal(5, reloaded.DailyDistillThreshold);
            Assert.Equal(4, reloaded.DailyMaxRequestsPerNpc);
            Assert.True(reloaded.AutoSummarizeDaily);
        }
    }
}
