# VT3-C1 阶段验收文档（纸面工单 · VT3-C1-V2）

> 本文档从 `git show d9cff9c5` 与当前 `src/LLM/Prompts/Prompts.cs` 产出，零游戏依赖。
> 关联提交：`d9cff9c5`（VT3-C1 主体）· 关联修复票：`task_5a31819d`（ComposeCurrentConversationHeading 双体）。

---

## 一、逐方法移动清单表

> 说明：移动检测以"实例方法体逐字对应静态方法体（仅实例状态访问替换为显式参数）"为判据。
> 复制体数量 = 0（每个实例生产者有且仅有一个 PromptsBlocks 静态对应项，无 result/wrapper 类型）。

### 1.1 Tier 1 生产者（17 个）

| # | 方法 | 原 instance 处置 | 静态对应 | 移动检测 | 备注 |
|---|---|---|---|---|---|
| 1 | `GetGameState` | 删除 | `PromptsBlocks.BuildGameState` | ✓ 体逐字 | 含 Kent 年判断 |
| 2 | `GetEventHistory` | 删除 | `PromptsBlocks.BuildEventHistory` | ✓ 体逐字 | 委托 EventHistoryHelper |
| 3 | `BuildBranchTheme`（四态） | —（新静态方法，内联块搬移） | `PromptsBlocks.BuildBranchTheme` | ✓ 四分支前缀逐字 | StoodUp/Date/Greeting/Normal；Date 含 RecordDateDialogue |
| 4 | `GetMicroEnvironment` | 删除 | `PromptsBlocks.BuildMicroEnvironment` | ✓ 体逐字 | 入参增加 `ContextFlags flags` |
| 5 | `GetSensoryWeatherDescription` | 删除 | `PromptsBlocks.BuildSensoryWeatherDescription` | ✓ 体逐字 | 入参增加 `Character character` |
| 6 | `AppendCompanionWalkingContext` | 删除 | `PromptsBlocks.BuildCompanionWalkingContext` | ✓ 体逐字 | 入参增加 `bool isZh` |
| 7 | `InjectGreetingContext` | 删除 | `PromptsBlocks.BuildGreetingContext` | ✓ 体逐字 | — |
| 8 | `GetMarriageFeelings` | 删除 | `PromptsBlocks.BuildMarriageFeelings` | ✓ 体逐字 | 入参 `name` 替代 `Name` |
| 9 | `GetChildren` | 删除 | `PromptsBlocks.BuildChildren` | ✓ 体逐字 | — |
| 10 | `GetTrinkets` | 删除 | `PromptsBlocks.BuildTrinkets` | ✓ 体逐字 | — |
| 11 | `GetSpouse` | 删除 | `PromptsBlocks.BuildSpouse` | ✓ 体逐字 | — |
| 12 | `GetSpecialRelationshipStatus` | 删除 | `PromptsBlocks.BuildSpecialRelationshipStatus` | ✓ 体逐字 | 入参增加 `pendingMilestoneBlock` |
| 13 | `GetNonSpouseFriendshipLevel` | 删除 | `PromptsBlocks.BuildNonSpouseFriendshipLevel` | ✓ 体逐字 | 入参增加 `CharacterData npcData` |
| 14 | `GetFriendshipText` | 删除 | `PromptsBlocks.BuildFriendshipText` | ✓ 体逐字 | 入参增加 `Character character` |
| 15 | `GetRecentEvents` | 删除 | `PromptsBlocks.BuildRecentEvents` | ✓ 体逐字 | 入参增加 `allPreviousActivities` |
| 16 | `GetSpecialDatesAndBirthday` | 删除 | `PromptsBlocks.BuildSpecialDatesAndBirthday` | ✓ 体逐字 | — |
| 17 | `GetSpouseAction` | 删除 | `PromptsBlocks.BuildSpouseAction` | ✓ 体逐字 | — |

### 1.2 Tier 2b 生产者（8 个）

