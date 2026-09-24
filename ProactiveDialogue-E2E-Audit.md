# 主动对话系统 v1.1 端到端审计报告

**审计日期**：2026-09-25  
**审计范围**：STEP1–5 全链（提交链 cd9d0595 → b3ce16de → 772a42f2 → e979db29 → b3ce16de）  
**审计方法**：只读静态审计（grep/read 取证）+ 人工实测清单  
**唯一代码改动**：AmbientBarkModule.cs FetchBarksAsync Debug 日志扩展 1 行（Config.Debug 门控）

---

## 第一部分：全链静态审计

### L1 Scan（感知扫描）

**调用点证据**：`src/Dialogue/Ambark/Bark/BarkPromptBuilder.cs:39` — `PlayerStateScanner.Scan()`  
**数据流证据**：Scan 将 PlayerStateScanner 的 8 个 key 写入 PerceptionManager 的 _playerStateBucket / _activityBucket / _globalGossip，供后续 SensoryClassifier.Evaluate 通过 GetFilteredBucketFor 读取。

**结论**：CONFIRMED

---

### L2 决策（Resolve 前置）

**调用点证据**：`src/Dialogue/Ambark/Bark/BarkPromptBuilder.cs:47` — `ProactiveDialogueManager.Resolve(npc.Name)` 在 `L54` — `BarkFocusRouter.Decide(npc, barkState, bio, isZh)` 之前执行。  
**数据流证据**：Resolve 返回 ProactiveDialogueDecision；Mode == MicroSocial 时进入 TryBuildMicroSocial 分支（L50），否则落入既有 Soliloquy 流程。

**结论**：CONFIRMED（N3 裁定落地：Resolve 前置于 FocusRouter）

---

### L3 闸门（决策网）

**调用点证据**：`src/Dialogue/Ambark/Bark/ProactiveDialogueManager.cs:62-131` — Resolve 内闸门次序：  
- L70：EnabledProvider() → "disabled"  
- L80：gate0-recent-main-dialogue（120 分钟窗口）  
- L85-91：SensoryClassifier.Evaluate → SensoryCooldownStore.IsLocked → daily-cap  
- L104：PlayerMovingProvider → gate2-player-moving  
- L109-131：HeartsProvider → 7+ 恒触发 / 3-6 概率 / 0-2 拒绝 + daily-cap  

**数据流证据**：SensoryCooldownStore.IsLocked/TryClaim 在 Resolve 与 Commit 中分离调用（U3 契约）；TryClaim 在 Commit 中免 TryClaim（关系路径）或上锁（感官路径）。

**结论**：CONFIRMED

---

### L4 构建（MicroSocial 请求）

**调用点证据**：`src/Dialogue/Ambark/Bark/BarkPromptBuilder.cs:794-853` — TryBuildMicroSocial：  
- L804：rawPersona 解析（BuildFull + LocalizeNamesInText）  
- L812：FormatPerceptionForBark 净化 sensoryLine  
- L836-838：BuildMicroSocialUserPrompt 组装三段式 prompt  
- L840：ConsumePerceptions（感知消费，对齐 Soliloquy 阅后即焚）  
- L843-844：ProactiveDialogueManager.Commit（TryClaim + cap 登记）  
- L849：barkState.LastFocusType = BarkFocusType.Interactive + LastSensoryKey = null  

**数据流证据**：三态分流（感官/关系/非法）；关系路径跳过 FormatPerceptionForBark + ConsumePerceptions，sensoryLine=null；Commit 对关系路径免 TryClaim。

**结论**：CONFIRMED

---

### L5 回传（Mode 传递）

**调用点证据**：`src/Dialogue/Ambark/Bark/AmbientBarkModule.cs:1087` — FetchBarksAsync 成功回传点 `Mode = request.Mode`  
**数据流证据**：`src/Dialogue/Ambark/Bark/AmbientBarkModule.cs:1122` — EnqueueBarkFallback 强制 `Mode = BarkOutputMode.Soliloquy`（U4 降级：解析失败/网络失败 → 单条通用 fallback，强制按 Soliloquy 队列路径播放）

**结论**：CONFIRMED

---

### L6 直出（ProcessPendingResults 分支）

