// MovementWorldEvaluationTests.cs
// ═══════════════════════════════════════════════════════════════════════════
// CTX-004 REV B — 移动意图（文本）与世界求值（地图）分离后的纯逻辑锁定
// ═══════════════════════════════════════════════════════════════════════════
//
// 本文件只覆盖不需要游戏世界的纯逻辑：
//
// 1) MovementPathfinding.ResolveStepDelta 字面量表
//    —— 锁定 CTX-004 从 MovementCoordinator 抽取 delta 表是等价迁移：
//       绝对方向 4 例（Left/Right/Up/Down，且不随朝向变化）、
//       Forward×4 朝向、Backward×4 朝向取反，共 12 个必需例 + 4 个朝向无关佐证例。
//
// 2) ContextRouter.ResolveDirectionalEvaluation 决策矩阵
//    —— 邻接优先于阻挡；阻挡 = 执行真相（pos1 不可走 ⇒ MovementCoordinator
//       只 faceGeneralDirection，见 MovementCoordinator.cs 滑行前 else 分支）；
//       pos1 可走 ⇒ 畅通且 BlockDirection 保持 None。
//
// 为什么不需要 xTile map：决策核心不读 Game1 / NPC / 地图，只消费调用方给出的
// 三个布尔输入；IsTileWalkable 的集成测试受限于无头环境无法构造已装载地图，
// 该限制记录在 ContextRouterBaselineTests 与本次交付的 map-test limitation 中。
//
// ═══════════════════════════════════════════════════════════════════════════

using ValleytalkReborn;
using Xunit;

namespace ValleytalkReborn.Tests;

public class MovementWorldEvaluationTests
{
    // ── 1. 步进 delta 字面量表（迁移等价性）──────────────────────────────

    [Theory]
    // 绝对方向：与 NPC 朝向无关
    [InlineData(MovementType.Left,  0, -1,  0)]
    [InlineData(MovementType.Left,  2, -1,  0)]
    [InlineData(MovementType.Right, 0,  1,  0)]
    [InlineData(MovementType.Right, 2,  1,  0)]
    [InlineData(MovementType.Up,    0,  0, -1)]
    [InlineData(MovementType.Up,    2,  0, -1)]
    [InlineData(MovementType.Down,  0,  0,  1)]
    [InlineData(MovementType.Down,  2,  0,  1)]
    // Forward：0=上 1=右 2=下 3=左
    [InlineData(MovementType.Forward, 0,  0, -1)]
    [InlineData(MovementType.Forward, 1,  1,  0)]
    [InlineData(MovementType.Forward, 2,  0,  1)]
    [InlineData(MovementType.Forward, 3, -1,  0)]
    // Backward：朝向取反
    [InlineData(MovementType.Backward, 0,  0,  1)]
    [InlineData(MovementType.Backward, 1, -1,  0)]
    [InlineData(MovementType.Backward, 2,  0, -1)]
    [InlineData(MovementType.Backward, 3,  1,  0)]
    public void ResolveStepDelta_ReturnsExecutorDelta(
        MovementType type,
        int facingDirection,
        int expectedDx,
        int expectedDy)
    {
        Assert.Equal(
            (expectedDx, expectedDy),
            MovementPathfinding.ResolveStepDelta(type, facingDirection));
    }

    // ── 2. 决策矩阵 ────────────────────────────────────────────────────

    // 邻接优先：即使两侧均不可走，也不得报"阻挡"。
    [Fact]
    public void ResolveDirectionalEvaluation_AdjacentTarget_OverridesBlocked()
    {
        MovementEvaluation result = ContextRouter.ResolveDirectionalEvaluation(
            isAdjacentTarget: true,
            pos1Walkable: false,
            pos2Walkable: false,
            requestedDirection: BlockDirection.Forward);

        Assert.True(result.IsEvaluated);
        Assert.False(result.IsBlocked);
        Assert.Equal(BlockDirection.None, result.BlockDirection);
        Assert.True(result.IsAlreadyAdjacent);
    }

    // 执行真相：pos1 与 pos2 均不可走 ⇒ 阻挡并带上请求方向。
    [Fact]
    public void ResolveDirectionalEvaluation_BothTilesBlocked_ReportsRequestedDirection()
    {
        MovementEvaluation result = ContextRouter.ResolveDirectionalEvaluation(
            isAdjacentTarget: false,
            pos1Walkable: false,
            pos2Walkable: false,
            requestedDirection: BlockDirection.Left);

        Assert.True(result.IsEvaluated);
        Assert.True(result.IsBlocked);
        Assert.Equal(BlockDirection.Left, result.BlockDirection);
        Assert.False(result.IsAlreadyAdjacent);
    }

    // 执行真相：pos1 不可走、pos2 可走 ⇒ 执行端仅转向，仍属阻挡。
    // （这是 REV B 明文公式 !pos1 && !pos2 与 MovementCoordinator 实际分支的差异点，
    //   按 Architect 裁定以代码为准。）
    [Fact]
    public void ResolveDirectionalEvaluation_Pos1BlockedPos2Open_StillBlocked()
    {
        MovementEvaluation result = ContextRouter.ResolveDirectionalEvaluation(
            isAdjacentTarget: false,
            pos1Walkable: false,
            pos2Walkable: true,
            requestedDirection: BlockDirection.Up);

        Assert.True(result.IsEvaluated);
        Assert.True(result.IsBlocked);
        Assert.Equal(BlockDirection.Up, result.BlockDirection);
        Assert.False(result.IsAlreadyAdjacent);
    }

    // pos1 可走 ⇒ 畅通，无论 pos2 是否可走（pos2 只决定执行端滑一格还是两格）。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ResolveDirectionalEvaluation_Pos1Walkable_PathClear(bool pos2Walkable)
    {
        MovementEvaluation result = ContextRouter.ResolveDirectionalEvaluation(
            isAdjacentTarget: false,
            pos1Walkable: true,
            pos2Walkable: pos2Walkable,
            requestedDirection: BlockDirection.Right);

        Assert.True(result.IsEvaluated);
        Assert.False(result.IsBlocked);
        Assert.Equal(BlockDirection.None, result.BlockDirection);
        Assert.False(result.IsAlreadyAdjacent);
    }

    // ── 3. map-test limitation（如实上报，不得伪造地图）───────────────────

    private const string LoadedMapSkipReason =
        "Requires a loaded Stardew Valley map. MovementPathfinding.IsTileWalkable reads " +
        "loc.map.Layers[0].LayerWidth/Height, Game1.viewport and loc.isCollidingPosition; " +
        "the harness has no GameRunner viewport, and npc.currentLocation cannot be " +
        "initialized headless (Character.currentLocationRef / NetLocationRef requires live " +
        "net state — CTX-001 evidence). Reported as CTX-004's map-test limitation instead " +
        "of being faked with a stub map.";

    [Fact(Skip = LoadedMapSkipReason)]
    public void EvaluateDirectionalMovement_LoadedMap_DerivesFlagsFromWalkability()
    {
        // 保留给后续票（有官方 map fixture 时启用）的预期契约：
        //   NPC 置于已装载 GameLocation；
        //   请求方向的下一格 == Game1.player.Tile      ⇒ Adjacent=true, Blocked=false, Direction=None；
        //   下一格不可走（不论两格外是否可走）         ⇒ Blocked=true,  Direction=请求方向；
        //   下一格可走                                 ⇒ Blocked=false, Direction=None。
        //   求值只经由 MovementPathfinding.IsTileWalkable，路由自身不执行任何移动。
        Assert.True(false, "requires a loaded-map fixture; see Skip reason.");
    }
}
