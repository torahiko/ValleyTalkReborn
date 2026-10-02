using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using ValleytalkReborn.Cutscene;
using ValleytalkReborn.Cutscene.Actions;
using ValleytalkReborn.Cutscene.Model;
using ValleytalkReborn.Cutscene.Perception;
using ValleytalkReborn.Cutscene.Prompt;
using ValleytalkReborn.Cutscene.Services;
using Xunit;

namespace ValleytalkReborn.Tests
{
    public class CutscenePhase5Tests
    {
        static CutscenePhase5Tests()
        {
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                string assemblyName = new AssemblyName(args.Name).Name;
                string smapiInternal = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..",
                    "Stardew Valley", "smapi-internal", assemblyName + ".dll");
                return File.Exists(smapiInternal) ? Assembly.LoadFrom(smapiInternal) : null;
            };
        }

        [Fact]
        public void CutsceneScriptIR_Deserializes_ChoiceActionAndOptions()
        {
            string json = @"
            {
                ""title"": ""Alex Training Chat"",
                ""actors"": [""Alex""],
                ""actions"": [
                    { ""type"": ""speak"", ""actor"": ""Alex"", ""text"": ""你觉得我能成为职业球手吗？"" },
                    {
                        ""type"": ""choice"",
                        ""actor"": ""Alex"",
                        ""prompt"": ""Alex 满怀期待地看着你，你要如何回答？"",
                        ""options"": [
                            {
                                ""text"": ""我相信你一定可以！"",
                                ""friendship"": 15,
                                ""feedback"": ""Alex更喜欢你了。"",
                                ""actions"": [
                                    { ""type"": ""emote"", ""actor"": ""Alex"", ""emote"": ""HEART"", ""waitForCompletion"": false },
                                    { ""type"": ""speak"", ""actor"": ""Alex"", ""text"": ""哈哈，有你这句话我就放心了！"" }
                                ]
                            },
                            {
                                ""text"": ""现实往往很残酷..."",
                                ""friendship"": -10,
                                ""feedback"": ""Alex有点难过。"",
                                ""actions"": [
                                    { ""type"": ""emote"", ""actor"": ""Alex"", ""emote"": ""SAD"", ""waitForCompletion"": false },
                                    { ""type"": ""speak"", ""actor"": ""Alex"", ""text"": ""好吧...也许你说得对。"" }
                                ]
                            }
                        ]
                    }
                ]
            }";

            var script = JsonConvert.DeserializeObject<CutsceneScriptIR>(json);

            Assert.NotNull(script);
            Assert.Equal("Alex Training Chat", script.Title);
            Assert.Equal(2, script.Actions.Count);

            var choiceAction = script.Actions[1];
            Assert.Equal("choice", choiceAction.Type);
            Assert.Equal("Alex", choiceAction.Actor);
            Assert.Equal("Alex 满怀期待地看着你，你要如何回答？", choiceAction.Prompt);
            Assert.NotNull(choiceAction.Options);
            Assert.Equal(2, choiceAction.Options.Count);

            var opt1 = choiceAction.Options[0];
            Assert.Equal("我相信你一定可以！", opt1.Text);
            Assert.Equal(15, opt1.Friendship);
            Assert.Equal("Alex更喜欢你了。", opt1.Feedback);
            Assert.Equal(2, opt1.Actions.Count);
            Assert.Equal("emote", opt1.Actions[0].Type);
            Assert.Equal("speak", opt1.Actions[1].Type);

            var opt2 = choiceAction.Options[1];
            Assert.Equal("现实往往很残酷...", opt2.Text);
            Assert.Equal(-10, opt2.Friendship);
            Assert.Equal("Alex有点难过。", opt2.Feedback);
            Assert.Equal(2, opt2.Actions.Count);
        }

        [Fact]
        public void ChoiceOption_Properties_InitializedProperly()
        {
            var subActions = new List<IDirectorAction> { new WaitAction(1.0f) };
            var option = new ChoiceOption(
                text: "好的，我们走吧！",
                friendshipDelta: 20,
                feedbackMessage: "Sam更喜欢你了。",
                actions: subActions
            );

            Assert.Equal("好的，我们走吧！", option.Text);
            Assert.Equal(20, option.FriendshipDelta);
            Assert.Equal("Sam更喜欢你了。", option.FeedbackMessage);
            Assert.Single(option.Actions);
            Assert.Same(subActions[0], option.Actions[0]);

            var defaultOption = new ChoiceOption("默认选项");
            Assert.Equal("默认选项", defaultOption.Text);
            Assert.Equal(0, defaultOption.FriendshipDelta);
            Assert.Null(defaultOption.FeedbackMessage);
            Assert.Empty(defaultOption.Actions);
        }

        [Fact]
        public void ChoiceAction_Properties_InitializedProperly()
        {
            var options = new List<ChoiceOption>
            {
                new ChoiceOption("选项 A", 10, "反馈 A"),
                new ChoiceOption("选项 B", -5, "反馈 B")
            };

            var action = new ChoiceAction("请做出你的决定：", options, targetActor: null, isSerendipity: true);

            Assert.Equal("请做出你的决定：", action.Prompt);
            Assert.Equal(2, action.Options.Count);
            Assert.Null(action.TargetActor);
            Assert.True(action.WaitForCompletion);
            Assert.True(action.IsSerendipity);
        }

        [Fact]
        public void FriendshipSettlementService_CalculateAllowedDelta_EnforcesDailyCap()
        {
            var service = FriendshipSettlementService.Instance;
            service.Reset();

            // First gain
            Assert.Equal(15, service.CalculateAllowedDelta("Alex", 15));
            Assert.Equal(20, service.CalculateAllowedDelta("Alex", 25)); // Clamped to max +20

            // Losses
            Assert.Equal(-10, service.CalculateAllowedDelta("Alex", -10));
            Assert.Equal(-20, service.CalculateAllowedDelta("Alex", -35)); // Clamped to max -20

            // Edge cases
            Assert.Equal(0, service.CalculateAllowedDelta("", 10));
            Assert.Equal(0, service.CalculateAllowedDelta(null, 10));
            Assert.Equal(0, service.CalculateAllowedDelta("Alex", 0));
        }

        [Fact]
        public void FriendshipSettlementService_SandboxMode_NeverModifiesDailyChanges()
        {
            var service = FriendshipSettlementService.Instance;
            service.Reset();

            service.ApplyFriendshipDelta("Sam", 15, isSerendipity: false, feedbackText: "Sam更喜欢你了。");
            Assert.Equal(0, service.GetDailyChange("Sam"));

            service.ApplyFriendshipDelta("Sam", -10, isSerendipity: false, feedbackText: "Sam有点难过。");
            Assert.Equal(0, service.GetDailyChange("Sam"));
        }

        [Fact]
        public void FriendshipSettlementService_SerendipityMode_AccumulatesAndCapsDeltas()
        {
            var service = FriendshipSettlementService.Instance;
            service.Reset();

            // +15 gain
            service.ApplyFriendshipDelta("Shane", 15, isSerendipity: true);
            Assert.Equal(15, service.GetDailyChange("Shane"));

            // +10 gain -> capped at +20 (+5 allowed)
            service.ApplyFriendshipDelta("Shane", 10, isSerendipity: true);
            Assert.Equal(20, service.GetDailyChange("Shane"));

            // Further gain -> 0 allowed
            service.ApplyFriendshipDelta("Shane", 5, isSerendipity: true);
            Assert.Equal(20, service.GetDailyChange("Shane"));

            // -15 loss
            service.ApplyFriendshipDelta("Shane", -15, isSerendipity: true);
            Assert.Equal(5, service.GetDailyChange("Shane"));

            // -30 loss -> capped at -20 total daily change
            service.ApplyFriendshipDelta("Shane", -30, isSerendipity: true);
            Assert.Equal(-20, service.GetDailyChange("Shane"));

            // Further loss -> 0 allowed
            service.ApplyFriendshipDelta("Shane", -5, isSerendipity: true);
            Assert.Equal(-20, service.GetDailyChange("Shane"));
        }

        [Fact]
        public void FriendshipSettlementService_OnDayStarted_ResetsDailyChanges()
        {
            var service = FriendshipSettlementService.Instance;
            service.Reset();

            service.ApplyFriendshipDelta("Alex", 20, isSerendipity: true);
            service.ApplyFriendshipDelta("Haley", -10, isSerendipity: true);

            Assert.Equal(20, service.GetDailyChange("Alex"));
            Assert.Equal(-10, service.GetDailyChange("Haley"));

            service.OnDayStarted();

            Assert.Equal(0, service.GetDailyChange("Alex"));
            Assert.Equal(0, service.GetDailyChange("Haley"));
        }

        [Fact]
        public void CutscenePromptBuilder_SystemPrompt_ContainsChoiceSchemaAndRules()
        {
            string systemPrompt = CutscenePromptBuilder.BuildSystemPrompt();

            Assert.Contains(@"""type"": ""choice""", systemPrompt);
            Assert.Contains(@"""prompt""", systemPrompt);
            Assert.Contains(@"""options""", systemPrompt);
            Assert.Contains(@"""friendship""", systemPrompt);
            Assert.Contains(@"""feedback""", systemPrompt);
            Assert.Contains("【分支选择规则（choice）】", systemPrompt);
        }

        [Fact]
        public void CutscenePromptBuilder_SystemPrompt_ChoiceRuleAnchorsFarmerInvolvement()
        {
            string systemPrompt = CutscenePromptBuilder.BuildSystemPrompt();

            // 可验证边界：choice 的受话人是农夫本人，且剧情走向取决于农夫的表态
            Assert.Contains("受话人始终是农夫本人", systemPrompt);
            // 焦点锚定：村民之间的交流以村民为镜头焦点，不强制卷入农夫
            Assert.Contains("镜头与台词始终聚焦在村民身上", systemPrompt);
        }

        [Fact]
        public void CutscenePromptBuilder_UserPrompt_SoloActor_ChoiceConditionalOnFarmerInvolvement()
        {
            var ctx = BuildSoloContext();

            string userPrompt = CutscenePromptBuilder.BuildUserPrompt(ctx, "测试日常");

            // 回归守卫：旧的"请适时编排 1 个 choice 动作"强制措辞必须移除
            Assert.DoesNotContain("请适时编排", userPrompt);
            Assert.Contains("只有农夫才能回答", userPrompt);
            Assert.Contains("亚历克斯更喜欢你了。", userPrompt);
        }

        [Fact]
        public void CutscenePromptBuilder_UserPrompt_MultiActor_ChoiceOnlyWhenFarmerAddressed()
        {
            var ctx = BuildSoloContext();
            ctx.Actors.Add(new ActorProfile
            {
                Name = "Haley",
                DisplayName = "海莉",
                CurrentTile = new Microsoft.Xna.Framework.Vector2(24, 31),
                FacingDirection = 3,
                HeartLevel = 2,
                FriendshipPoints = 500
            });

            string userPrompt = CutscenePromptBuilder.BuildUserPrompt(ctx, null);

            Assert.Contains("choice 动作只属于", userPrompt);
            Assert.Contains("镜头始终聚焦交流中的村民", userPrompt);
            Assert.DoesNotContain("增强农夫的临场参与感", userPrompt);
        }

        [Fact]
        public void CutscenePromptBuilder_UserPrompt_ContainsChoiceGuidance_WhenSoloActor()
        {
            var ctx = BuildSoloContext();

            string userPrompt = CutscenePromptBuilder.BuildUserPrompt(ctx, "测试日常");

            Assert.Contains("互动分支", userPrompt);
            Assert.Contains("choice", userPrompt);
            Assert.Contains("亚历克斯更喜欢你了。", userPrompt);
        }

        private static CutsceneContext BuildSoloContext()
        {
            return new CutsceneContext
            {
                LocationName = "Town",
                LocationFriendlyName = "鹈鹕镇",
                Season = "spring",
                DayOfMonth = 10,
                TimeOfDay = 1200,
                Weather = "Sun",
                FarmerTile = new Microsoft.Xna.Framework.Vector2(20, 30),
                Actors = new List<ActorProfile>
                {
                    new ActorProfile
                    {
                        Name = "Alex",
                        DisplayName = "亚历克斯",
                        CurrentTile = new Microsoft.Xna.Framework.Vector2(22, 30),
                        FacingDirection = 2,
                        HeartLevel = 4,
                        FriendshipPoints = 1000
                    }
                }
            };
        }
    }
}
