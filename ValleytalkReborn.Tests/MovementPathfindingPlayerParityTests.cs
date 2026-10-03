// MovementPathfindingPlayerParityTests.cs
// ═══════════════════════════════════════════════════════════════════════════
// NPC 跟随的玩家规则对齐（水面 + 拆障）——纯逻辑锁定
//
// 背景：1.6 反编译确认 GameLocation.isTilePassable 与 isCollidingPosition 均
// 不把 Back 层 "Water" 属性当作移动碰撞（仅 rafting / 火山弹道特例），
// 且 Character 默认 willDestroyObjectsUnderfoot=true（NPC 被栅栏挡路时当场拆障
// 穿行）。因此 vanilla 寻路与物理允许 NPC 过河、穿栅栏，而玩家两者都不行。
//
// 本文件覆盖新抽出的两段纯逻辑（IsTileWalkable 的集成测试受限于无头环境
// 无法构造已装载地图，见 MovementWorldEvaluationTests 的 map-test limitation）：
//
// 1) MovementPathfinding.IsOpenWater
//    —— 开阔水面 = Back 层 Water 且上方无 Buildings 层 tile（桥面/码头）。
//       桥板（Buildings 层覆盖）必须放行，否则 Town 过河唯一通路被封死。
//
// 2) MovementPathfinding.RouteRespectsPlayerPassability
//    —— vanilla A* 路线内点校验：起点/终点豁免（vanilla findPath 终格无条件可达
//       语义），内点逐格过玩家通行规则；并锁定 Stack 枚举序 = Pop 序（栈顶=起点）
//       这一 TryCreatePath 调用所依赖的前提。
//
// ═══════════════════════════════════════════════════════════════════════════

using Microsoft.Xna.Framework;
using ValleytalkReborn;
using Xunit;

namespace ValleytalkReborn.Tests;

public class MovementPathfindingPlayerParityTests
{
    // ── 1. 开阔水面判定 ────────────────────────────────────────────────

    [Theory]
    // 开阔水面：Back 层 Water 且无 Buildings 覆盖 → 不可走（对齐玩家：玩家走不进河里）
    [InlineData(true,  false, true)]
    // 桥面/码头板：水面上有 Buildings 层 tile → 不由水面规则封锁，交由 Buildings 层判定
    [InlineData(true,  true,  false)]
    // 普通陆地：无 Water 属性 → 与 Buildings 覆盖无关
    [InlineData(false, false, false)]
    [InlineData(false, true,  false)]
    public void IsOpenWater_MatchesPlayerIntuition(
        bool isWaterBackTile,
        bool hasBuildingsLayerTile,
        bool expectedOpenWater)
    {
        Assert.Equal(
            expectedOpenWater,
            MovementPathfinding.IsOpenWater(isWaterBackTile, hasBuildingsLayerTile));
    }

    // ── 2. 路线内点校验 ────────────────────────────────────────────────

    [Fact]
    public void RouteValidation_NullOrEmptyRoute_Fails()
    {
        Func<Vector2, bool> alwaysWalkable = _ => true;

        Assert.False(MovementPathfinding.RouteRespectsPlayerPassability(null, alwaysWalkable));
        Assert.False(MovementPathfinding.RouteRespectsPlayerPassability(
            new List<Point>(), alwaysWalkable));
    }

    [Fact]
    public void RouteValidation_SingleTileRoute_Passes()
    {
        // start == end（NPC 已在目标格）：唯一元素同时是起点与终点，必须豁免通过。
        var route = new List<Point> { new Point(5, 7) };

        Assert.True(MovementPathfinding.RouteRespectsPlayerPassability(
            route, _ => false));
    }

    [Fact]
    public void RouteValidation_AllInteriorWalkable_Passes()
    {
        var route = new List<Point>
        {
            new Point(0, 0),  // 起点（豁免）
            new Point(0, 1),
            new Point(0, 2),
            new Point(0, 3),  // 终点（豁免）
        };

        Assert.True(MovementPathfinding.RouteRespectsPlayerPassability(
            route, _ => true));
    }

    [Fact]
    public void RouteValidation_InteriorViolation_Rejects()
    {
        var route = new List<Point>
        {
            new Point(0, 0),  // 起点（豁免）
            new Point(0, 1),  // 内点违规（如水面）
            new Point(0, 2),
            new Point(0, 3),  // 终点（豁免）
        };

        // 起点与终点违规都必须被豁免；仅内点违规触发拒绝。
        Assert.False(MovementPathfinding.RouteRespectsPlayerPassability(
            route, t => t != new Vector2(0, 1)));
    }

    [Fact]
    public void RouteValidation_StartAndEndViolations_Exempted()
    {
        var route = new List<Point>
        {
            new Point(0, 0),  // 起点：NPC 当前所在格，允许位于水面（卡河自救路径的前提）
            new Point(0, 1),
            new Point(0, 2),  // 终点：无条件可达（vanilla findPath 邻接判定先例）
        };

        // 只有 (0,1) 视为可走；起点与终点均违规但必须豁免。
        Assert.True(MovementPathfinding.RouteRespectsPlayerPassability(
            route, t => t == new Vector2(0, 1)));
    }

    // ── 3. Stack 枚举序前提锁定 ────────────────────────────────────────

    // TryCreatePath 依赖 vanilla.pathToEndPoint?.ToList() 的顺序 = Pop 序（栈顶=起点，
    // 与 PathFindController.reconstructPath 的压栈方向一致：终点先压、起点后压=栈顶）。
    // 若该前提被破坏，内点校验会把起点/终点豁免用错位置。
    // 4 格路线：内点仅 m1/m2 产生谓词调用；枚举序正确时 visited 应为 [m1, m2]，
    // 若枚举序颠倒（栈底优先）则会变成 [m2, m1]。
    [Fact]
    public void RouteValidation_StackToList_EnumeratesStartFirst()
    {
        var start  = new Point(1, 1);
        var m1     = new Point(2, 1);
        var m2     = new Point(3, 1);
        var end    = new Point(4, 1);

        // 模拟 reconstructPath 压栈序：终点最先压（栈底），起点最后压（栈顶）。
        var stack = new Stack<Point>();
        stack.Push(end);
        stack.Push(m2);
        stack.Push(m1);
        stack.Push(start);

        var visited = new List<Point>();
        MovementPathfinding.RouteRespectsPlayerPassability(
            stack.ToList(),
            t =>
            {
                visited.Add(new Point((int)t.X, (int)t.Y));
                return true;
            });

        // 仅内点产生调用：起点（豁免位）不产生谓词调用。
        Assert.Equal(new[] { m1, m2 }, visited);
    }
}
