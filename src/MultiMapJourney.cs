using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace ValleytalkReborn
{
    internal class JourneySegment
    {
        public string MapName { get; set; }
        public Vector2 WalkToTile { get; set; }
        public string NextMapName { get; set; }
        public Vector2 NextMapLanding { get; set; }
    }

    internal class MultiMapJourney
    {
        public NPC Npc { get; set; }
        public Queue<JourneySegment> Segments { get; } = new();
        public Action OnComplete { get; set; }
        public Action OnFail { get; set; }
        public bool SegmentDispatched { get; set; } = false;
        public bool IsFinished { get; set; } = false;
    }

    public class MultiMapNavigator
    {
        private static readonly MultiMapNavigator _instance = new();

        public static MultiMapNavigator Instance
        {
            get
            {
                _instance.EnsureEventsSubscribed();
                return _instance;
            }
        }

        private Dictionary<string, List<WarpEdge>> _warpGraph = new(StringComparer.OrdinalIgnoreCase);
        private bool _graphBuilt = false;
        private bool _eventsSubscribed = false;

        // ★ 当前正在执行的导航任务，最多一个
        private MultiMapJourney _currentJourney;

        // ★ 等待队列
        private readonly Queue<MultiMapJourney> _waitingQueue = new();

        private MultiMapNavigator()
        {
        }

        private void EnsureEventsSubscribed()
        {
            if (_eventsSubscribed || ModEntry.SHelper == null)
                return;

            ModEntry.SHelper.Events.GameLoop.UpdateTicked += OnUpdateTicked;

            ModEntry.SHelper.Events.GameLoop.DayStarted += (_, _) => CancelAll();

            ModEntry.SHelper.Events.GameLoop.DayEnding += (_, _) => CancelAll();

            ModEntry.SHelper.Events.GameLoop.SaveLoaded += (_, _) =>
            {
                _graphBuilt = false;
                CancelAll();
            };

            ModEntry.SHelper.Events.Player.Warped += (_, _) => _graphBuilt = false;

            _eventsSubscribed = true;
        }

        public bool IsNavigating(NPC npc)
        {
            if (npc?.Name == null)
                return false;

            if (_currentJourney?.Npc?.Name == npc.Name && !_currentJourney.IsFinished)
                return true;

            return _waitingQueue.Any(j => !j.IsFinished && j.Npc?.Name == npc.Name);
        }

        public void NavigateTo(
            NPC npc,
            string targetMapName,
            Vector2 targetTile,
            Action onComplete = null,
            Action onFail = null)
        {
            if (npc == null)
                return;

            if (string.IsNullOrWhiteSpace(targetMapName))
            {
                ModEntry.SMonitor?.Log(
                    "[MultiMapNavigator] NavigateTo failed: targetMapName is null or empty.",
                    LogLevel.Warn);

                SafeInvoke(onFail, "onFail");
                return;
            }

            if (string.IsNullOrWhiteSpace(npc.Name))
            {
                ModEntry.SMonitor?.Log(
                    "[MultiMapNavigator] NavigateTo failed: NPC name is null or empty.",
                    LogLevel.Warn);

                SafeInvoke(onFail, "onFail");
                return;
            }

            // 如果这个 NPC 自己已经有当前任务或排队任务，先取消旧的
            Cancel(npc.Name);

            EnsureGraphBuilt();

            string fromMap = npc.currentLocation?.Name;

            if (string.IsNullOrWhiteSpace(fromMap))
            {
                ModEntry.SMonitor?.Log(
                    $"[MultiMapNavigator] {npc.Name} has no valid currentLocation. Falling back to 'FarmHouse'.",
                    LogLevel.Warn);

                fromMap = "FarmHouse";
            }

            var segments = PlanRoute(fromMap, targetMapName, targetTile);

            if (segments == null || segments.Count == 0)
            {
                ModEntry.SMonitor?.Log(
                    $"[MultiMapNavigator] No route from '{fromMap}' to '{targetMapName}' for {npc.Name}. Warping directly.",
                    LogLevel.Warn);

                WarpCharacterSafe(npc, targetMapName, targetTile);
                SafeInvoke(onComplete, "no-route fallback onComplete");
                return;
            }

            var journey = new MultiMapJourney
            {
                Npc = npc,
                OnComplete = onComplete,
                OnFail = onFail
            };

            foreach (var seg in segments)
                journey.Segments.Enqueue(seg);

            TryEnqueueOrStart(journey);
        }

        public void Cancel(string npcName)
        {
            if (string.IsNullOrWhiteSpace(npcName))
                return;

            bool cancelled = false;

            if (_currentJourney?.Npc?.Name == npcName)
            {
                _currentJourney.IsFinished = true;

                if (_currentJourney.Npc != null)
                    MovementManager.Instance.CancelMoveToTile(_currentJourney.Npc, invokeFailCallback: false);

                _currentJourney = null;
                cancelled = true;
            }

            if (_waitingQueue.Count > 0)
            {
                var keep = new List<MultiMapJourney>();

                while (_waitingQueue.Count > 0)
                {
                    var j = _waitingQueue.Dequeue();

                    if (j?.Npc?.Name == npcName)
                    {
                        j.IsFinished = true;

                        if (j.Npc != null)
                            MovementManager.Instance.CancelMoveToTile(j.Npc, invokeFailCallback: false);

                        cancelled = true;
                    }
                    else if (j != null && !j.IsFinished)
                    {
                        keep.Add(j);
                    }
                }

                foreach (var j in keep)
                    _waitingQueue.Enqueue(j);
            }

            if (cancelled)
                ModEntry.SMonitor?.Log($"[MultiMapNavigator] Journey cancelled for {npcName}.", LogLevel.Debug);
        }

        public void CancelAll()
        {
            if (_currentJourney != null)
            {
                _currentJourney.IsFinished = true;

                if (_currentJourney.Npc != null)
                    MovementManager.Instance.CancelMoveToTile(_currentJourney.Npc, invokeFailCallback: false);

                _currentJourney = null;
            }

            while (_waitingQueue.Count > 0)
            {
                var j = _waitingQueue.Dequeue();
                j.IsFinished = true;

                if (j.Npc != null)
                    MovementManager.Instance.CancelMoveToTile(j.Npc, invokeFailCallback: false);
            }
        }

        // ── Update 主循环 ──────────────────────────────────────────
        private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
        {
            if (!Context.IsWorldReady)
                return;

            if (_currentJourney == null || _currentJourney.IsFinished)
            {
                _currentJourney = null;
                TryStartNextWaiting();
            }

            if (_currentJourney != null && !_currentJourney.IsFinished)
                TickJourney(_currentJourney);
        }

        private void TryStartNextWaiting()
        {
            var mm = MovementManager.Instance;

            if (_currentJourney != null && !_currentJourney.IsFinished)
                return;

            // ★ 只要有跟随、GoTo、Step 正在发生，就等待
            if (mm.HasActiveFollow || mm.IsMoving || mm.IsStepActive)
                return;

            while (_waitingQueue.Count > 0)
            {
                var next = _waitingQueue.Dequeue();

                if (next == null || next.IsFinished)
                    continue;

                if (next.Npc == null || string.IsNullOrWhiteSpace(next.Npc.Name))
                    continue;

                // ★ 如果这个 NPC 又开始跟随了，跳过它的旧导航
                if (mm.CurrentFollowingNpc == next.Npc || mm.CurrentGotoNpc == next.Npc)
                {
                    next.IsFinished = true;
                    continue;
                }

                if (next.Npc.currentLocation == null)
                {
                    next.IsFinished = true;
                    SafeInvoke(next.OnFail, "queued NPC has no currentLocation");
                    continue;
                }

                StartJourney(next);
                return;
            }
        }

        private void TryEnqueueOrStart(MultiMapJourney journey)
        {
            if (journey == null || journey.IsFinished || journey.Npc == null)
                return;

            var mm = MovementManager.Instance;

            bool busy =
                (_currentJourney != null && !_currentJourney.IsFinished) ||
                _waitingQueue.Count > 0;

            // ★ 只要有跟随、GoTo、Step，就排队
            if (!busy && (mm.HasActiveFollow || mm.IsMoving || mm.IsStepActive))
                busy = true;

            if (busy)
            {
                _waitingQueue.Enqueue(journey);

                ModEntry.SMonitor?.Log(
                    $"[MultiMapNavigator] {journey.Npc.Name} queued. Queue size: {_waitingQueue.Count}.",
                    LogLevel.Debug);
            }
            else
            {
                StartJourney(journey);
            }
        }

        private void StartJourney(MultiMapJourney journey)
        {
            if (journey == null || journey.IsFinished)
                return;

            var npc = journey.Npc;

            if (npc == null || string.IsNullOrWhiteSpace(npc.Name))
            {
                journey.IsFinished = true;
                SafeInvoke(journey.OnFail, "StartJourney invalid NPC");
                return;
            }

            _currentJourney = journey;

            ModEntry.SMonitor?.Log(
                $"[MultiMapNavigator] Journey started for {npc.Name}: " +
                string.Join(" → ", journey.Segments.Select(s => s.MapName)),
                LogLevel.Info);

            DispatchCurrentSegment(journey);
        }

        private void TickJourney(MultiMapJourney journey)
        {
            if (journey == null || journey.IsFinished)
                return;

            var npc = journey.Npc;

            if (npc == null || string.IsNullOrWhiteSpace(npc.Name))
            {
                FailJourney(journey);
                return;
            }

            if (_currentJourney != journey)
            {
                journey.IsFinished = true;
                return;
            }

            var mm = MovementManager.Instance;

            // ★ 如果当前导航的 NPC 突然开始跟随玩家，导航让位
            if (mm.CurrentFollowingNpc == npc)
            {
                ModEntry.SMonitor?.Log(
                    $"[MultiMapNavigator] {npc.Name} started following player. Cancelling navigation.",
                    LogLevel.Debug);

                mm.CancelMoveToTile(npc, invokeFailCallback: false);
                FailJourney(journey);
                return;
            }

            if (journey.Segments.Count == 0)
            {
                FinishJourney(journey);
                return;
            }

            var seg = journey.Segments.Peek();

            // NPC 被外力移走
            if (!string.Equals(npc.currentLocation?.Name, seg.MapName, StringComparison.OrdinalIgnoreCase))
            {
                ModEntry.SMonitor?.Log(
                    $"[MultiMapNavigator] {npc.Name} unexpectedly at '{npc.currentLocation?.Name}' " +
                    $"instead of '{seg.MapName}'. Re-evaluating.",
                    LogLevel.Debug);

                var finalSeg = journey.Segments.Last();

                if (finalSeg.NextMapName == null &&
                    string.Equals(npc.currentLocation?.Name, finalSeg.MapName, StringComparison.OrdinalIgnoreCase))
                {
                    while (journey.Segments.Count > 1)
                        journey.Segments.Dequeue();

                    journey.SegmentDispatched = false;
                    DispatchCurrentSegment(journey);
                }
                else
                {
                    FailJourney(journey);
                }

                return;
            }

            // 如果上一段结束后没有成功派发下一段，这里重试
            if (!journey.SegmentDispatched)
            {
                DispatchCurrentSegment(journey);
                return;
            }

            // ★ 防卡死：可见段已派发但 MovementManager 不再移动该 NPC
            if (journey.SegmentDispatched &&
                IsVisibleMap(seg.MapName) &&
                !mm.IsNpcMoving(npc))
            {
                ModEntry.SMonitor?.Log(
                    $"[MultiMapNavigator] {npc.Name} visible segment seems externally cancelled. Treating as failed segment.",
                    LogLevel.Debug);

                OnSegmentArrived(journey, seg, success: false);
                return;
            }
        }

        // ── 段派发 ────────────────────────────────────────────────
        private void DispatchCurrentSegment(MultiMapJourney journey)
        {
            if (journey == null || journey.IsFinished || journey.Segments.Count == 0)
                return;

            var npc = journey.Npc;

            if (npc == null || string.IsNullOrWhiteSpace(npc.Name))
            {
                FailJourney(journey);
                return;
            }

            if (_currentJourney != journey)
                return;

            var mm = MovementManager.Instance;

            // ★ 如果 MovementManager 正忙，不派发，等下一帧 Tick 重试
            if (mm.HasActiveFollow || mm.IsMoving || mm.IsStepActive)
            {
                journey.SegmentDispatched = false;
                return;
            }

            var seg = journey.Segments.Peek();
            journey.SegmentDispatched = true;

            ModEntry.SMonitor?.Log(
                $"[MultiMapNavigator] {npc.Name} walking to ({seg.WalkToTile.X},{seg.WalkToTile.Y}) on '{seg.MapName}'.",
                LogLevel.Debug);

            if (IsVisibleMap(seg.MapName))
            {
                MovementManager.Instance.MoveToTile(
                    npc,
                    seg.WalkToTile,
                    onComplete: () => OnSegmentArrived(journey, seg, success: true),
                    onFail: () => OnSegmentArrived(journey, seg, success: false));
            }
            else
            {
                OnSegmentArrived(journey, seg, success: true);
            }
        }

        private void OnSegmentArrived(
            MultiMapJourney journey,
            JourneySegment completedSeg,
            bool success)
        {
            if (journey == null || completedSeg == null || journey.IsFinished)
                return;

            if (_currentJourney != journey)
                return;

            var npc = journey.Npc;

            if (npc == null || string.IsNullOrWhiteSpace(npc.Name))
            {
                FailJourney(journey);
                return;
            }

            if (journey.Segments.Count > 0 && journey.Segments.Peek() == completedSeg)
                journey.Segments.Dequeue();

            journey.SegmentDispatched = false;

            // 最后一段
            if (completedSeg.NextMapName == null)
            {
                bool shouldWarpToFinalTile = !success || !IsVisibleMap(completedSeg.MapName);

                if (shouldWarpToFinalTile)
                {
                    ModEntry.SMonitor?.Log(
                        $"[MultiMapNavigator] {npc.Name} final segment needs warp. " +
                        $"Map: '{completedSeg.MapName}', Tile: ({completedSeg.WalkToTile.X},{completedSeg.WalkToTile.Y}).",
                        LogLevel.Debug);

                    WarpCharacterSafe(npc, completedSeg.MapName, completedSeg.WalkToTile);
                }

                FinishJourney(journey);
                return;
            }

            // 中间段失败：跳过走路，直接 warp
            if (!success)
            {
                ModEntry.SMonitor?.Log(
                    $"[MultiMapNavigator] {npc.Name} segment pathfind failed on '{completedSeg.MapName}', " +
                    $"warping directly to '{completedSeg.NextMapName}'.",
                    LogLevel.Warn);
            }

            WarpCharacterSafe(npc, completedSeg.NextMapName, completedSeg.NextMapLanding);

            if (journey.Segments.Count > 0)
                DispatchCurrentSegment(journey);
            else
                FinishJourney(journey);
        }

        // ── 路由规划 BFS ──────────────────────────────────────────
        private List<JourneySegment> PlanRoute(string fromMap, string toMap, Vector2 finalTile)
        {
            if (string.IsNullOrWhiteSpace(fromMap) || string.IsNullOrWhiteSpace(toMap))
                return null;

            // ★ 同地图也返回最终段，统一进入队列
            if (string.Equals(fromMap, toMap, StringComparison.OrdinalIgnoreCase))
            {
                return new List<JourneySegment>
                {
                    new JourneySegment
                    {
                        MapName = toMap,
                        WalkToTile = finalTile,
                        NextMapName = null
                    }
                };
            }

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { fromMap };
            var queue = new Queue<(string map, List<JourneySegment> path)>();
            queue.Enqueue((fromMap, new List<JourneySegment>()));

            while (queue.Count > 0)
            {
                var (currentMap, path) = queue.Dequeue();

                if (!_warpGraph.TryGetValue(currentMap, out var edges) || edges == null)
                    continue;

                foreach (var edge in edges)
                {
                    if (edge == null || string.IsNullOrWhiteSpace(edge.TargetMapName))
                        continue;

                    if (visited.Contains(edge.TargetMapName))
                        continue;

                    var newSeg = new JourneySegment
                    {
                        MapName = currentMap,
                        WalkToTile = edge.ExitTile,
                        NextMapName = edge.TargetMapName,
                        NextMapLanding = edge.LandingTile
                    };

                    var newPath = new List<JourneySegment>(path) { newSeg };

                    if (string.Equals(edge.TargetMapName, toMap, StringComparison.OrdinalIgnoreCase))
                    {
                        newPath.Add(new JourneySegment
                        {
                            MapName = edge.TargetMapName,
                            WalkToTile = finalTile,
                            NextMapName = null
                        });

                        return newPath;
                    }

                    visited.Add(edge.TargetMapName);
                    queue.Enqueue((edge.TargetMapName, newPath));
                }
            }

            return null;
        }

        // ── Warp 图构建 ────────────────────────────────────────────
        private void EnsureGraphBuilt()
        {
            if (_graphBuilt)
                return;

            if (!Context.IsWorldReady || Game1.locations == null)
                return;

            BuildWarpGraph();
            _graphBuilt = true;
        }

        private void BuildWarpGraph()
        {
            _warpGraph = new Dictionary<string, List<WarpEdge>>(StringComparer.OrdinalIgnoreCase);

            if (Game1.locations == null)
                return;

            foreach (var loc in Game1.locations)
            {
                if (loc?.warps == null || string.IsNullOrWhiteSpace(loc.Name))
                    continue;

                var edges = new List<WarpEdge>();

                foreach (var warp in loc.warps)
                {
                    if (warp == null || string.IsNullOrWhiteSpace(warp.TargetName))
                        continue;

                    edges.Add(new WarpEdge
                    {
                        ExitTile = new Vector2(warp.X, warp.Y),
                        TargetMapName = warp.TargetName,
                        LandingTile = new Vector2(warp.TargetX, warp.TargetY)
                    });
                }

                if (edges.Count == 0)
                    continue;

                if (!_warpGraph.TryGetValue(loc.Name, out var existing))
                    _warpGraph[loc.Name] = edges;
                else
                    existing.AddRange(edges);
            }

            ModEntry.SMonitor?.Log(
                $"[MultiMapNavigator] Warp graph built: {_warpGraph.Count} maps, " +
                $"{_warpGraph.Values.Sum(v => v.Count)} edges.",
                LogLevel.Info);
        }

        /// <summary>
        /// ★ 改为“玩家当前所在地图才可寻路”。
        /// 玩家不在该地图时，MultiMapNavigator 会直接走 warp。
        /// </summary>
        private static bool IsVisibleMap(string mapName)
        {
            if (string.IsNullOrWhiteSpace(mapName))
                return false;

            string currentMap = Game1.currentLocation?.Name;

            return string.Equals(currentMap, mapName, StringComparison.OrdinalIgnoreCase);
        }

        // ── 完成 / 失败 / 辅助 ───────────────────────────────────
        private void FinishJourney(MultiMapJourney journey)
        {
            if (journey == null || journey.IsFinished)
                return;

            journey.IsFinished = true;

            if (_currentJourney == journey)
                _currentJourney = null;

            SafeInvoke(journey.OnComplete, "journey.OnComplete");
            TryStartNextWaiting();
        }

        private void FailJourney(MultiMapJourney journey)
        {
            if (journey == null || journey.IsFinished)
                return;

            journey.IsFinished = true;

            if (_currentJourney == journey)
                _currentJourney = null;

            SafeInvoke(journey.OnFail, "journey.OnFail");
            TryStartNextWaiting();
        }

        private static void SafeInvoke(Action action, string context)
        {
            if (action == null)
                return;

            try
            {
                action.Invoke();
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log(
                    $"[MultiMapNavigator] Callback exception in {context}: {ex}",
                    LogLevel.Error);
            }
        }

        private static void WarpCharacterSafe(NPC npc, string mapName, Vector2 tile)
        {
            if (npc == null || string.IsNullOrWhiteSpace(mapName))
                return;

            try
            {
                var point = new Point((int)Math.Round(tile.X), (int)Math.Round(tile.Y));
                Game1.warpCharacter(npc, mapName, point);
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log(
                    $"[MultiMapNavigator] Warp failed for {npc.Name} to '{mapName}' at ({tile.X},{tile.Y}): {ex}",
                    LogLevel.Error);
            }
        }

        /// <summary>
        /// 跳过导航队列，直接将 NPC warp 到目标地图和 tile。
        /// 供跟随模式下的非跟随配偶使用。
        /// </summary>
        public static void WarpDirectTo(NPC npc, string mapName, Vector2 tile)
        {
            WarpCharacterSafe(npc, mapName, tile);
        }
    }

    internal class WarpEdge
    {
        public Vector2 ExitTile { get; set; }
        public string TargetMapName { get; set; }
        public Vector2 LandingTile { get; set; }
    }
}