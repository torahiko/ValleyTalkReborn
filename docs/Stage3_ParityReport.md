# Stage 3 Parity Report (VT3-E-INS)

## 1. A/B Parity: `vt_ab_topology`（VT3-E-INS 仪器修正版）

### Method（每分支，共 4 分支：Normal / StoodUp / Greeting / Date）
1. **每分支会话隔离**：`SessionCache.ClearForNpc(npc)`（与 multi 同规）+ 反射清除该 NPC 在
   `Tier1SnapshotStore` 的 active/closed 记录 → `TryReuseSession` 必走"新会话"分支
   （禁跨分支/真实游玩快照复用）。
2. **单一上下文**：每分支 `BuildPopulatedContext` 一次构建；同一实例流入 BuildPlan、
   Side A 遗留薄壳、Side B `AssembleCore`。日志输出同源断言（反射读两侧 `Prompts.Context`
   私有 getter，`ReferenceEquals` 校验；不同源 → 该分支 FAIL，比对无效）。
3. **Side A（遗留）**：`plan.ActiveImpulses` 注入 `Pending*` 字段 + `PendingEchoIsBridge`
   + `PendingEvolvedTraitsBlock`（取自 `plan.Tier1Snapshot`，补齐遗留服务注入职责——
   遗留四分支均渲染该块），读 `CorePrompt`。
4. **Side B（新）**：fresh `Prompts` + `AssembleCore(plan, context, character)` +
   `Instructions = GetInstructions(plan.Branch)`（生产接线镜像，LlmDialogueService.cs:91）。
5. **三线分支断言**：intended / A resolved（遗留级联镜像，含 `PendingMilestoneBlock` 门控）/
   B resolved(=plan.Branch)，同行输出 `EnableDateSystem` 与 `session reused`；
   intended ≠ resolved（任一侧）→ 该分支 FIXTURE_FAIL 跳过比对，不产出垃圾 FAIL。
   StoodUp 的 `EnableDateSystem=false` 问题在此显形。
6. **块集比对**：空行分块 → 块集等价 + Instructions 子集校验（legacy Normal 行 ⊆ new 分支行）。
7. **Date 夹具**：复用 `DumpDateBranch` 状态操纵（Phase=Active 等 5 字段，finally 恢复）；
   夹具仍失败 → 3 分支覆盖 + Date 转移 P-1（日志记录，非阻塞）。

### 随机方差类（WARN 不 FAIL）
- Preoccupation（Side A 50% 重掷）、Gift（双抽取点）——存在/内容差 = WARN。

### Tier 0 观察桶（无断言）
SystemPrompt / GameConstantContext / NpcConstantContext —— 仅记录（gossip 轮换天然不稳定）。

### 差异明细格式（mismatch 必输出，§5 失败路径硬化）
- 三个清单：仅 A 侧块 / 仅 B 侧块 / 双侧皆有但内容不同（首行配对）。
- 每个内容差异块分类：
  - **【归一化后同文】**——去全部空白后相同 → 比对器归一化缺口（修比对器）；
  - **【真差异】**——附首个差异行样本（A/B 各一行，截断展示）→ 开 C1-D 修复票。

### 比对器自测：`vt_ab_topology --selftest`
- SELFTEST-1 自反性：side A vs 自身 → 必须 0 差异（分块/归一化无自反性缺陷）。
- SELFTEST-2 捕获力：side A vs 去除一个已知常规块 → 必须 FAIL 且正确定位。
- 双向 PASS 方可采信 A/B 结果；任一 FAIL → 比对器不可用。

## 2. Multi-turn: `vt_ab_topology_multi`（VT3-E-INS 仪器修正版）

### Method
- 每轮 `SessionCache.ClearForNpc(npc)`；**累积式**罐头历史（0/2/4 行，行文本跨轮稳定
  `hist-line{i}`），使 CurrentConversation 逐轮只追加。
- 段捕获：Tier2a 用独立 fresh `Prompts` 实例渲染（避免与 `AssembleCore` 的
  `_emittedBlockKeys` 台账交叉——A4 字节级拼接完整性的前提）。