**调用点证据**：`src/Dialogue/Ambark/Bark/AmbientBarkModule.cs:222-237` — microDirect 判定 + 直出分支六件套：  
- L227：state.BarkQueue.Clear()（决策 3：清空 + 覆盖重排）  
- L228：state.IsRequesting = false  
- L229：state.HasPlayedFirst = true（N6 状态回填）  
- L230：state.AddRecentBark(microLines[0])（N6 思绪记忆链）  
- L231：state.BusyTicksRemaining = DISPLAY_LINE_VISIBLE_TICKS（N6 节日占用计时口径）  
- L232：state.DisplayCountdown = NextDisplayInterval  
- L233：_outputQueue.Enqueue(..., isMicroSocialBark: true)（line[0] 桥标记）  
- L234-235：state.BarkQueue.Enqueue(microLines[i])（[1..] 入队）  

**数据流证据**：microDirect 排除 IsFestivalNow（L223）；NPC 查名失败 / 全空白行 → 常规路径（L238）。

**结论**：CONFIRMED

---

### L7 桥（记录 → 消费 → 守卫）

**调用点证据**：  
- 记录：`src/Dialogue/Ambient/MainThreadOutputQueue.cs:210` — `FreshBarkBridgeStore.Record(npc.Name, text)`（IsMicroSocialBark 守卫，显示成功后）  
- 消费：`src/LLM/Prompts/Generation/LlmDialogueService.cs:145` — `FreshBarkBridgeStore.BuildBridgeBlock(character.Name)`  
- 标志：`src/LLM/Prompts/Generation/LlmDialogueService.cs:149` — `prompts.PendingEchoIsBridge = true`  
- 守卫：`src/LLM/Prompts/Generation/LlmDialogueService.cs:494-495` — `if (!string.IsNullOrEmpty(prompts.PendingEchoBlock) && !prompts.PendingEchoIsBridge)` → ConsumeEcho  

**数据流证据**：桥为 consume-on-read（TryConsume 内 TryRemove）；桥命中轮抑制 Echo 本轮注入（台账 #11）；Echo 条目保留 90s TTL 供下次交互。

**结论**：CONFIRMED

---

### L8 生命周期（清理）

**调用点证据**：`src/Dialogue/Ambark/Bark/AmbientBarkModule.cs:881-883` — CleanupAll 三清理：  
- L881：SensoryCooldownStore.ClearAll()  
- L882：ProactiveDialogueManager.ClearAll()  
- L883：FreshBarkBridgeStore.ClearAll()  

**数据流证据**：CleanupAll 由 OnDayStarted / OnReturnedToTitle / TickStates（功能关闭时）调用；FreshBarkBridgeStore 另有 3 秒现实时间 TTL + 跨日自然过期。

**结论**：CONFIRMED

---

### L9 降级矩阵

| 场景 | 代码落点 | 行为 |
|------|----------|------|
| 节日 | AmbientBarkModule.cs:223 `!IsFestivalNow` | 不分支 → 常规队列（festivalAnyBusy 节流照常） |
| NPC 查名失败 | AmbientBarkModule.cs:223 `npcMicro == null` | 不分支 → 常规队列 |
| 全空白行 | AmbientBarkModule.cs:222 `microLines.Length == 0` | 不分支 → 常规队列（空白行被过滤） |
| fallback（解析/网络失败） | AmbientBarkModule.cs:1122 `Mode = Soliloquy` | 强制 Soliloquy 队列路径 |
| 配置关闭 | ProactiveDialogueManager.cs:70 `EnabledProvider()` | "disabled" → Soliloquy |
| gate0 拦截 | ProactiveDialogueManager.cs:80 | "gate0-recent-main-dialogue" → Soliloquy |
| 每日 cap | ProactiveDialogueManager.cs:91/114/124 | "daily-cap" → Soliloquy |
| 玩家移动中 | ProactiveDialogueManager.cs:104 | "gate2-player-moving" → Soliloquy |
| 心数 0-2 | ProactiveDialogueManager.cs:131 | "gate3-low-hearts" → Soliloquy |
| 桥过期/跨日 | FreshBarkBridgeStore.cs TryConsume | null → 既有 Echo 路径 |

**结论**：CONFIRMED

---

### L10 遗留项

**BarkRequest.SensoryLine 无生产读取方**：`grep "\.SensoryLine" src/` 返回零匹配。  
**理由**：SensoryLine 在 TryBuildMicroSocial 中赋值（BarkPromptBuilder.cs:837），但当前管线无消费方——LlmDialogueService 的 S4.6 区域仅消费 PendingEchoBlock（桥或 Echo），不读取 BarkRequest.SensoryLine。  
**未来用途**：若后续需要将感官行注入回传链路（如 Latex 解析失败时降级显示原始感官行），可直接消费此字段。  
**风险**：无。字段为 null 时不影响任何既有逻辑（BarkRequest 构造时不读取 SensoryLine）。

**结论**：CONFIRMED（已知遗留，无风险）

---

