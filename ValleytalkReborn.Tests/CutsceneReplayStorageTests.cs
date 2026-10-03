using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using Microsoft.Xna.Framework;
using Newtonsoft.Json;
using Netcode;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Mods;
using StardewValley.Network;
using ValleytalkReborn.Cutscene;
using ValleytalkReborn.Cutscene.Storage;
using ValleytalkReborn.Services;
using Xunit;

namespace ValleytalkReborn.Tests
{
    /// <summary>
    /// 归档剧本录像式回放契约测试：站位记录的持久化往返与旧档兼容语义；
    /// VT-CUTSCENE-REPLAY-ORIGIN-01：原点锚开演覆写与原版阻断真值表
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
        public void ArchivedCutscene_PlayerStance_RoundTripsThroughStorageJson()
        {
            var cutscene = new ArchivedCutscene
            {
                Title = "山中农舍重聚曲",
                LocationName = "FarmHouse",
                ActorNames = new System.Collections.Generic.List<string> { "Alex" },
                ActorStances = new System.Collections.Generic.List<ArchivedActorStance>
                {
                    new ArchivedActorStance { Name = "Alex", TileX = 12, TileY = 9, Facing = 2 }
                },
                PlayerStance = new ArchivedActorStance { Name = "Player", TileX = 9, TileY = 11, Facing = 1 },
                UserIntent = "回归录制现场重播",
                RawJson = @"{""title"":""山中农舍重聚曲"",""actions"":[]}",
                ActionCount = 6,
                CreatedAt = new DateTime(2026, 10, 3, 4, 30, 0)
            };

            string path = Path.Combine(Path.GetTempPath(), $"vt_archived_{Guid.NewGuid():N}.json");
            try
            {
                StorageJson.Write(path, cutscene);
                var loaded = StorageJson.Read<ArchivedCutscene>(path);

                Assert.NotNull(loaded);
                Assert.Equal("山中农舍重聚曲", loaded.Title);

                Assert.NotNull(loaded.PlayerStance);
                Assert.Equal("Player", loaded.PlayerStance.Name);
                Assert.Equal(9, loaded.PlayerStance.TileX);
                Assert.Equal(11, loaded.PlayerStance.TileY);
                Assert.Equal(1, loaded.PlayerStance.Facing);
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

            // 旧归档无农夫站位记录：PlayerStance 反序列化为默认实例（TileX/TileY 双零 → 回放走兜底落地）
            Assert.NotNull(loaded.PlayerStance);
            Assert.Equal(0, loaded.PlayerStance.TileX);
            Assert.Equal(0, loaded.PlayerStance.TileY);
        }

        // ─────────────────────────────────────────────────────────────────────
        // VT-CUTSCENE-REPLAY-ORIGIN-01
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>极简导演动作桩：Enter 记录进入即完成，供无头开演派发。</summary>
        private sealed class StubDirectorAction : IDirectorAction
        {
            public bool Entered { get; private set; }
            public bool WaitForCompletion { get; set; }

            public void Enter() => Entered = true;
            public bool Update(Microsoft.Xna.Framework.GameTime time) => true;
            public void Exit() { }
        }

        /// <summary>菜单哨兵：IClickableMenu 抽象类的空派生（其无参构造为空体，无头安全）。</summary>
        private sealed class MenuSentinel : IClickableMenu
        {
        }

        /// <summary>极简作用域复位器：Dispose 时执行注入的还原动作。</summary>
        private sealed class RestoreScope : IDisposable
        {
            private readonly Action _restore;
            public RestoreScope(Action restore) => _restore = restore;
            public void Dispose() => _restore();
        }

        private static readonly BindingFlags InstanceFlags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static object GetStaticField(Type type, string name) =>
            type.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);

        private static void SetStaticField(Type type, string name, object value) =>
            type.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .SetValue(null, value);

        /// <summary>
        /// 未初始化 GameLocation：装 name 与 uniqueName 两个 NetString。
        /// NameOrUniqueName 直读 uniqueName.Value（null 字段会 NRE），两者都不可缺。
        /// </summary>
        private static GameLocation MakeHeadlessLocation(string name)
        {
            var location = (GameLocation)FormatterServices.GetUninitializedObject(typeof(GameLocation));
            location.GetType().GetField("name", InstanceFlags).SetValue(location, new NetString(name));
            location.GetType().GetField("uniqueName", InstanceFlags).SetValue(location, new NetString());
            return location;
        }

