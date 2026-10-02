using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Newtonsoft.Json;
using ValleytalkReborn.Cutscene;
using ValleytalkReborn.Cutscene.Storage;
using Xunit;

namespace ValleytalkReborn.Tests
{
    public class CutscenePhase3Tests
    {
        [Fact]
        public void ArchivedCutscene_Serialization_RoundTripsAccurately()
        {
            var cutscene = new ArchivedCutscene
            {
                Id = "test_scene_123",
                Title = "冬日酒吧聚会",
                LocationName = "Saloon",
                ActorNames = new List<string> { "Sam", "Sebastian", "Abigail" },
                UserIntent = "关于外面雪下得太大的闲聊",
                RawJson = "{\"title\":\"冬日酒吧聚会\",\"actors\":[\"Sam\",\"Sebastian\",\"Abigail\"],\"actions\":[]}",
                ActionCount = 8,
                CreatedAt = new DateTime(2026, 10, 3, 1, 0, 0, DateTimeKind.Utc)
            };

            string json = JsonConvert.SerializeObject(cutscene, Formatting.Indented);
            var deserialized = JsonConvert.DeserializeObject<ArchivedCutscene>(json);

            Assert.NotNull(deserialized);
            Assert.Equal("test_scene_123", deserialized.Id);
            Assert.Equal("冬日酒吧聚会", deserialized.Title);
            Assert.Equal("Saloon", deserialized.LocationName);
            Assert.Equal(3, deserialized.ActorNames.Count);
            Assert.Contains("Sam", deserialized.ActorNames);
            Assert.Contains("Sebastian", deserialized.ActorNames);
            Assert.Contains("Abigail", deserialized.ActorNames);
            Assert.Equal("关于外面雪下得太大的闲聊", deserialized.UserIntent);
            Assert.Equal(8, deserialized.ActionCount);
        }

        [Fact]
        public void ArchivedCutscene_DefaultValues_AreConsistent()
        {
            var cutscene = new ArchivedCutscene();

            Assert.False(string.IsNullOrWhiteSpace(cutscene.Id));
            Assert.NotNull(cutscene.ActorNames);
            Assert.Empty(cutscene.ActorNames);
            Assert.True((DateTime.UtcNow - cutscene.CreatedAt.ToUniversalTime()).TotalMinutes < 1);
            Assert.Equal(string.Empty, cutscene.Title);
            Assert.Equal(string.Empty, cutscene.RawJson);
        }

        [Fact]
        public void CutsceneStorageService_Save_NullOrEmptyRawJson_ReturnsFalse()
        {
            Assert.False(CutsceneStorageService.Save(null));

            var cutsceneEmpty = new ArchivedCutscene
            {
                Title = "Empty",
                RawJson = ""
            };
            Assert.False(CutsceneStorageService.Save(cutsceneEmpty));

            var cutsceneWhitespace = new ArchivedCutscene
            {
                Title = "Whitespace",
                RawJson = "   "
            };
            Assert.False(CutsceneStorageService.Save(cutsceneWhitespace));
        }

        [Fact]
        public void CutsceneStorageService_Replay_InvalidState_ReturnsFalseWithErrorMessage()
        {
            // When game world / context is not ready in test environment, Replay must safely fail with clear message
            var cutscene = new ArchivedCutscene
            {
                Title = "Test Replay",
                RawJson = "{\"title\":\"Test\"}"
            };

            bool success = CutsceneStorageService.Replay(cutscene, out string error);
            Assert.False(success);
            Assert.False(string.IsNullOrWhiteSpace(error));
        }

        [Fact]
        public void CutsceneSnapshot_ActorState_PreservesAnimationAndRouteFields()
        {
            var state = new CutsceneSnapshot.ActorState
            {
                OriginalTile = new Vector2(10, 20),
                OriginalFacing = 2,
                EndOfRouteBehaviorName = "alex_ball",
                DoingEndOfRouteAnimation = true,
                GoingToDoEndOfRouteAnimation = false,
                HadActiveController = true,
                SavedRoute = new List<Point> { new Point(10, 21), new Point(10, 22), new Point(10, 23) },
                ControllerEndPoint = new Point(10, 25),
                ControllerFacing = 0,
                ControllerNpcSchedule = true,
                FollowSchedule = true
            };

            Assert.Equal("alex_ball", state.EndOfRouteBehaviorName);
            Assert.True(state.DoingEndOfRouteAnimation);
            Assert.False(state.GoingToDoEndOfRouteAnimation);
            Assert.True(state.HadActiveController);
            Assert.Equal(3, state.SavedRoute.Count);
            Assert.Equal(new Point(10, 25), state.ControllerEndPoint);
            Assert.True(state.ControllerNpcSchedule);
            Assert.True(state.FollowSchedule);
        }

        [Fact]
        public void CutsceneSnapshot_RouteReconstruction_PreservesExactPopOrder()
        {
            // Simulate original controller stack: top is (10, 21), next is (10, 22), bottom is (10, 23)
            var originalStack = new Stack<Point>();
            originalStack.Push(new Point(10, 23));
            originalStack.Push(new Point(10, 22));
            originalStack.Push(new Point(10, 21));

            // In Capture, .ToList() serializes stack from top to bottom
            var capturedList = originalStack.ToList();
            Assert.Equal(new Point(10, 21), capturedList[0]);
            Assert.Equal(new Point(10, 22), capturedList[1]);
            Assert.Equal(new Point(10, 23), capturedList[2]);

            // In Restore, reversing the list before passing to Stack constructor reconstructs identical pop order
            var restoredStack = new Stack<Point>(capturedList.AsEnumerable().Reverse());
            Assert.Equal(new Point(10, 21), restoredStack.Pop());
            Assert.Equal(new Point(10, 22), restoredStack.Pop());
            Assert.Equal(new Point(10, 23), restoredStack.Pop());
            Assert.Empty(restoredStack);
        }

        [Fact]
        public void MoveToTileAction_Constructor_ThrowsOnNullNpc()
        {
            Assert.Throws<ArgumentNullException>(() => new ValleytalkReborn.Cutscene.Actions.MoveToTileAction(null, Vector2.Zero));
        }
    }
}