| # | 方法 | 原 instance 处置 | 静态对应 | 移动检测 | 备注 |
|---|---|---|---|---|---|
| 18 | `GetPreoccupation` | 删除 | `PromptsBlocks.BuildPreoccupation` | ✓ 体逐字 | 含 50% roll；入参增加 `injectedPrivateThoughts` |
| 19 | `GetGift` | 删除 | `PromptsBlocks.BuildGift` | ✓ 体逐字 | 入参增加 `giveGift` |
| 20 | `<interaction_state>`（内联块） | 删除（内联→静态） | `PromptsBlocks.BuildInteractionState` | ✓ 体逐字 | 入参增加 `isMarriedOrRoommate` |
| 21 | `<jealousy_trigger>`（内联块） | 删除（内联→静态） | `PromptsBlocks.BuildJealousyTrigger` | ✓ 体逐字 | 入参增加 `bool isZh` |
| 22 | `AppendDateInvitationProtocol` | 删除 | `PromptsBlocks.BuildDateInvitationProtocol` | ✓ 体逐字 | 入参 `ContextFlags flags` |
| 23 | `AppendFollowInvitationProtocol` | 删除 | `PromptsBlocks.BuildFollowInvitationProtocol` | ✓ 体逐字 | 入参增加 `Character character` |
| 24 | `AppendDateEndingProtocol` | 删除 | `PromptsBlocks.BuildDateEndingProtocol` | ✓ 体逐字 | 入参 `ContextFlags flags` |
| 25 | `InjectMovementInstruction` | 删除 | `PromptsBlocks.BuildMovementInstruction` | ✓ 体逐字 | 入参增加 `bool moveApplicable` |

### 1.3 Tier 2a 生产者（2 个）

| # | 方法 | 原 instance 处置 | 静态对应 | 移动检测 | 备注 |
|---|---|---|---|---|---|
| 26 | `GetCurrentConversation` | 删除 | `PromptsBlocks.BuildCurrentConversation` | ✓ 体逐字 | 入参增加 `emittedBlockKeys`；见 §三双体修复票 |
| 27 | `InjectSessionContinuity` | 删除 | `PromptsBlocks.BuildSessionContinuity` | ✓ 体逐字 | 入参增加 `bool isZh` |
| 28 | `InjectPendingTopic` | 删除 | `PromptsBlocks.BuildPendingTopic` | ✓ 体逐字 | 入参增加 `injectedPrivateThoughts` |

### 1.4 Helper 搬移（5 个）

| # | Helper | 处置 | 出处 → 现址 | 可见性 |
|---|---|---|---|---|
| H1 | `NpcIsMale` | 提升为静态方法 | instance 字段计算逻辑 → `PromptsBlocks.NpcIsMale(character)` L839 | `private static` |
| H2 | `BuildRelationshipWord` | 删除实例方法，新建静态 | `RelationshipWord` L1770（删）→ `PromptsBlocks.BuildRelationshipWord` L836 | `internal static` |
| H3 | `FormatDateElapsed` | 删除实例方法，新建静态 | L284（删）→ `PromptsBlocks.FormatDateElapsed` L850 | `internal static` |
| H4 | `BuildDateGiftLine` | 删除实例方法，新建静态 | L295（删）→ `PromptsBlocks.BuildDateGiftLine` L858 | `internal static` |
| H5 | `LoadLocalised` | 原位保留（已是 static） | L806（原即 `private static`） | `private static`（嵌套类可访问） |

### 1.5 记账副作用锚点

| 副作用 | 位置 | 处置 |
|---|---|---|
| `RecordDateDialogue`（Date 分支内联副作用） | 原 L552-554 → 现 `BuildBranchTheme` Date case L945 | 逐字迁移；注释标注"Manager 访问，非 Prompts 状态" |
| Preoccupation 50% roll | 原 L976 → 现 `BuildPreoccupation` L1381 | 逐字保留（原位：roll 分支内外） |
| `_injectedPrivateThoughts.Add` | 原 GetPreoccupation L1018 / InjectPendingTopic L1069 → 现 BuildPreoccupation L1417 / BuildPendingTopic L1728 | 逐字保留；写入目标经参数穿线 |
| `MarkEmitted("CurrentConversation")` | 原 GetCurrentConversation L1030/L1051 → 现 BuildCurrentConversation L1642/L1660 | 逐字保留（`emittedBlockKeys.Add`）；`MarkEmitted` 包装方法已删除（引用数归零） |

### 1.6 EvolvedTraits

`EvolvedTraitManager.GetPromptBlock` 调用点位于 `LlmDialogueService.cs:80`（**Prompts.cs 外部**，不在 target_files）。
`PendingEvolvedTraitsBlock` 在 Prompts.cs 内为纯 passthrough 字段（get/set），无生产者方法需提取。
**结论：本文件内无 EvolvedTraits 生产者，符合预期。**

---

## 二、三辅助方法审计（否决权项）

> 判定标准："单一方法体，双调用方可达" = 合法；"静态体内联同文 + instance 版并存" = Δ1 双体违约。

### 2.1 `ComposeCurrentConversationHeading`

- **实例方法体**：L61
  ```csharp
  return "### " + Util.GetString(Character, "currentConversationHeading");
  ```
