using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Newtonsoft.Json;
using ValleytalkReborn.Cutscene.Compiler;
using ValleytalkReborn.Cutscene.Model;
using ValleytalkReborn.Cutscene.Perception;
using ValleytalkReborn.Cutscene.Prompt;
using Xunit;

namespace ValleytalkReborn.Tests
{
    public class CutsceneGeneratorTests
    {
        [Fact]
        public void BuildSystemPrompt_ContainsEssentialSchemaAndDirectives()
        {
            string prompt = CutscenePromptBuilder.BuildSystemPrompt();

            Assert.NotNull(prompt);
            Assert.Contains("Virtual Cutscene Director", prompt);
            Assert.Contains("```json", prompt);
            Assert.Contains("\"title\"", prompt);
            Assert.Contains("\"actors\"", prompt);
            Assert.Contains("\"actions\"", prompt);
            Assert.Contains("空间锚定铁律", prompt);
            Assert.Contains("waitForCompletion", prompt);
        }

        [Fact]
        public void BuildSystemPrompt_ContainsOpeningFreezeDiscipline_WithoutCommuteWording()
        {
            string prompt = CutscenePromptBuilder.BuildSystemPrompt();

            // 开场定格铁律：第 0 帧已就位面向农夫，剧本直接以 camera/emote/face/speak 开场
            Assert.Contains("【开场定格铁律】", prompt);
            Assert.Contains("无需任何入场移动", prompt);
            // 通勤式走位纪律必须移除
            Assert.DoesNotContain("必须移动走向农夫", prompt);
            Assert.DoesNotContain("必须主动走向", prompt);
        }

        [Fact]
        public void BuildUserPrompt_SoloActor_OpeningFreezeReplacesCommuteWording()
        {
            var ctx = new CutsceneContext
            {
                LocationName = "Town",
                LocationFriendlyName = "鹈鹕镇",
                Season = "spring",
                DayOfMonth = 15,
                TimeOfDay = 1900,
                Weather = "Sun",
                FarmerTile = new Vector2(10, 20),
                Actors = new List<ActorProfile>
                {
                    new ActorProfile
                    {
                        Name = "Abigail",
                        DisplayName = "阿比盖尔",
                        CurrentTile = new Vector2(12, 22),
                        FacingDirection = 2,
                        FriendshipPoints = 1500,
                        HeartLevel = 6
                    }
                }
            };

            string userPrompt = CutscenePromptBuilder.BuildUserPrompt(ctx, null);

            Assert.Contains("开场即已立于农夫身旁", userPrompt);
            Assert.Contains("无需任何入场移动", userPrompt);
            Assert.DoesNotContain("必须主动走向", userPrompt);
        }

        [Fact]
        public void BuildUserPrompt_IncludesAllContextElementsAndIntent()
        {
            var ctx = new CutsceneContext
            {
                LocationName = "Saloon",
                LocationFriendlyName = "格斯星落酒吧",
                Season = "spring",
                DayOfMonth = 15,
                TimeOfDay = 1900,
                Weather = "Raining",
                FarmerTile = new Vector2(10, 20),
                Actors = new List<ActorProfile>
                {
                    new ActorProfile
                    {
                        Name = "Abigail",
                        DisplayName = "阿比盖尔",
                        CurrentTile = new Vector2(12, 22),
                        FacingDirection = 2,
                        FriendshipPoints = 1500,
                        HeartLevel = 6,
                        PersonalitySummary = "喜欢冒险、紫发乐手"
                    }
                },
                NearbyPois = new List<PoiInfo>
                {
                    new PoiInfo { Description = "酒吧吧台", Tile = new Vector2(14, 20) },
                    new PoiInfo { Description = "点唱机", Tile = new Vector2(11, 24) }
                }
            };

            string userPrompt = CutscenePromptBuilder.BuildUserPrompt(ctx, "讨论今晚的雨景和冒险");

            Assert.NotNull(userPrompt);
            Assert.Contains("格斯星落酒吧", userPrompt);
            Assert.Contains("Saloon", userPrompt);
            Assert.Contains("spring 第 15 天", userPrompt);
            Assert.Contains("1900", userPrompt);
            Assert.Contains("Raining", userPrompt);
            Assert.Contains("(10, 20)", userPrompt);
            Assert.Contains("Abigail", userPrompt);
            Assert.Contains("阿比盖尔", userPrompt);
            Assert.Contains("6 心", userPrompt);
            Assert.Contains("喜欢冒险、紫发乐手", userPrompt);
            Assert.Contains("酒吧吧台", userPrompt);
            Assert.Contains("[14, 20]", userPrompt);
            Assert.Contains("讨论今晚的雨景和冒险", userPrompt);
        }

