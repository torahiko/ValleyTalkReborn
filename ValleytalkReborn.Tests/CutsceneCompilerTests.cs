using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ValleytalkReborn.Cutscene.Compiler;
using ValleytalkReborn.Cutscene.Model;
using Xunit;

namespace ValleytalkReborn.Tests
{
    public class CutsceneCompilerTests
    {
        [Fact]
        public void ExtractJson_WithMarkdownFences_ExtractsCleanJson()
        {
            string raw = "Here is the cutscene script:\n```json\n{\n  \"title\": \"Morning Encounter\",\n  \"actors\": [\"Alex\"]\n}\n```\nHope you like it!";
            string clean = CutsceneScriptCompiler.ExtractJson(raw);

            Assert.NotNull(clean);
            Assert.Contains("\"title\": \"Morning Encounter\"", clean);
            Assert.DoesNotContain("```", clean);
            Assert.DoesNotContain("Hope you like it!", clean);
        }

        [Fact]
        public void ExtractJson_WithNestedBracesAndEscapedQuotes_ExtractsAccurately()
        {
            string raw = "Random text prefix { \"title\": \"Test {with nested}\", \"actions\": [ { \"type\": \"speak\", \"text\": \"He said: \\\"Hello!\\\"\" } ] } trailing noise";
            string clean = CutsceneScriptCompiler.ExtractJson(raw);

            Assert.NotNull(clean);
            Assert.StartsWith("{", clean);
            Assert.EndsWith("}", clean);
            Assert.Contains("He said: \\\"Hello!\\\"", clean);
        }

        [Fact]
        public void ResolveTargetTile_WithArrayObjectAndString_ParsesAccurately()
        {
            // Case 1: [64, 67]
            var action1 = new CutsceneActionIR { TargetTileToken = JToken.Parse("[64, 67]") };
            Vector2? tile1 = action1.ResolveTargetTile();
            Assert.NotNull(tile1);
            Assert.Equal(64f, tile1.Value.X);
            Assert.Equal(67f, tile1.Value.Y);

            // Case 2: {"x": 10.5, "y": 20.5}
            var action2 = new CutsceneActionIR { TargetTileToken = JToken.Parse("{\"x\": 10.5, \"y\": 20.5}") };
            Vector2? tile2 = action2.ResolveTargetTile();
            Assert.NotNull(tile2);
            Assert.Equal(10.5f, tile2.Value.X);
            Assert.Equal(20.5f, tile2.Value.Y);

            // Case 3: "30, 40"
            var action3 = new CutsceneActionIR { TargetTileToken = JToken.Parse("\"30, 40\"") };
            Vector2? tile3 = action3.ResolveTargetTile();
            Assert.NotNull(tile3);
            Assert.Equal(30f, tile3.Value.X);
            Assert.Equal(40f, tile3.Value.Y);

            // Case 4: Invalid format
            var action4 = new CutsceneActionIR { TargetTileToken = JToken.Parse("\"invalid\"") };
            Vector2? tile4 = action4.ResolveTargetTile();
            Assert.Null(tile4);
        }

        [Fact]
        public void ResolveDirection_WithNumbersAndBilingualStrings_ParsesAccurately()
        {
            var testCases = new (string jsonToken, int? expected)[]
            {
                ("0", 0),
                ("1", 1),
                ("2", 2),
                ("3", 3),
                ("\"up\"", 0),
                ("\"north\"", 0),
                ("\"上\"", 0),
                ("\"right\"", 1),
                ("\"east\"", 1),
                ("\"右\"", 1),
                ("\"down\"", 2),
                ("\"south\"", 2),
                ("\"下\"", 2),
                ("\"left\"", 3),
                ("\"west\"", 3),
                ("\"左\"", 3),
                ("\"invalid\"", null)
            };

            foreach (var (jsonToken, expected) in testCases)
            {
                var action = new CutsceneActionIR { Direction = JToken.Parse(jsonToken) };
                int? actual = action.ResolveDirection();
                Assert.Equal(expected, actual);
            }
        }

        [Fact]
        public void CutsceneScriptIR_Deserializes_ParallelAndSequentialFlags()
        {
            string json = @"
            {
                ""title"": ""Test Script"",
                ""actors"": [""Alex"", ""Haley""],
                ""actions"": [
                    { ""type"": ""camera"", ""target"": ""Alex"", ""waitForCompletion"": false },
                    { ""type"": ""emote"", ""actor"": ""Alex"", ""emote"": ""SURPRISE"", ""waitForCompletion"": false },
                    { ""type"": ""move"", ""actor"": ""Alex"", ""targetTile"": [64, 67], ""waitForCompletion"": true },
                    { ""type"": ""wait"", ""duration"": 1.5, ""waitForCompletion"": true },
                    { ""type"": ""sound"", ""soundName"": ""dwop"", ""waitForCompletion"": false }
                ]
            }";

            var script = JsonConvert.DeserializeObject<CutsceneScriptIR>(json);

            Assert.NotNull(script);
            Assert.Equal("Test Script", script.Title);
            Assert.Equal(2, script.Actors.Count);
            Assert.Equal(5, script.Actions.Count);

            Assert.False(script.Actions[0].WaitForCompletion);
            Assert.False(script.Actions[1].WaitForCompletion);
            Assert.True(script.Actions[2].WaitForCompletion);
            Assert.True(script.Actions[3].WaitForCompletion);
            Assert.False(script.Actions[4].WaitForCompletion);
        }
    }
}