- 断言（每条独立日志行，summary 按 A1~A4/Rotation 命名，不再聚合计数）：
  - **A1**：`Seg_A(1)==(2)==(3)`（Tier1 冻结，active 续用保证）。
  - **A2**：`Seg_X(1)⊑Seg_X(2)⊑Seg_X(3)` 字节前缀链；输出有效 `historyWindow`
    （window<4 时尾部截断会破坏前缀链，属夹具环境而非管线缺陷；turn1 空历史受
    `SpokeJustNow` 影响，FAIL 时输出各轮长度归因）。
  - **A3**：`Seg_B(i)==RenderTier2b(plan_i)` 逐轮构造一致性（段捕获后重渲同一 plan，
    字节级；非跨轮比较——原实现为 no-op 已废除）。
  - **A4**：`CorePrompt(i)==Seg_A⊕Seg_X⊕Seg_B` 拼接完整性（字节级，逐轮）。
- Rotation probe（turn4 Normal→Greeting）：reused=F、Seg_A 变化=T。
- Reuse 观察行：turn1/2/3 = F,T,T。

## 3. 重跑协议（本票验收路径）

1. 修复 commit + push 后，人工单次启动游戏，依次执行：
   `vt_ab_topology --selftest` → `vt_ab_topology <NPC>` → `vt_ab_topology_multi <同NPC>`，
   报告回传。
2. 判定：selftest 双向 PASS + A/B 4 分支（或 3+Date 转移 P-1）等价 + multi A1~A4 全绿
   → E-R2 清理放行。
3. FAIL → 按 §1 差异明细归因：【归一化后同文】→ 修比对器归一化；【真差异】→ 开 C1-D
   修复票。

## 4. Consumption Regression Spot-check

After a successful turn, `ConfirmDynamicBlocksConsumed` should zero:
- Eavesdrop → `ConsumeEavesdropEntries` ✓
- SpouseWaiting → `ConfirmSpouseDialogueConsumed` ✓
- Echo (non-bridge) → `ConsumeEcho` ✓
- Milestone → `ConfirmConsumed` ✓

No side effects expected on:
- Gift / Emotion / LocalPerception / Topic.

(Verified by code inspection of `LlmDialogueService.ConfirmDynamicBlocksConsumed`.)

## 5. Segment Accessors (internal, retained through cleanup)

- `Prompts.AssembleTier1Segment(plan)` — static.
- `Prompts.AssembleTier2aSegment(context, character)` — instance.
- `Prompts.AssembleTier2bSegment(plan)` — static.

Single-line delegates over the existing `AssembleTier1/2a/2b` private methods.

## 6. Build & Test Status

- `dotnet build`: 0 errors, 0 warnings.
- `dotnet test`: 194/194 passed.

## 7. Date Branch Cross-reference (P-1, non-blocking)

ab 命令已含 Date 夹具（复用 DumpDateBranch 状态操纵）。若夹具仍失败（FIXTURE_FAIL），
按 3 分支覆盖验收，Date 运行时验证转移 P-1（记录，非阻塞）。

## 9. VT3-E-R2 Cleanup Deletion (阶段 3 收口)

### 9.1 Deleted members — `src/LLM/Prompts\Prompts.cs`

| Item | Kind | Rationale |
|------|------|-----------|
| `GetCorePrompt()` + `#pragma CS0618` pair | method body | Maze retired; `CorePrompt` now backed by `_corePrompt` field populated solely by `AssembleCore` (LlmDialogueService.cs:90 calls `AssembleCore` before any `CorePrompt` read). |
| `PendingEvolvedTraitsBlock` / `PendingLocalPerceptionBlock` / `PendingEavesdropBlock` / `PendingSpouseWaitingBlock` / `PendingEchoBlock` / `PendingEchoIsBridge` / `PendingMilestoneBlock` / `PendingEmotionBlock` + `[Obsolete]` attributes | properties | InjectionPlan/AssembleCore is now the single source; no remaining producers/consumers. |
| `CorePrompt` lazy-init `??= GetCorePrompt()` → direct `_corePrompt` | property accessor | Matches ticket invariant "CorePrompt 起始为空，仅 AssembleCore 填充". |
| `ComposeInstructionsHeading()` | method | Only consumer was `LogTopologyVerification` (deleted). |
| `CurrentConversationHasContent()` | method | Only consumer was `LogTopologyVerification` (deleted). |
| `LogTopologyVerification()` + `#pragma CS0618` pair | method body | Call sites already removed in VT3-D. |
| `GetInstructions()` parameterless overload | method | Replaced by direct `GetInstructions(InstructionsBranch.Normal)` call in `Instructions` getter; overload retained per ticket. |

### 9.2 Deleted members — `src\Debug\PromptTopologyDumper.cs`

Entire file rewritten to retain only the multi-turn command, selftest, and their transitive dependencies. Deleted:

