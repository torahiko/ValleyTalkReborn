using System;
using System.IO;
using Newtonsoft.Json;
using ValleytalkReborn.Cutscene.Storage;
using ValleytalkReborn.Services;
using Xunit;

namespace ValleytalkReborn.Tests
{
    /// <summary>
    /// 归档剧本录像式回放契约测试：站位记录的持久化往返与旧档兼容语义
    /// </summary>
    public class CutsceneReplayStorageTests
    {
        [Fact]
        public void ArchivedCutscene_ActorStances_RoundTripsThroughStorageJson()
        {
            var cutscene = new ArchivedCutscene
            {
                Title = "雪夜暖屋狂欢曲",
                LocationName = "FarmHouse",
                ActorNames = new System.Collections.Generic.List<string> { "Alex", "Sam" },
                ActorStances = new System.Collections.Generic.List<ArchivedActorStance>
                {
                    new ArchivedActorStance { Name = "Alex", TileX = 12, TileY = 9, Facing = 2 },
                    new ArchivedActorStance { Name = "Sam", TileX = 10, TileY = 9, Facing = 1 }
                },
                UserIntent = "轻松幽默的小镇日常",
                RawJson = @"{""title"":""雪夜暖屋狂欢曲"",""actions"":[]}",
                ActionCount = 15,
                CreatedAt = new DateTime(2026, 10, 3, 3, 14, 3)
            };

            string path = Path.Combine(Path.GetTempPath(), $"vt_archived_{Guid.NewGuid():N}.json");
            try
            {
                StorageJson.Write(path, cutscene);
                var loaded = StorageJson.Read<ArchivedCutscene>(path);

                Assert.NotNull(loaded);
                Assert.Equal("雪夜暖屋狂欢曲", loaded.Title);
                Assert.Equal("FarmHouse", loaded.LocationName);

                Assert.NotNull(loaded.ActorStances);
                Assert.Equal(2, loaded.ActorStances.Count);

                var first = loaded.ActorStances[0];
                Assert.Equal("Alex", first.Name);
                Assert.Equal(12, first.TileX);
                Assert.Equal(9, first.TileY);
                Assert.Equal(2, first.Facing);

                var second = loaded.ActorStances[1];
                Assert.Equal("Sam", second.Name);
                Assert.Equal(10, second.TileX);
                Assert.Equal(9, second.TileY);
                Assert.Equal(1, second.Facing);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public void ArchivedCutscene_LegacyJsonWithoutStances_DeserializesWithEmptyStances()
        {
            string legacyJson = @"
            {
                ""Id"": ""60fd6635981a4241a845d074b17c49c3"",
                ""Title"": ""旧档剧本"",
                ""ActorNames"": [""Alex""],
                ""RawJson"": ""{}"",
                ""ActionCount"": 8
            }";

            var loaded = JsonConvert.DeserializeObject<ArchivedCutscene>(legacyJson);

            Assert.NotNull(loaded);
            Assert.NotNull(loaded.ActorStances);
            Assert.Empty(loaded.ActorStances);
            Assert.Single(loaded.ActorNames);
            Assert.Equal("Alex", loaded.ActorNames[0]);
        }
    }
}