        /// <summary>
        /// 未初始化 Farmer：补 Character.position（NetPosition）、瓦格缓存对（令 Tile 短路返回，
        /// 绕开 StandingPixel 的 Sprite 依赖）与 facingDirection（NetInt）。
        /// </summary>
        private static Farmer MakeHeadlessFarmer(Vector2 tile, int facing)
        {
            var farmer = (Farmer)FormatterServices.GetUninitializedObject(typeof(Farmer));
            var characterType = typeof(StardewValley.Character);
            characterType.GetField("position", InstanceFlags).SetValue(farmer, new NetPosition());
            farmer.Position = tile * 64f;
            characterType.GetField("cachedTile", InstanceFlags).SetValue(farmer, tile);
            characterType.GetField("pixelPositionForCachedTile", InstanceFlags).SetValue(farmer, tile * 64f);
            characterType.GetField("facingDirection", InstanceFlags)
                .SetValue(farmer, new NetDirection(facing));
            // Farmer.FacingDirection 重写版经 IsLocalPlayer 读 uniqueMultiplayerID（NetInt64）
            typeof(Farmer).GetField("uniqueMultiplayerID", InstanceFlags)
                .SetValue(farmer, new NetLong(0));
            return farmer;
        }

        /// <summary>
        /// 把 Character（含 Farmer 派生）的 currentLocationRef 指向传入地点实例
        /// （直接写 _gameLocation 并复位 _dirty，绕开 NetLocationRef.Set 的地图名解析，先例：
        /// FarmGreenhouseObservationTests.InstallLocationRef）。
        /// </summary>
        private static void InstallCharacterLocation(StardewValley.Character character, GameLocation location)
        {
            var locationRef = new NetLocationRef();
            locationRef.GetType().GetField("_gameLocation", InstanceFlags).SetValue(locationRef, location);
            locationRef.GetType().GetField("_dirty", InstanceFlags).SetValue(locationRef, false);
            typeof(StardewValley.Character).GetField("currentLocationRef", InstanceFlags)
                .SetValue(character, locationRef);
        }

        /// <summary>把导演复位到空闲态（IsActive 私有 setter、私有 _phase/_snapshot/队列）。</summary>
        private static void ResetDirector(VirtualDirector director)
        {
            var directorType = typeof(VirtualDirector);
            directorType.GetField("<IsActive>k__BackingField", InstanceFlags).SetValue(director, false);
            var phaseField = directorType.GetField("_phase", InstanceFlags);
            phaseField.SetValue(director, Enum.Parse(phaseField.FieldType, "Idle"));
            directorType.GetField("_snapshot", InstanceFlags).SetValue(director, null);
            ((Queue<IDirectorAction>)directorType.GetField("_actionQueue", InstanceFlags).GetValue(director)).Clear();
            ((List<IDirectorAction>)directorType.GetField("_activeActions", InstanceFlags).GetValue(director)).Clear();
        }