### 审计汇总

| 环节 | 结论 |
|------|------|
| L1 Scan | CONFIRMED |
| L2 决策 | CONFIRMED |
| L3 闸门 | CONFIRMED |
| L4 构建 | CONFIRMED |
| L5 回传 | CONFIRMED |
| L6 直出 | CONFIRMED |
| L7 桥 | CONFIRMED |
| L8 生命周期 | CONFIRMED |
| L9 降级矩阵 | CONFIRMED |
| L10 遗留项 | CONFIRMED（无风险） |

**CONFIRMED 数**：10  
**DEVIATION 数**：0  
**缺陷清单**：无

---

## 第二部分：人工实测清单

> **前置条件**：SMAPI Debug 日志开启；EnableAmbientBarks = true；EnableProactiveMicroSocial = true

### T1 感官全链（微社交直出 + 桥注入）

**前置条件**：Bark 总开关开启；EnableProactiveMicroSocial = true  
**操作步骤**：  
1. 调试刷取刘易斯短裤并穿上  
2. 静止靠近开了 Bark 的村民（如 Alex）  
3. 观察日志序列  
4. 气泡出现后 3 秒内按 A（对话键）  

**期望日志锚点**：  
- `[Proactive] MicroSocial committed: Alex (sensory:LewisShorts)`  
- 气泡立即直出 line[0]  
- 按 A 后对话 Debug 日志中 prompt 含 `<fresh_bark_bridge>`  
- 不含 `recent_interaction_echo`  

**失败时上报**：L3（committed 缺失）→ L6（无直出）→ L7（桥块缺失）

---

### T2 桥过期（>3 秒后按 A）

**前置条件**：同 T1  
**操作步骤**：  
1. 重复 T1 步骤 1-3  
2. 等待 >3 秒后按 A  

**期望日志锚点**：  
- prompt 含 `recent_interaction_echo`（打捞的 [1..2]）  
- 不含 `<fresh_bark_bridge>`  

**失败时上报**：L7（桥未过期 / 仍有桥块）

---

### T3 关系路径（7+ 心）

**前置条件**：7+ 心 NPC；无感官装束  
**操作步骤**：  
1. 靠近 7+ 心 NPC（如配偶）  
2. 观察是否概率性/确定性出现 committed 日志  

**期望日志锚点**：  
- `[Proactive] MicroSocial committed: <NPC> (sensory:none)`  

**失败时上报**：L3（闸门 3 判定异常）

---

### T4 闸门 0（主对话后 120 分钟内）

**前置条件**：与某 NPC 刚结束主对话  
**操作步骤**：  
1. 与 NPC 对话后立即靠近  
2. 观察 120 游戏分钟内无 committed 日志  

**期望日志锚点**：  
- 无 `[Proactive] MicroSocial committed` 日志  
- 仅有常规自语  

**失败时上报**：L3（gate0 未拦截）

---

### T5 总开关（EnableProactiveMicroSocial = false）

**前置条件**：GMCM 中关闭 EnableProactiveMicroSocial  
**操作步骤**：  
1. 关闭开关  
2. 游玩任意时长  
3. 观察全程零 committed 日志  

**期望日志锚点**：  
- 无 `[Proactive] MicroSocial committed` 日志  

**失败时上报**：L3（disabled 闸门失效）

---

### T6 降级（节日 / 玩家移动中）

**前置条件**：节日内 / 玩家持续移动  
**操作步骤**：  
1. 节日内靠近 NPC  
2. 持续移动中靠近 NPC  
3. 观察无直出（常规队列或无触发）  

**期望日志锚点**：  
- 无 `[Proactive] MicroSocial committed` 日志或常规队列行为  

**失败时上报**：L6（直出分支未排除节日）/ L3（gate2 未拦截移动）

---

### T7 每日 cap

**前置条件**：同 NPC 当日已触发一次 MicroSocial  
**操作步骤**：  
1. 触发一次 MicroSocial  
2. 再次靠近同 NPC  
3. 观察无第二次 committed 日志  

**期望日志锚点**：  
- 无 `[Proactive] MicroSocial committed` 日志  
- 或 DenyReason="daily-cap"  

**失败时上报**：L3（daily-cap 未拦截）

---

## 备注

- **R3（互动频率 20-30%）**：需真实游玩统计，不在本工单范围。上线后调参项：`MicroSocialMidFriendshipChance`（默认 0.35）。
- **唯一代码改动**：AmbientBarkModule.cs FetchBarksAsync Debug 日志扩展 1 行（`Mode={request.Mode}`），Config.Debug 门控保持，常规运行时零开销。