        [Fact]
        public void BuildUserPrompt_WithoutPois_ProvidesFallbackRelativeCoordinates()
        {
            var ctx = new CutsceneContext
            {
                LocationName = "Town",
                LocationFriendlyName = "鹈鹕镇",
                Season = "summer",
                DayOfMonth = 1,
                TimeOfDay = 1200,
                Weather = "Sunny",
                FarmerTile = new Vector2(30, 40),
                Actors = new List<ActorProfile>
                {
                    new ActorProfile
                    {
                        Name = "Alex",
                        DisplayName = "亚历克斯",
                        CurrentTile = new Vector2(32, 40),
                        FacingDirection = 3
                    }
                },
                NearbyPois = new List<PoiInfo>() // Empty
            };

            string userPrompt = CutscenePromptBuilder.BuildUserPrompt(ctx, null);

            Assert.NotNull(userPrompt);
            Assert.Contains("鹈鹕镇", userPrompt);
            Assert.Contains("即兴日常微型互动", userPrompt);
            Assert.Contains("角色前方开阔区域", userPrompt);
            Assert.Contains("农夫身旁交谈位", userPrompt);
        }

        [Fact]
        public void LlmSimulatedOutput_FullCycleVerification_DeserializesAndValidates()
        {
            // 模拟大模型根据 System + User Prompt 生成的完整 JSON 剧本
            string simulatedLlmResponse = @"
            ```json
            {
              ""title"": ""酒吧雨夜闲谈"",
              ""actors"": [""Abigail""],
              ""actions"": [
                { ""type"": ""camera"", ""target"": ""Abigail"", ""waitForCompletion"": false },
                { ""type"": ""sound"", ""soundName"": ""dwop"", ""waitForCompletion"": false },
                { ""type"": ""emote"", ""actor"": ""Abigail"", ""emote"": ""SURPRISE"", ""waitForCompletion"": false },
                { ""type"": ""move"", ""actor"": ""Abigail"", ""targetTile"": [14, 20], ""timeout"": 5.0, ""waitForCompletion"": true },
                { ""type"": ""lookAt"", ""actor"": ""Abigail"", ""target"": ""farmer"", ""waitForCompletion"": true },
                { ""type"": ""wait"", ""duration"": 0.5, ""waitForCompletion"": true },
                { ""type"": ""speak"", ""actor"": ""Abigail"", ""text"": ""雨夜的酒吧总是格外温暖呢！"", ""waitForCompletion"": true },
                { ""type"": ""emote"", ""actor"": ""Abigail"", ""emote"": ""HEART"", ""waitForCompletion"": false },
                { ""type"": ""sound"", ""soundName"": ""coin"", ""waitForCompletion"": false }
              ]
            }
            ```";

            string cleanJson = CutsceneScriptCompiler.ExtractJson(simulatedLlmResponse);
            Assert.NotNull(cleanJson);

            var scriptIR = JsonConvert.DeserializeObject<CutsceneScriptIR>(cleanJson);
            Assert.NotNull(scriptIR);
            Assert.Equal("酒吧雨夜闲谈", scriptIR.Title);
            Assert.Single(scriptIR.Actors);
            Assert.Equal("Abigail", scriptIR.Actors[0]);
            Assert.Equal(9, scriptIR.Actions.Count);

            // 验证各个动作类型
            Assert.Equal("camera", scriptIR.Actions[0].Type);
            Assert.False(scriptIR.Actions[0].WaitForCompletion);

            Assert.Equal("sound", scriptIR.Actions[1].Type);
            Assert.Equal("dwop", scriptIR.Actions[1].SoundName);

            Assert.Equal("emote", scriptIR.Actions[2].Type);
            Assert.Equal("SURPRISE", scriptIR.Actions[2].Emote);

            Assert.Equal("move", scriptIR.Actions[3].Type);
            var targetTile = scriptIR.Actions[3].ResolveTargetTile();
            Assert.NotNull(targetTile);
            Assert.Equal(14f, targetTile.Value.X);
            Assert.Equal(20f, targetTile.Value.Y);
            Assert.True(scriptIR.Actions[3].WaitForCompletion);

            Assert.Equal("lookAt", scriptIR.Actions[4].Type);
            Assert.Equal("farmer", scriptIR.Actions[4].Target);

            Assert.Equal("wait", scriptIR.Actions[5].Type);
            Assert.Equal(0.5f, scriptIR.Actions[5].Duration);

            Assert.Equal("speak", scriptIR.Actions[6].Type);
            Assert.Equal("雨夜的酒吧总是格外温暖呢！", scriptIR.Actions[6].Text);
        }
    }
}