- **调用方**：
  - `LogTopologyVerification` L592（`computedHeading = ComposeCurrentConversationHeading()`）
  - `LogTopologyVerification` L623（`string.IsNullOrEmpty(Util.GetString(Character, "currentConversationHeading"))` — 同逻辑内联，非方法调用）
- **BuildCurrentConversation 获取同逻辑路径**：
  - L1641：`prompt.AppendLine("### " + Util.GetString(character, "currentConversationHeading"));`
  - L1660：`prompt.AppendLine("### " + Util.GetString(character, "currentConversationHeading"));`
  - **判定：静态体内联同文 + instance 版并存 → Δ1 双体违约 ⚠️**

### 2.2 `ComposeInstructionsHeading`

- **实例方法体**：L66
  ```csharp
  return "## " + Util.GetString(Character, "instructionsHeading", new { Language = TargetLanguageName });
  ```
- **调用方**：
  - `LogTopologyVerification` L601（`instructionsText.Contains(ComposeInstructionsHeading())`）
- **BuildCurrentConversation / 其他静态生产者**：无内联同文。`instructionsHeading` 的实际渲染由 Tier-0 `GetInstructions`（instance L739，**不在 C1 范围**）完成，为 instance-instance 同文（均属遗留 Tier-0，非 C1 引入）。
- **判定：无双体（静态侧无对应内联）。✓**

### 2.3 `CurrentConversationHasContent`

- **实例方法体**：L71
  ```csharp
  return (Context?.ChatHistory?.Any() ?? false) || (Character?.SpokeJustNow() ?? false);
  ```
- **调用方**：
  - `LogTopologyVerification` L576（`bool expected = routeType != "STOOD_UP" && CurrentConversationHasContent();`）
- **BuildCurrentConversation 获取同逻辑路径**：
  - L1639：`if (context.ChatHistory.Any())` — 结构为 `if (A) … else if (B)`，**非** `A || B` 同文。
  - 语义相关但文本结构不同（分支 vs 布尔或），不构成"同文"。
- **判定：无双体（静态侧为 if/else 结构，非内联同文）。✓**

### 2.4 审计结论

| 辅助方法 | 双体？ | 动作 |
|---|---|---|
| `ComposeCurrentConversationHeading` | **是（Δ1 违约）** | 开修复票 → `task_5a31819d` |
| `ComposeInstructionsHeading` | 否 | — |
| `CurrentConversationHasContent` | 否 | — |

**修复票 `task_5a31819d` 内容**：提取 `PromptsBlocks.BuildConversationHeading(character)` 静态方法，供
`BuildCurrentConversation`（静态）与 `ComposeCurrentConversationHeading`（instance 薄壳委托）共用，
消除双体。修复属 C1 范畴，V2 纸单不自行触碰 Prompts.cs。

---

## 三、Δ6.4 读写图

### 3.1 阶段划分

- **PLAN** = 全部 Tier 2b 内容构建器（Preoccupation / Gift / InteractionState / JealousyTrigger / DateInvitation / FollowInvitation / DateEnding / Movement）— 在 GetCorePrompt 薄壳调用时产出字符串。
- **ASSEMBLY** = Tier 1 / Tier 2a 内容构建器（GameState / EventHistory / BranchTheme / MicroEnvironment / GreetingContext / MarriageFeelings / Children / Trinkets / Spouse / SpecialRelationshipStatus / NonSpouseFriendshipLevel / FriendshipText / RecentEvents / SpecialDates / SpouseAction / CurrentConversation / SessionContinuity / PendingTopic）— 在 GetCorePrompt 薄壳调用时产出字符串。

> 注：本工单的"PLAN/ASSEMBLY"是借用 VT3-C2 的薄壳内调用序语义。实际 C1 中所有静态构建器均在 GetCorePrompt 薄壳内同步调用（无真正队列），两阶段在同一请求内顺序完成。

### 3.2 `_injectedPrivateThoughts`（`List<string>`）

| 角色 | 位置 | file:line | 阶段 |
|---|---|---|---|
| **声明/存储载体** | `Prompts.cs` | L26 `private readonly List<string> _injectedPrivateThoughts` | — |
| **写入者** | `PromptsBlocks.BuildPreoccupation` | L1417 `injectedPrivateThoughts.Add(preoccupation)` | PLAN |
| **写入者** | `PromptsBlocks.BuildPendingTopic` | L1728 `injectedPrivateThoughts.Add(pending)` | PLAN |
| **薄壳穿线（写入目标）** | `GetCorePrompt` | L326 / L382 / L405 / L492 / L539 | ASSEMBLY 调用点 |
| **读取者（公开属性）** | `Prompts.cs` | L27 `public IReadOnlyList<string> InjectedPrivateThoughts` | — |
| **读取者（下游消费）** | `LlmDialogueService.ProcessLines` | L268, L370（`prompts.InjectedPrivateThoughts`） | 下游（C1 范围外） |