- **Commands**: `vt_dump_topology` (registration + `OnCommand` + `RunDump` + `DumpBranch` + `DumpDateBranch` + `WriteManifest`; G2 baseline archival complete, superseded by production `[AI Request Context]` dumps), `vt_ab_topology` (registration + `OnAbCommand`; superseded by selftest-on-multi).
- **Date fixture machinery**: `SetDateField`, `RunAbDateBranch`, `DumpDateBranch`.
- **Side-A helpers**: `RunAbBranch`, `ResolveLegacyBranchMirror`, `ForceNewTier1Sessions`, `GetPromptsContext`, `CompareBlockSets`, `NormalizeToBlocks`, `LogBlockDiffDetail`, `ComputeBlockDiffDetail`, `BlockDiffDetail`, `GetInstructionLines`.
- **Diff/block helpers**: `FirstLine`, `StripAllWhitespace`, `FirstDifferingLine`, `Truncate`, `FlattenLines`-adjacent diagnostics (`LogBlockDiffDetail`).
- **Reflection plumbing**: `EnsureReflection`, `ResolveStatic`, `ResolveStaticTwoArgs`, `ResolveMethod`, `InvokeStatic`, `InvokeMethod`, `GetMilestoneManagerInstance`, `PromptDeduplicatorDeduplicate` + all `MethodInfo` caches.
- **Dump helpers**: `DumpFullRequest`, `AppendSection`, `WriteManifest`, `TryGetGitCommitHash`, `TryGetGameVersion`, `FormatInGameDate`, `GetOwnModVersion`.
- **Enums**: `BranchOutcome`, `ParityResult` + `GetLabel`.
- `#pragma CS0618` pairs in `DumpFullRequest` (item 9) — removed with the method.

### 9.3 Retained (per ticket keep-list)

- `Prompts.AssembleTier1Segment / AssembleTier2aSegment / AssembleTier2bSegment` (single-line delegates, do not remove).
- `vt_ab_topology_multi` (`OnAbMultiCommand`) including Reuse annotation.
- `--selftest` 5 cases (`RunComparatorSelfTest`) — **moved** from `vt_ab_topology` to `vt_ab_topology_multi --selftest`.
- `BuildPopulatedContext` (shared by director + multi).
- Shared statics: `BuildConversationHeading`, `SelectGiftGiven`, `IsZh`, `MovementInstructionApplicable` (director + multi in use).
- `CompareFlattened` + `FlattenLines` + `IsRandomLine` + `FlatCompareResult` (selftest + retained).
- `BuildTurnChatHistory`, `FindCharacter`, `Info`, `ConversationDirectorInstance`.

### 9.4 Random-class symmetric-removal audit list (retained in comparator)

Lines classified as random-variance and symmetrically removed from both sides before bag comparison (WARN, never FAIL; removal logged for audit):

| Class | Marker |
|-------|--------|
| Preoccupation | `[preoccupation]` |
| Gift | `giftGiving` |
| Gift (zh) | `你刚刚收到了` |
| Gift (en) | `You just received` |

(Per VT3-E-INS2 authoritative comparator; list frozen — extending requires architect approval.)

### 9.5 Zero-reference evidence (post-cleanup `src/` grep)

All symbols below have **zero executable references** in `src/` (only comments / unrelated `Tier2bBlockIds.PendingTopic` enum values remain):

`GetCorePrompt`, `PendingEvolvedTraitsBlock`, `PendingLocalPerceptionBlock`, `PendingEavesdropBlock`, `PendingSpouseWaitingBlock`, `PendingEchoBlock`, `PendingEchoIsBridge`, `PendingMilestoneBlock`, `PendingEmotionBlock`, `ComposeInstructionsHeading`, `CurrentConversationHasContent`, `LogTopologyVerification`, `vt_dump_topology`.

`vt_ab_topology` command registration removed; `--selftest` now lives on `vt_ab_topology_multi`.

### 9.6 Build & Test Status

- `dotnet build ValleytalkReborn.sln`: **0 errors, 0 warnings**.
- `dotnet test`: **194/194 passed**.

### 9.7 Pending manual in-game verification (人工, to be run once by developer)

These require a live SMAPI game session and cannot be executed by the cleanup commit itself:

1. `vt_ab_topology_multi --selftest` → expect **5/5 PASS** (INS2 re-validation).
2. `vt_ab_topology_multi <NPC>` → expect **A1/A2/A3/A4 + Rotation + Reuse** all green.
3. Production smoke: converse once each with two NPCs → `[Director]` Plan normal, `[AI Request Context]` dump normal, no exceptions.

Steps 1–3 gate the final sign-off; the cleanup commit (build + unit tests) is independently green.