        /// <summary>
        /// Play 终点的 Keyboard.GetState 会触发 MonoGame 的 SDL2 本机库加载：无头测试进程
        /// 探测路径上缺 SDL2.dll 时（Clean 重建后的 bin），从仓库游戏目录补一份，保证用例可复现
        /// </summary>
        private static void EnsureHeadlessSdl2()
        {
            const string probeName = "SDL2.dll";
            string baseDir = AppContext.BaseDirectory;
            if (File.Exists(Path.Combine(baseDir, probeName)))
                return;

            for (var dir = new DirectoryInfo(baseDir); dir != null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "Stardew Valley", probeName);
                if (!File.Exists(candidate))
                    continue;
                File.Copy(candidate, Path.Combine(baseDir, probeName));
                return;
            }
        }

        [Fact]
        public void Play_WithOriginAnchor_OverridesSnapshotPlayerFields_AndForcesPlayerCanMove()
        {
            EnsureHeadlessSdl2();
            var director = VirtualDirector.Instance;
            object previousPlayer = GetStaticField(typeof(Game1), "_player");
            object previousGame1 = GetStaticField(typeof(Game1), "game1");
            object previousNetWorldState = GetStaticField(typeof(Game1), "netWorldState");
            bool previousEventUp = Game1.eventUp;
            object previousMenu = GetStaticField(typeof(Game1), "_activeClickableMenu");
            bool previousDisplayHud = Game1.displayHUD;
            bool previousViewportFreeze = Game1.viewportFreeze;

            try
            {
                ResetDirector(director);

                // IsPlayerBlockedByVanillaState → Game1.CurrentEvent → currentLocation → game1.instanceGameLocation
                var stageLocation = MakeHeadlessLocation("Farm");
                var game1Shim = (Game1)FormatterServices.GetUninitializedObject(typeof(Game1));
                typeof(Game1).GetField("instanceGameLocation", InstanceFlags).SetValue(game1Shim, stageLocation);
                SetStaticField(typeof(Game1), "game1", game1Shim);
                // NetPosition.Value 读写经 NetPosition.Get → Game1.HostPaused → netWorldState
                SetStaticField(typeof(Game1), "netWorldState",
                    Activator.CreateInstance(typeof(NetRoot<NetWorldState>), new NetWorldState()));

                var farmer = MakeHeadlessFarmer(new Vector2(25f, 40f), 2);
                InstallCharacterLocation(farmer, stageLocation);
                SetStaticField(typeof(Game1), "_player", farmer);
                Game1.eventUp = false;
                SetStaticField(typeof(Game1), "_activeClickableMenu", null);

                var actions = new List<IDirectorAction> { new StubDirectorAction() };
                var anchor = new VirtualDirector.PlayerAnchor("OriginFarm", new Vector2(25f, 40f), 1);

                TestEnvironment.WithWorldReady(() =>
                    director.Play(actions, new List<NPC>(), anchor));

                var snapshot = (CutsceneSnapshot)typeof(VirtualDirector)
                    .GetField("_snapshot", InstanceFlags)
                    .GetValue(director);
                Assert.NotNull(snapshot); // 为 null 意味着 Play 途中异常自愈，锚点链路未达成

                // 锚点覆写三字段：Capture 盲读的是 "Farm"，终态必须是原点锚 "OriginFarm"
                Assert.Equal("OriginFarm", snapshot.PlayerLocationName);
                Assert.Equal(new Vector2(25f, 40f), snapshot.PlayerTile);
                Assert.Equal(1, snapshot.PlayerFacing);
                // 谢幕终态无条件可动：Capture 盲读的 CanMove（垫片下为 false）不得成为唯一终态
                Assert.True(snapshot.PlayerCanMove);
                Assert.True(director.IsActive); // 演出确已开演（而非门槛拒绝或异常中止）
                Assert.True(((StubDirectorAction)actions[0]).Entered); // 首批动作已派发
            }
            finally
            {
                ResetDirector(director);
                SetStaticField(typeof(Game1), "_player", previousPlayer);
                SetStaticField(typeof(Game1), "game1", previousGame1);
                SetStaticField(typeof(Game1), "netWorldState", previousNetWorldState);
                Game1.eventUp = previousEventUp;
                SetStaticField(typeof(Game1), "_activeClickableMenu", previousMenu);
                Game1.displayHUD = previousDisplayHud;
                Game1.viewportFreeze = previousViewportFreeze;
            }
        }

        [Fact]
        public void IsPlayerBlockedByVanillaState_TruthTableOverVanillaBlockers()
        {
            object previousGame1 = GetStaticField(typeof(Game1), "game1");
            bool previousEventUp = Game1.eventUp;
            object previousMenu = GetStaticField(typeof(Game1), "_activeClickableMenu");

            try
            {
                // CurrentEvent 为 get-only 属性（currentLocation.currentEvent）：
                // 经未初始化 game1 实例指入无头地点后，写其公共字段 currentEvent
                var location = MakeHeadlessLocation("Farm");
                var game1Shim = (Game1)FormatterServices.GetUninitializedObject(typeof(Game1));
                typeof(Game1).GetField("instanceGameLocation", InstanceFlags).SetValue(game1Shim, location);
                SetStaticField(typeof(Game1), "game1", game1Shim);

                foreach (bool eventUp in new[] { false, true })
                foreach (bool hasEvent in new[] { false, true })
                foreach (bool hasMenu in new[] { false, true })
                {
                    Game1.eventUp = eventUp;
                    location.currentEvent = hasEvent
                        ? (Event)FormatterServices.GetUninitializedObject(typeof(Event))
                        : null;
                    SetStaticField(typeof(Game1), "_activeClickableMenu", hasMenu ? new MenuSentinel() : null);

                    bool blocked = VirtualDirector.IsPlayerBlockedByVanillaState();
                    Assert.True(blocked == (eventUp || hasEvent || hasMenu),
                        $"eventUp={eventUp}, CurrentEvent!=null={hasEvent}, menu!=null={hasMenu} → blocked={blocked}");
                }
            }
            finally
            {
                SetStaticField(typeof(Game1), "game1", previousGame1);
                Game1.eventUp = previousEventUp;
                SetStaticField(typeof(Game1), "_activeClickableMenu", previousMenu);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // VT-CUTSCENE-BACKSTAGE-SUSPEND-02：同图孪生避让与复原
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 未初始化 NPC：补齐避让/复原路径读写的 Character/NPC 成员
        /// （name、position+瓦格缓存对、facingDirection、isInvisible、sprite NetRef
        /// ——faceDirection 的空检查需要非 null NetRef——simpleNonVillagerNPC、modData 背板）。
        /// </summary>
        private static NPC MakeSuspendableNpc(string name, Vector2 tile, int facing)
        {
            var npc = (NPC)FormatterServices.GetUninitializedObject(typeof(NPC));
            var characterType = typeof(StardewValley.Character);
            characterType.GetField("name", InstanceFlags).SetValue(npc, new NetString(name));
            characterType.GetField("position", InstanceFlags).SetValue(npc, new NetPosition());
            npc.Position = tile * 64f;
            characterType.GetField("cachedTile", InstanceFlags).SetValue(npc, tile);
            characterType.GetField("pixelPositionForCachedTile", InstanceFlags).SetValue(npc, tile * 64f);
            characterType.GetField("facingDirection", InstanceFlags).SetValue(npc, new NetDirection(facing));
            typeof(NPC).GetField("isInvisible", InstanceFlags).SetValue(npc, new NetBool());
            characterType.GetField("sprite", InstanceFlags).SetValue(npc, new NetRef<AnimatedSprite>());
            characterType.GetField("simpleNonVillagerNPC", InstanceFlags).SetValue(npc, new NetBool());
            characterType.GetField("<modData>k__BackingField", InstanceFlags)
                .SetValue(npc, new ModDataDictionary());
            return npc;
        }

        /// <summary>把地点的 characters（NetCollection）指向全新集合并注入传入角色。</summary>
        private static void InstallCharacters(GameLocation location, params NPC[] npcs)
        {
            var characters = new NetCollection<NPC>();
            foreach (var npc in npcs)
                characters.Add(npc);
            location.GetType().GetField("characters", InstanceFlags).SetValue(location, characters);
        }

        /// <summary>game1 实例垫片：_locations 指入传入地点列表（Game1.locations 读取）。</summary>
        private static IDisposable InstallGame1Locations(params GameLocation[] locations)
        {
            var game1Shim = (Game1)FormatterServices.GetUninitializedObject(typeof(Game1));
            typeof(Game1).GetField("_locations", InstanceFlags).SetValue(game1Shim, new List<GameLocation>(locations));
            SetStaticField(typeof(Game1), "game1", game1Shim);
            return new RestoreScope(() => SetStaticField(typeof(Game1), "game1", null));
        }

        /// <summary>netWorldState 垫片：NetPosition.Value 读写（Game1.HostPaused）与 setTilePosition 依赖。</summary>
        private static IDisposable InstallNetWorldState()
        {
            object previous = GetStaticField(typeof(Game1), "netWorldState");
            SetStaticField(typeof(Game1), "netWorldState",
                Activator.CreateInstance(typeof(NetRoot<NetWorldState>), new NetWorldState()));
            return new RestoreScope(() => SetStaticField(typeof(Game1), "netWorldState", previous));
        }

        private static bool ReadFreezeMotion(StardewValley.Character character) =>
            (bool)typeof(StardewValley.Character)
                .GetField("freezeMotion", InstanceFlags)
                .GetValue(character);

        [Fact]
        public void SuspendPrototypesIfPresent_HidesAndMarksSameMapPrototypes_LeavesOthersUntouched()
        {
            using var _ = InstallNetWorldState();
            var stage = MakeHeadlessLocation("FarmHouse");
            var haley = MakeSuspendableNpc("Haley", new Vector2(3f, 5f), 2);
            var sam = MakeSuspendableNpc("Sam", new Vector2(6f, 5f), 1);
            var clone = MakeSuspendableNpc("Haley", new Vector2(9f, 9f), 2);
            clone.modData[CutsceneCloneService.CloneMarkerKey] = "1";
            InstallCharacters(stage, haley, sam, clone);

            try
            {
                CutsceneCloneService.SuspendPrototypesIfPresent(new List<string> { "Haley" }, stage);

                // 同图同名真人原型：可见性 + 运动学双压制 + 标记键
                Assert.True(haley.IsInvisible);
                Assert.Equal("1", haley.modData[CutsceneCloneService.SuspendedMarkerKey]);
                Assert.True(ReadFreezeMotion(haley));
                // 同图其他真人：零接触
                Assert.False(sam.IsInvisible);
                Assert.False(ReadFreezeMotion(sam));
                Assert.False(sam.modData.ContainsKey(CutsceneCloneService.SuspendedMarkerKey));
                // 克隆演员（带克隆标记键）：不在避让谓词内
                Assert.False(clone.IsInvisible);
                Assert.False(clone.modData.ContainsKey(CutsceneCloneService.SuspendedMarkerKey));
            }
            finally
            {
                // 清空注册表，避免跨用例泄漏
                CutsceneCloneService.RestoreSuspendedPrototypes();
            }
        }

        [Fact]
        public void RestoreSuspendedPrototypes_ReversesSuspensionCleanly()
        {
            // HostPaused（NetPosition 读写）与 setTilePosition（Position 写）依赖
            using var _ = InstallNetWorldState();
            var stage = MakeHeadlessLocation("FarmHouse");
            var haley = MakeSuspendableNpc("Haley", new Vector2(3f, 5f), 2);
            InstallCharacters(stage, haley);

            CutsceneCloneService.SuspendPrototypesIfPresent(new List<string> { "Haley" }, stage);
            Assert.True(haley.IsInvisible);

            CutsceneCloneService.RestoreSuspendedPrototypes();

            Assert.False(haley.IsInvisible);
            Assert.False(haley.modData.ContainsKey(CutsceneCloneService.SuspendedMarkerKey));
            Assert.False(ReadFreezeMotion(haley));
            Assert.Equal(new Vector2(3f, 5f) * 64f, haley.Position); // 原瓦格归位
            Assert.Equal(2, haley.FacingDirection);                  // 原朝向复原

            // 注册表已清空：再次复原为静默无操作
            CutsceneCloneService.RestoreSuspendedPrototypes();
        }

        [Fact]
        public void SweepAll_ClearsSuspendedMarkerResidue_AndCloneResidue()
        {
            using var _ = InstallNetWorldState();
            var stage = MakeHeadlessLocation("FarmHouse");
            var leftoverClone = MakeSuspendableNpc("LeftoverClone", new Vector2(1f, 1f), 0);
            leftoverClone.modData[CutsceneCloneService.CloneMarkerKey] = "1";
            var haley = MakeSuspendableNpc("Haley", new Vector2(3f, 5f), 2);
            haley.IsInvisible = true;
            typeof(StardewValley.Character).GetField("freezeMotion", InstanceFlags).SetValue(haley, true);
            haley.modData[CutsceneCloneService.SuspendedMarkerKey] = "1";
            InstallCharacters(stage, leftoverClone, haley);

            using var world = InstallGame1Locations(stage);
            CutsceneCloneService.SweepAll();

            // 克隆残留：移出成员（既有克隆清扫语义不变）
            Assert.Same(haley, Assert.Single(stage.characters));
            // 压制残留：仅凭标记键确定性复原
            Assert.False(haley.IsInvisible);
            Assert.False(haley.modData.ContainsKey(CutsceneCloneService.SuspendedMarkerKey));
            Assert.False(ReadFreezeMotion(haley));
        }

        [Fact]
        public void StageClones_FullConstructionFailure_StillClosesSuspensionOnDisposeAll()
        {
            // 无头下克隆构造必然逐个失败（无地图/无内容/无 villager 数据）：
            // 克隆入图前的避让须在 DisposeAll 的零克隆路径上闭合复原
            using var _ = InstallNetWorldState();
            var stage = MakeHeadlessLocation("FarmHouse");
            var haley = MakeSuspendableNpc("Haley", new Vector2(3f, 5f), 2);
            InstallCharacters(stage, haley);

            using var world = InstallGame1Locations(stage);
            try
            {
                var cutscene = new ArchivedCutscene
                {
                    Title = "避让闭合验证",
                    LocationName = "FarmHouse",
                    ActorStances = new List<ArchivedActorStance>
                    {
                        new ArchivedActorStance { Name = "Haley", TileX = 3, TileY = 5, Facing = 2 }
                    },
                    RawJson = @"{""title"":""避让闭合验证"",""actions"":[]}"
                };

                var clones = CutsceneCloneService.StageClones(cutscene, stage);
                Assert.Empty(clones);
                Assert.True(haley.IsInvisible); // 克注入图前已避让

                CutsceneCloneService.DisposeAll(); // 零克隆路径仍须复原
                Assert.False(haley.IsInvisible);
                Assert.False(haley.modData.ContainsKey(CutsceneCloneService.SuspendedMarkerKey));
                Assert.False(ReadFreezeMotion(haley));
            }
            finally
            {
                CutsceneCloneService.RestoreSuspendedPrototypes();
            }
        }
    }
}