**判定**：
- 规则 1（同阶段）：写入者（BuildPreoccupation/BuildPendingTopic）均为 PLAN 阶段。✓
- 规则 4（Δ8）：字段保留为存储载体，构建器以显式参数接收并写入，薄壳传入实例字段。✓
- 读取者 `LlmDialogueService` 为下游消费（C1 范围外、不动），不构成跨阶段反转。
- **结论：无顺序反转，Δ8 合规。**

### 3.3 `_emittedBlockKeys`（`HashSet<string>`，StringComparer.Ordinal）

| 角色 | 位置 | file:line | 阶段 |
|---|---|---|---|
| **声明/存储载体** | `Prompts.cs` | L29 `private readonly HashSet<string> _emittedBlockKeys` | — |
| **写入者** | `PromptsBlocks.BuildCurrentConversation` | L1642, L1660 `emittedBlockKeys.Add("CurrentConversation")` | ASSEMBLY |
| **薄壳穿线（写入目标）** | `GetCorePrompt` | L380 / L403 / L539 | ASSEMBLY 调用点 |
| **读取者** | `Prompts.cs.LogTopologyVerification` | L575 `_emittedBlockKeys.Contains("CurrentConversation")` | 遗留验证（C1 范围外） |

**判定**：
- 规则 1（同阶段）：写入者与读取者均在 ASSEMBLY（GetCorePrompt 薄壳内 BuildCurrentConversation 写入，LogTopologyVerification 在 return 前读取）。✓
- 规则 4（Δ8）：字段保留为存储载体，构建器以显式参数接收并写入。✓
- LogTopologyVerification 为遗留验证逻辑，其读取位于 `prompt.ToString()` 之后（L575 在 L527 `string finalPrompt = prompt.ToString()` 之后），顺序保持。
- **结论：无顺序反转，Δ8 合规。**

### 3.4 `RecordDateDialogue` 写入目标（外部 Manager，非 Prompts 状态）

| 角色 | 位置 | file:line |
|---|---|---|
| **写入者** | `PromptsBlocks.BuildBranchTheme` Date case | L945 `DateManager.Instance.RecordDateDialogue(…)` |

**判定**：`DateManager.Instance` 为外部 Manager 访问，非 Prompts 实例状态，非 Δ8 两集合。
按 Δ4 "外部 Manager → 逐字移动" 处置。**合规。**

### 3.5 读写图判定汇总

| 集合/目标 | 写入阶段 | 读取阶段 | 顺序反转？ | 结论 |
|---|---|---|---|---|
| `_injectedPrivateThoughts` | PLAN | 下游（C1 外） | 否 | ✓ Δ8 合规 |
| `_emittedBlockKeys` | ASSEMBLY | ASSEMBLY（遗留验证） | 否 | ✓ Δ8 合规 |
| `RecordDateDialogue` | ASSEMBLY（BuildBranchTheme） | —（外部 Manager） | 否 | ✓ Δ4 合规 |

---

## 四、OQ4 结论（StoodUp 路由门控实况）

读 `d9cff9c5` 薄壳路由段（当前 `GetCorePrompt` L322）：

```csharp
if (flags?.HasStoodUpPending == true && ModEntry.Config.EnableDateSystem)
{
    prompt.Append(PromptsBlocks.BuildBranchTheme(Character, Context, InstructionsBranch.StoodUp));
    …
```

**结论：StoodUp 分支含 `ModEntry.Config.EnableDateSystem` 门控。**
实况：条件 `flags?.HasStoodUpPending == true && ModEntry.Config.EnableDateSystem`。
与遗留代码（原 L455）完全一致 — 门控随遗留，未新增未删除。

---

## 五、搜索证据

### 5.1 PromptsBlocks 签名无 `Prompts` 类型参数

```
grep "Prompts[^B]" → 无 param/类型引用
```

所有 PromptsBlocks 静态方法均以 `Character character` / `DialogueContext context` /
`ContextFlags flags` / 值类型 / 集合作为参数，**无 `Prompts` 类型参数**。✓

### 5.2 返回值仅 string

所有 PromptsBlocks 方法签名为 `internal static string BuildXxx(…)`。
**无 `PreoccupationResult` / `CurrentConversationResult` / `PendingTopicResult` 等 wrapper 类型重新声明。** ✓
（空串 = 本轮不渲染，无第三态。）

### 5.3 `_c1_ledger.md` 不存在

