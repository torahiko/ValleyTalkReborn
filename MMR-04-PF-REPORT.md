# MMR-04 PF-REPORT: FollowMovementTracker 提取测绘

## A — 跟随区域方法地图（迁移后现状）

| # | 方法 | 行范围 (Tracker) | 职责 |
|---|------|-----------------|------|
| 1 | Tick | 268–340 | 主循环：GoTo 占用判定 → 菜单/对话暂停 → EndTime/22:00 截止 → 跨地图 warp → 状态机分发 |
| 2 | TransitionTo | 498–547 | 状态机转换：清零上一状态 → 初始化下一状态（速度/计时器/日志） |
| 3 | TickHalted | 370–421 | Halted 状态：启动延迟 → 约会走近 → 普通走近/闲逛触发 |
| 4 | TickPathing | 423–485 | Pathing 状态：失败退避 → 刹车惯性 → 停止距离 → 卡死检测 → 重寻路 |
| 5 | TickWandering | 487–496 | Wandering 状态：距离/空闲退出 → 闲逛路径设置 |
| 6 | UpdateFollowSpeed | 358–368 | 两挡变速：>10 冲刺 / <4 普通 |
| 7 | GetSmartFollowTarget | 549–600 | 路径目标选择：身后优先 → 周围候选 → 兜底 |
| 8 | SetFollowPath | 602–627 | 路径创建：TryRecoverStartingTile → TryCreatePath → 失败退避 |
| 9 | TrySetWanderPath | 629–674 | 闲逛路径：70% 身后扇形 / 30% 随机 → 20 次尝试 |
| 10 | StartRegularFollow | 138–153 | 启动普通跟随：Unbind → 赋值 → TransitionTo(Halted) |
| 11 | StartDateFollow | 155–172 | 启动约会跟随：Unbind → 赋值 → TransitionTo(Halted) → OnFollowStartedCallback |

额外公开方法：
- Unbind (174–211): 原 StopFollowInternal，公开化，注入 onFollowStopped 回调
- SuspendForGoto (342–354): 构建快照 + ClearNpcMovement
- RestoreSuspendedFollow (487–500): 恢复快照 → TransitionTo(Halted)
- ResetAll (502–520): 清零全部字段 + clearNpcMovement，零回调零派发

## B — 状态迁移表

| 当前状态 | 触发条件 | 目标状态 |
|---------|---------|---------|
| Halted | dist > 4.5f + 延迟计满 | Pathing |
| Halted | 约会: idleTimer ≥ 180 + dist > 2.0f | Pathing |
| Halted | 普通: idleTimer ≥ 120 + dist > 3.5f | Pathing |
| Halted | 普通: idleTimer ≥ 360 + dist ≤ 2.0f | Wandering |
| Pathing | dist ≤ 2.5f (刹车) | Halted |
| Pathing | dist ≤ 2.0f (idle, 刹车) | Halted |
| Wandering | dist > 6.5f 或 idleTimer < 120 | Pathing |
| 任意 | EndTime/22:00/Unbind | Halted (via ResetAll) |

## C — 四个公共 Follow 方法签名

`csharp
// MovementManager 包装器（签名不变）
public void StartRegularFollow(NPC npc, int endTime);
public void StartDateFollow(NPC npc, int endTime);
public void StopFollow(NPC npc);
public void StopDateFollow(NPC npc);
`

## D — 字段分类表（14 项，全部迁入 Tracker）

| 字段 | 类型 | 默认值 | 类别 |
|------|------|--------|------|
| _followingNpc | NPC | null | 核心状态 |
| _followEndTime | int | 0 | 核心状态 |
| _isDateFollow | bool | false | 核心状态 |
| _followState | FollowState | Halted | 状态机 |
| _committedTarget | Vector2 | Zero | 路径稳定 |
| _retargetCooldown | int | 0 | 路径稳定 |
| _isSprinting | bool | false | 变速 |
| _startDelayTimer | int | 0 | 启动延迟 |
| _isBraking | bool | false | 刹车 |
| _brakeTimer | int | 0 | 刹车 |
| _followPathFailCount | int | 0 | 退避 |
| _followPathFailCooldown | int | 0 | 退避 |
| _idleGazeTimer | int | 0 | 空闲视觉 |
| _lastPlayerTile | Vector2 | Zero | 玩家静止 |
| _playerIdleTimer | int | 0 | 玩家静止 |
| _wanderPathCooldown | int | 0 | 闲逛 |
| _lastNpcTileInPathing | Vector2 | Zero | 卡死检测 |
| _npcStuckTicks | int | 0 | 卡死检测 |