```
git show d9cff9c5 --name-only → 无 ledger
ls _c1_ledger.md → 不存在
```

**台账文件未提交、工作区亦不存在。** ✓

### 5.4 记账副作用逐字保留

| 副作用 | 原位置 | 现位置 | 证据 |
|---|---|---|---|
| Preoccupation 50% roll | L976 | L1381 | `if (Game1.random.NextDouble() < 0.5) return prompt.ToString();` 逐字 |
| Preoccupation 写入 `_injectedPrivateThoughts` | L1018 `_injectedPrivateThoughts.Add(preoccupation)` | L1417 `injectedPrivateThoughts.Add(preoccupation)` | 逐字（参数名小写） |
| PendingTopic 写入 `_injectedPrivateThoughts` | L1069 | L1728 | 逐字 |
| MarkEmitted → emittedBlockKeys.Add | L1030/L1051 | L1642/L1660 | 逐字（`MarkEmitted` 包装已删，引用数归零） |
| RecordDateDialogue | L552-554 | L945 | 逐字 + 注释标注 |

### 5.5 静态生产者体内 Prompts 实例状态引用

构建通过（`dotnet build` 0 错误 0 警告）即证明静态方法无非法实例成员访问
（C# 编译器禁止 static 方法访问实例成员）。BuildStardewSummary 为唯一残留 instance 方法（Tier 0，范围外）。✓

---

## 六、验收标准核对

| # | 标准 | 状态 | 证据 |
|---|---|---|---|
| 1 | `vt_dump_topology` 改造前后 Normal/StoodUp 逐字节一致 | **待运行时** | 需游戏+SMAPI 执行；本纸单无法验证，见 §七 |
| 2 | 逐方法移动清单表 + 处置 + 移动检测 + 复制体=0 | ✓ | §一（28 行 + 5 helper，复制体=0） |
| 3 | 静态生产者体内 Prompts 实例状态引用 = 0 | ✓ | §5.5（编译器强制）+ 两集合参数化写入除外 |
| 4 | 侧效读写图 + 判定结论 | ✓ | §三 |
| 5 | 记账副作用代码行逐字保留 | ✓ | §5.4 |
| 6 | 编译零错误零新警告 | ✓ | `dotnet build` → 0 error 0 warning |
| 7 | 单一 commit 落 master，台账文件已删除 | ✓ | `d9cff9c5`，无 ledger |

---

## 七、离线比对工具门判定（DumpComparer）

> 工具位于 `tools/DumpComparer/`，形态随仓库惯例（Python 脚本，不接入生产构建）。
> 详见 §八。

### 7.1 噪声基线（模式 A：同 commit 两次运行比对）

预期随机项（以 R1 vs R2 实测为准，不做先验豁免）：
- Preoccupation 出席（50% roll）— `BuildPreoccupation` L1381
- gossip 候选（`_mentionedGossipKeys` 去重，位于 PerceptionInjector，非 Prompts.cs）
- 每日关系抽选 — `RelationshipRotationResolver.SelectDailyRelationships`（L281，Tier-0）
- 情绪快照（若含随机）— `EmotionalStateResolver`

### 7.2 门判定（模式 B：pre-C1 vs post-C1 比对）

- **PASS 条件**：pre vs post 差异 ⊆ 噪声基线（模式 A 实测的变化行分类）。
- **FAIL 条件**：任何超出噪声基线的差异 → 输出差异块锚点（file/branch/行）。

### 7.3 当前判定

- 模式 A 与模式 B 均需实际 dump 输入（依赖 `vt_dump_topology` 在游戏内产出）。
- 本工具为**机械执行器**：给定两组dump目录，自动产出 PASS/FAIL + 差异锚点。
- **当前状态**：工具待样例 dump 演示（见 §八 README）。

---

## 八、DumpComparer 工具

目录：`tools/DumpComparer/`

- `compare.py` — 双模式入口（A 噪声基线 / B 门判定）。
- `README.md` — 用法、输入格式、输出示例。

输入：两组 dump 目录（每组 4 分支 + manifest）。

设计要点（不接入生产构建；仓库内独立可运行）：
- 解析 `manifest.json` 获取分支列表（Normal / StoodUp / Date / Greeting）。
- 逐分支逐行 diff，分类"变化行"为：随机项（Preoccupation/gossip/关系/情绪）vs 结构性变化。
- 模式 A：同 commit 两次运行 → 输出实际变化行分类（建立噪声基线）。
- 模式 B：pre vs post → 差异 ⊆ 噪声基线 = PASS；超集 = FAIL + 锚点。

详见 `tools/DumpComparer/README.md`。