## E — Follow↔Goto 事实

- Tracker **从未**调用 gotoTracker.Start——仅使用 gotoTracker.IsMoving(npc.Name) 作占用判定
- Tracker **从未**赋值 OnGotoEnded——该回调在 MovementManager.WireGotoTrackerCallbacks 中设置
- 占用判定语义：Tick 入口处 _gotoTracker.IsMoving(_followingNpc.Name) → return 让路
- SuspendForGoto：构建快照 + ClearNpcMovement，交由 GotoMovementTracker.Start 传入

## F — 跨地图（FindSafeFollowTile）

- 条件：_followingNpc.currentLocation != Game1.player.currentLocation
- 调用：MovementPathfinding.FindSafeFollowTile(targetLocation, Game1.player.Tile, _followingNpc)
- 成功：Game1.warpCharacter + ClearNpcMovement + TransitionTo(Halted)
- 失败：仅 Warn 日志，不强制落点（原样保持）

## G — 常量表（19 项）

| 名称 | 值 | 用途 |
|------|-----|------|
| RETARGET_COOLDOWN | 12 | 重寻路冷却帧 |
| SPRINT_DIST | 10f | 冲刺距离阈值 |
| NORMAL_DIST | 4f | 普通距离阈值 |
| SPEED_SPRINT | 3 | 冲刺速度 |
| SPEED_NORMAL | 2 | 普通速度 |
| DIST_START_PATH | 4.5f | 开始寻路距离 |
| DIST_STOP_PATH | 2.5f | 停止寻路距离 |
| DIST_ABORT_WANDER | 6.5f | 中止闲逛距离 |
| DIST_IDLE_APPROACH | 2.0f | 空闲走近距离 |
| DIST_PLAYER_STOPPED_APPROACH | 3.5f | 玩家停走近距离 |
| START_DELAY_FRAMES | 8 | 启动延迟帧数 |
| BRAKE_FRAMES | 4 | 刹车帧数 |
| BRAKE_SPEED | 1 | 刹车速度 |
| IDLE_GAZE_INTERVAL | 60 | 空闲视线间隔 |
| WANDER_HALF_SPREAD | 1.047f | 闲逛扇形半角 |
| IDLE_THRESHOLD | 360 | 玩家静止阈值 |
| WANDER_PAUSE | 180 | 闲逛暂停帧数 |
| WANDER_RADIUS_MIN | 2 | 闲逛最小半径 |
| WANDER_RADIUS_MAX | 5 | 闲逛最大半径 |
| STUCK_TICKS_THRESHOLD | 40 | 卡死检测阈值 |

## H — _followingNpc/_isDateFollow 消费点 → Tracker 查询映射

| 原 MovementManager 引用 | 迁移后 |
|------------------------|--------|
| _followingNpc != null | _followTracker.HasActiveFollow |
| _isDateFollow && _followingNpc != null | _followTracker.HasActiveDateFollow |
| _followingNpc == npc | _followTracker.CurrentFollowingNpc == npc |
| _followingNpc == npc && !_isDateFollow | _followTracker.IsFollowing(npc) |
| _isDateFollow (QueueMovementInternal) | _followTracker.HasActiveDateFollow |
| _followingNpc != npc (OnGotoEnded) | !_followTracker.IsFollowing(npc) |
| StopFollowInternal(silent) | _followTracker.Unbind(silent) |
| HandleFollow(e) | _followTracker.Tick(e) |
| RestoreSuspendedFollow(s) | _followTracker.RestoreSuspendedFollow(s) |
| ResetAllState 内字段清零 | _followTracker.ResetAll() |
