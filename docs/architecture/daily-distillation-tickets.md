# 当日日记提炼：蓝图与串行执行工单

日期：2026-10-02。状态：可供 Supervisor 派发；功能尚未实现。
设计基线：master / 46fbbf5abbf66f8115a29d45ce15fcd38a0b3a34。

## 使用与证据

- 现有接口依据：[本会话 MCP 证据](daily-distillation-mcp-evidence.md)，E01–E18/E20–E22。
- 新增接口依据：[Architect 声明清单](daily-distillation-declarations.md)，D1–D6；本会话已通过 MCP 读取。它们是待实现的设计声明，不能当成现有 API。
- 本文件 Layer 2 是执行权威。声明清单规定精确签名；Layer 2 规定行为。出现矛盾时停止对应工单并交 Architect 决策。
- Worker 先进行 Pre-flight Boundary Check：逐项确认文件、签名、存档所有者、生命周期和接口仍与证据一致；只实施本票范围。
- DD401 → DD402 → DD403 → DD404 → DD404B → DD405 → DD406 → DD407 → DD408，完全串行，共九票。
- 每票直接提交 master；Supervisor 记录真实提交 hash、相关检查结果及 SMAPI 实测证据。当前文档提交不是功能验收提交。
- 实施到 DD408 并通过集成验收后才交付完整功能；中间提交用于审查与依赖，不作为完整功能版本发布。

## Layer 1 — Blueprint

### 产品行为

三档分别为关闭、次日整理、日间更新。新安装默认日间更新；仅有旧开关的配置保留旧行为。
日间模式在累计达到阈值且连续空闲 10 个一秒事件间隔后生成首篇；同日新交流达到阈值后更新同一篇。
预算按实际发起的请求计数，包含首发、更新和失败；默认每 NPC 每目标日 2 次，用户可设 1–5。
次日最终整理独立保留最多 2 次请求，补充已有日记之后的晚间交流；预算耗尽时保留已有正文并明确记录未完成。
首次生成仍要求当天有效交流总数达到阈值；“不足阈值也次日生成”的承诺取消。
已有自动日记的最终整理只要求存在未覆盖的有效 NPC 交流，允许尾段少于阈值。

阈值统一按历史中该 NPC 的有效发言条目计数：SpeakerType.NPC，排除 eavesdrop、gift、session-end 和空白文本。
新任务日期键固定为一基日历序号：(Year-1)*112 + (int)Season*28 + DayOfMonth，即既有DateToDayNumber(date)+1；它与GameDayToStardewTime的正数逆变换一致。由TargetDate计算一次，创建正文与ledger共用，不使用运行结束日替代。旧CreatedDay=0仍按既有未知日期处理，不自动迁移旧日期。
它是“NPC 发言条目数”，不冒充完整问答对或独立会话数；玩家发言、礼物事实可作为生成材料。
UI 关框不是业务边界。每 5 个一秒事件读取已经落库的历史来识别变化；这个派生 dirty 检测兼容换壳、沉默结束与直接退出，避免向可能来自不同线程的历史写入链注入游戏状态访问。

### 状态拓扑

| 数据 | 权威 | 生命周期 |
| --- | --- | --- |
| 模式、阈值、日间请求上限 | Config | 安装级用户设置 |
| 日期、卡片所有权、覆盖行指纹、请求次数、提交 journal、最终整理结果 | NPC.modData | 对应 NPC 的存档持久状态，主机写 |
| 快照、队列、运行集合、闲置计数、存档/配置 epoch | Memory | 本次存档会话；后台仅持有快照 |
| 未结束的 Provider 请求观察句柄 | Memory | 进程级；切档后仍观察结束，不具有存档写权限 |
| 日记正文及现有条目属性 | 既有 MemoryManager 时间线 SaveData | 保留既有存储，不改为全局新库 |

NPC.modData 是新增业务状态权威。正文保留已有 SaveData 是对现有数据兼容的明确例外；不新增第二份正文权威。
ModData 随正常游戏保存形成持久检查点，不能保证进程崩溃或回滚到更早存档后仍保留外部请求预算。
SaveData 写入成功只表示该调用已接受序列化数据，不能宣称与 NPC.modData 跨存储原子落盘。
因此提交使用准备 journal + 期望内容比较 + 恢复核对；无法证明一致时暂停该 NPC/日期，保护玩家数据。

### 唯一性与编辑权

自动任务每 NPC/日期最多管理一张 Daily 卡片。自动创建前复查该日没有任何 Daily 卡片。
存在无所有权记录的旧卡片或玩家卡片时，保留内容并让出该日期。历史多卡片保留，不自动合并或删除。
自动生成后卡片 ID、创建日期和标签稳定；修改正文前比较原内容 hash。
玩家编辑、删除、另添同日卡片会使该日期暂停自动更新；暂停跨读档保留，不通过切模式悄悄恢复。
最终整理已经关闭且卡片后来被升档删除，不触发重建。

### 生成语义与预算

原始材料以当前保留的该日历史为准；现有历史最多 300 条/NPC，不承诺恢复已淘汰的资料。
生成最多使用 48 行，正文材料至多 6000 个 UTF-16 code unit；采用早段、均匀中段、最近段的确定性采样。
以前的日记是语气和情绪延续参考；本轮选中的原始对话是事实锚点。输出是“该日最重要的余韵”，不是 30 字完整事实档案。
生成使用明确目标日期；次日请求仍反思目标日，不用运行时“今天”替代。
语气取当前可取得的人设切片，只用于口吻。语义忠实性需 Supervisor 样本复核；JSON、条数、长度、第一人称标记和姓名检查由程序验证。

### 生命周期与请求边界

配置解析不读取游戏世界；GMCM 在既有 GameLaunched 注册点接入。
SaveLoaded 只建立新 epoch 并要求重建，真正读取存档状态延后到所有 SaveLoaded handler 结束后的主线程一秒事件。
主线程读取 NPC、历史、语言、persona、Provider 引用和配置；后台生成使用捕获的数据。
联机保持既有主机所有权：客机不写此账本，不收集或同步客机本地历史作为主机提炼材料；NPC ModData依靠游戏原生持久/同步机制，不新增网络消息协议。
后台完成只投递 MainThreadActionQueue；主线程重新验证 epoch、模式、主机身份、日期和编辑权后提交。
跨天只使当日日间任务失效，将未覆盖尾段交给最终整理；不会把昨天的结果记成今天。
先最终日记，再周报，再季报，再年报。被浓缩的源条目必须是生成时捕获的 ID 与内容，不能在完成时重新挑一批删除。

连续闲置、35 秒冷却和 Provider 请求占用是三个独立条件。
入场条件降低与连聊竞争；既有 Provider 没有统一优先级调度，不能承诺在途请求被后来的玩家对话抢占。
使用可取消 RunInferenceAsync；忽略取消的 Provider 仍须等待其真实 Task 结束才释放自动任务占用。
网络等待始终异步；上述等待不阻塞 MonoGame 主线程。旧手动提炼与其他后台模块不在本轮流量优先级改造范围。

## Layer 2 — Executable contracts

### DD401 — 配置迁移与用户选项

```xml
<ticket id="DD401-CONFIG-MIGRATION">
  <scope>
    <target_files>H:\ValleytalkReborn\src\Config\ModConfig.cs；H:\ValleytalkReborn\src\UI\GMCM\ModConfigMenu.cs；H:\ValleytalkReborn\src\Core\ModEntry.cs（仅配置校验调用）；H:\ValleytalkReborn\src\i18n\default.json；H:\ValleytalkReborn\src\i18n\zh.json；新建 H:\ValleytalkReborn\ValleytalkReborn.Tests\DailyDistillationConfigTests.cs</target_files>
    <dependencies>E01–E04、E16、E18；D1。沿用现有 Register，不引用不存在的 BuildManifestOptions。</dependencies>
    <forbidden_actions>保留 ProviderProfile 迁移、既有配置和周/季/年选项；本票不接入日间调度，不改 LLM Provider，不安装依赖。</forbidden_actions>
  </scope>
  <contract>
    <signatures>D1 全部精确声明。模式以规范字符串存储，理由是未知模式可进入明确的 Disabled 错误状态而不导致整个配置重置。运行期 AutoSummarizeDaily 保持 public bool 属性；新增阈值/预算是 Config，存在标志是 Memory。</signatures>
    <boundaries>
      <lifecycle>配置反序列化；GMCM 在既有 GameLaunched 注册；零游戏状态访问。</lifecycle>
      <threading>配置规范化与 GMCM 在主线程；反序列化回调只处理当前配置对象。</threading>
      <harmony>N/A</harmony>
    </boundaries>
  </contract>
  <implementation>
    <flow>
1. DailyDistillMode setter 记录新字段已提供。LegacyAutoSummarizeDaily 的只写 JSON 属性仅收集旧 bool，公开旧代理标记 JsonIgnore；它在运行期 getter 返回模式是否开启，setter false 映射 Disabled，setter true 仅将 Disabled 映射 Overnight。
2. OnDeserialized 统一决议：存在新模式就使用新值；否则存在旧 true → Overnight、旧 false → Disabled；两者都不存在 → Intraday。决议与 JSON 字段排列顺序无关。决议后清空迁移暂存，不进行 I/O。
3. Normalize 将模式按忽略大小写映射规范值；未知或空值设 Disabled 并记录配置错误。阈值 clamp 2–10，预算 clamp 1–5；有修改时记录原值与新值。
4. 将规范化接入每个现有 ReadConfig 后，以及 GMCM save 的 WriteConfig 前；其余初始化和迁移保持原顺序。不得自动把旧 true 改成 Intraday。
5. 用 D1 的 UI keys 注册三选一模式、阈值和日间请求预算。tooltip 说明预算包含首次生成和失败、次日另有至多两次最终整理、35 秒不是完成承诺。
6. 输出 JSON 只写新模式/阈值/预算；旧开关仅兼容读取。使用 Json.NET 对比新旧字段的两种排列，重复读取/规范化得到相同结果。
    </flow>
    <failure_paths>
未知模式、越界数值：BUG → Warn → 规范化后的明确值，其他配置保留。
GMCM 不存在：RECOVERABLE → 沿用现有 Warn → Register return，配置文件仍可使用。
本票新增迁移逻辑产生序列化异常：BUG → Error → 显式失败并停止该保存；不覆盖旧配置。
写配置文件发生 I/O 故障：BOUNDARY → Error → 保留运行期配置并报告保存失败，旧文件内容由存储边界保留；不声称已持久化。
    </failure_paths>
  </implementation>
  <verification>
    <criteria>Diff 锚点是旧日记复选框及 ReadConfig 后的规范化。测试覆盖旧 true/false、两种字段顺序、新值优先、缺省、非法值、边界数值及序列化再读取。日志可见配置规范化原因；GMCM 三项双向保存，中英文文案一致。执行 dotnet test 过滤 DailyDistillationConfigTests；提交 master 并报告真实 hash。</criteria>
  </verification>
  <extensions if-applicable="true"><execution_control><sequence>1</sequence><parallelizable_with>none</parallelizable_with><stop_conditions>旧 JSON bool 与私有只写映射发生冲突；任何既有配置值意外丢失；MCP 基线接口不匹配。</stop_conditions></execution_control><open_questions>无。</open_questions></extensions>
</ticket>
```

### DD402 — 稳定快照与统一阈值

```xml
<ticket id="DD402-SNAPSHOT">
  <scope>
    <target_files>新建 H:\ValleytalkReborn\src\Memory\DailyDistillationSnapshot.cs；H:\ValleytalkReborn\src\Models\history\DialogueHistoryManager.cs（仅 GetHistoryNpcNames）；新建 H:\ValleytalkReborn\ValleytalkReborn.Tests\DailyDistillationSnapshotTests.cs</target_files>
    <dependencies>E05–E06、E16；D2。读取 GetHistory 的完整保留列表，不使用 GetRecentHistory(...,30)。</dependencies>
    <forbidden_actions>保持历史去重、存储、压缩、Id 和游戏日期逻辑；本票无 LLM 调用，无 Game1 访问，无 ModData 写入。</forbidden_actions>
  </scope>
  <contract>
    <signatures>D2 的精确声明。GetHistoryNpcNames 在既有锁中返回字典键的 List&lt;string&gt; 副本。Create 为纯函数；返回全部 Rows 和采样 PromptLines 的不可变副本，所有新增字段为 Memory。</signatures>
    <boundaries><lifecycle>纯函数可在测试中执行；游戏调用在 SaveLoaded 后主线程采集。</lifecycle><threading>读取 GetHistory 和建立深快照在主线程同一同步阶段；后台只接收快照。</threading><harmony>N/A</harmony></boundaries>
  </contract>
  <implementation>
    <flow>
1. 从调用方提供的历史列表按既有写入顺序取目标年/季/日；过滤空文本、eavesdrop、session-end。忽略非持久的 IsConsumed，不读取 Guid Id。
2. RowKey 为 SHA256/UTF8/小写十六进制：规范 JSON 数组 [年,季的数值,日,timeOfDay,SpeakerType数值,DialogueType,SpeakerName,完整Text,UtcTimestampMs,GiftName,GiftTaste]。空字符串统一为空串；数值使用不变式表示，JSON 无缩进。InputFingerprint 是目标日、规范 NPC 名及有序 RowKey 数组的同样 hash。
3. Qualifies 精确为 SpeakerType.NPC 且非 gift。QualifyingCount 是所有过滤后 Rows 的合格条目总数；UncoveredQualifyingCount 用 coveredRowKeys 的多重集合扣减对应次数，重复文本不折叠成一个交流。
4. Rows 不超过当前历史保留范围，深复制标量字段。超过48行时：先取最早8、最近16，再从中间区按 floor(j*(n-1)/23)，j=0..23，选至多24个位置；去重后按原写入顺序排序。不足48行全选。
5. 每条 PromptLine 含游戏时刻、角色标签和至多96个 Unicode text element 的文本；标签限长32个 UTF-16 code unit。若全部格式化材料超过6000个 UTF-16 code unit，逐行缩短文本部分至能满足预算，保留行时刻/角色标签和所有选中行。截断附省略号计入预算；绝不拆 surrogate pair 或组合字符。
6. 任一采样或截断设置 InputTruncated。全部 RowKey 作为本次有限预算快照的“已评估范围”；这表示已按固定预算处理，并不证明每行均进入生成或被正文表达。
    </flow>
    <failure_paths>
npcName 空、targetDay 非正数、entries/coveredRowKeys 为 null：BUG → Warn → 返回 Rows/PromptLines 为空、计数为0的明确空快照；不触发生成。
过滤后无材料：RECOVERABLE → 调用方 Trace → 空快照。
资料超预算：RECOVERABLE → 调用方 Debug（每次发请求一次）→ 确定性采样快照，报告原行数/使用行数。
出现 null 历史条目：BUG → Warn → 忽略该条并保留其余材料。
    </failure_paths>
  </implementation>
  <verification>
    <criteria>Diff 锚点为纯快照构建与字典键副本。测试证明重新生成 Guid 不改变 fingerprint、序列化/重载保留字段得到相同 fingerprint；相同文本不同时间不被合并；礼物/偷听/结束标记统计正确；新增NPC行增大未覆盖计数；300条历史滚动移除早段后新行仍可计数；采样包含早/中/晚且严格满足预算；一基目标日首日=1、次年春1=113及跨季日期逆变换一致。过滤测试 DailyDistillationSnapshotTests 通过；master 真实 hash。</criteria>
  </verification>
  <extensions if-applicable="true"><execution_control><sequence>2</sequence><parallelizable_with>none</parallelizable_with><stop_conditions>需要修改历史格式、修复旧 Id 或访问 Game1 才能实现纯函数。</stop_conditions></execution_control><open_questions>无。</open_questions></extensions>
</ticket>
```

### DD403 — NPC 状态、所有权与恢复

```xml
<ticket id="DD403-LEDGER">
  <scope>
    <target_files>新建 H:\ValleytalkReborn\src\Memory\DailyDistillationState.cs；新建 H:\ValleytalkReborn\ValleytalkReborn.Tests\DailyDistillationStateTests.cs</target_files>
    <dependencies>D3 的 Ledger/StateStore 声明；E07 的 MemoryEntry；E14 的 NPC.modData；DD402.HashText。新 key 的唯一登记为声明清单 D3。</dependencies>
    <forbidden_actions>本票不提交正文、不发 LLM、不使用 player.modData 存储新 NPC 状态；不清除未知 schema 或静默修复所有权。</forbidden_actions>
  </scope>
  <contract>
    <signatures>D3 中 DailyDistillationLedger、DailyDistillationDayState、DailyPendingCommit、DailyDistillationStateStore 的全部声明。Days 使用 Dictionary&lt;int,DailyDistillationDayState&gt;；覆盖行保持 List&lt;string&gt;，保留重复次数。业务字段 ModData，反序列化副本 Memory。</signatures>
    <boundaries><lifecycle>SaveLoaded 全部 handler 完成后，并且 Context.IsWorldReady、Context.IsMainPlayer。</lifecycle><threading>NPC 读取、恢复和写入均主线程；测试只用无游戏实例的普通 DTO/JSON 与受控 NPC 对象，不访问世界。</threading><harmony>N/A</harmony></boundaries>
  </contract>
  <implementation>
    <flow>
1. TryRead：key 不存在 → 新 Version=1 ledger，true；存在 → 完整解析并校验版本1、Days非null、键=TargetDay、非负预算、Rows hash格式、PendingCommit必需字段。schema不满足 → false，原字符串保留。
2. TryWrite：在副本上完成校验/序列化，单次替换该 NPC 的 D3 key；失败返回false，调用方不发布副本。它不主动触发游戏保存。
3. Reconcile 每日期先处理 PendingCommit：目标日恰好1条Daily且指定EntryId和正文hash=NewContentHash → 恢复所有权、CoveredRowKeys及LastEvaluatedFingerprint；IsFinal则关闭最终整理为Committed；清除pending。目标日恰好1条且指定ID的正文hash=ExpectedOldHash，或首次创建且目标日没有卡片 → 清除pending，进度保持旧值。其余情况 → Suspended=true/RecoveryConflict，保留pending作为证据。次数在任何恢复分支不增加。
4. 无pending且有EntryId：未最终关闭时，条目缺失、hash变化或同日存在其他Daily条目 → 暂停，分别记录Deleted/Edited/MultipleEntries。已最终关闭且条目缺失 → 保留关闭状态，允许正常升档。已关闭但仍有条目且被编辑 → 保留玩家内容。
5. 无所有权且同日已有Daily条目 → 暂停UnownedEntry；不通过Source猜测它是否自动产生。
6. 回写恢复前后的变化一次；反复恢复同一状态得到相同结果。恢复本身不重发网络请求，不修改日记。
7. 回收由调度器提供当前日：保留今日和过去7日；更早条目先完成pending核对，未完成的状态记录Expired并暂停，再删除过期ledger项。保留原卡片，不补造旧日记。
    </flow>
    <failure_paths>
NPC为空、非主机或未加载：RECOVERABLE → Debug → TryRead/TryWrite=false，零写入。
畸形JSON、未知Version、负预算或字段矛盾：BUG → Error → TryRead=false；调度器该NPC本会话停止自动提炼，原key保持。
序列化或ModData赋值异常：BUG → Error → TryWrite=false，原权威与未提交进度保持，该NPC本会话停止自动提炼。
玩家编辑/删除/新增同日卡片：RECOVERABLE → Info → 日期持久Suspended，保留玩家数据。
prepared提交无法证明归属：RECOVERABLE → Warn → RecoveryConflict暂停，Supervisor检查journal和正文。
    </failure_paths>
  </implementation>
  <verification>
    <criteria>Diff 锚点仅新状态文件。校验重复Reconcile幂等；覆盖prepared未写、已写未ack、内容不匹配、手工编辑、删除、旧多卡片、未知schema、恢复不再计费。重载相同JSON得到相同次数/暂停状态；原畸形字符串未覆盖。过滤测试 DailyDistillationStateTests；master真实hash。</criteria>
  </verification>
  <extensions if-applicable="true"><execution_control><sequence>3</sequence><parallelizable_with>none</parallelizable_with><stop_conditions>需要将正文复制进ModData；需要把读档等同即时落盘；恢复时准备覆盖不匹配正文。</stop_conditions></execution_control><open_questions>无。</open_questions></extensions>
</ticket>
```

### DD404 — Daily 正文的比较提交

```xml
<ticket id="DD404-DAILY-COMMIT">
  <scope>
    <target_files>新建 H:\ValleytalkReborn\src\Memory\DailyTimelineCommit.cs；H:\ValleytalkReborn\src\Memory\MemoryManager.cs（仅新Daily入口）；新建 H:\ValleytalkReborn\ValleytalkReborn.Tests\DailyTimelineCommitTests.cs</target_files>
    <dependencies>D3 的 CommitRequest/Result/Status；E07 的现有时间线字典、日期换算、容量、SaveData key和MemoryEntry属性；DD402.HashText。</dependencies>
    <forbidden_actions>原 AddTimelineMemory、EditTimelineMemory、SaveTimeline 的签名和其他调用保持；本票不迁移全局存储、不扩容、不自动淘汰、不变更其他tier失败语义。</forbidden_actions>
  </scope>
  <contract>
    <signatures>internal DailyTimelineCommitResult CommitDailyTimeline(DailyTimelineCommitRequest request)。Result精确使用D3枚举。Request/Result是Memory；正文沿用既有SaveData权威；新入口只操作MemoryTier.Daily。</signatures>
    <boundaries><lifecycle>存档已加载且MemoryManager.IsLoaded，主机世界就绪。</lifecycle><threading>主线程同步比较、构建副本、WriteSaveData和发布；本票不新增后台文件I/O，不同步等待网络。</threading><harmony>N/A</harmony></boundaries>
  </contract>
  <implementation>
    <flow>
1. 参数完整性验证：NPC/EntryId非空、TargetDay正数、NewContent非空且不超过MaxMemoryLength；非法返回Invalid。世界/主机/Helper/加载状态不满足，或_loadFailed → Unavailable。
2. 首先识别重放：目标日唯一Daily的ID、tier和新正文hash与请求一致 → Unchanged；它优先于容量、重复正文和旧hash检查。其余创建分支：该NPC目标日Daily数量必须为0；ID必须未被任何该NPC时间线条目使用；Daily未满30；与既有Daily正文按现有忽略大小写规则不同。分别返回Conflict、CapacityFull或Duplicate。
3. 其余更新分支：目标日恰好1条Daily，ID匹配，正文hash匹配ExpectedContentHash；否则Conflict。与其他Daily重复 → Duplicate。
4. 从现有字典建立字典副本，目标NPC列表副本及被更新条目的属性副本。新建条目的Id使用request.EntryId；类型/类别/重要度/Source沿用现有Daily建立规则，日期和标签基于TargetDay。更新保留Id、CreatedDay、CreatedAt、DateLabel及其他属性，仅改Content。全程不改已发布列表。
5. 直接对既有TimelineSaveDataKey执行WriteSaveData副本；这里不调用吞掉异常的SaveTimeline。成功后才将副本发布到_timelineMemories，返回Applied和hash。失败返回StorageFailed；内存原值保持。此返回不承诺跨存储物理原子性。
6. 同一个create请求重复到达且同日唯一ID和正文hash都与请求一致 → Unchanged；不再次插入。其他同ID/异正文重放 → Conflict。
    </flow>
    <failure_paths>
内部请求非法：BUG → Error → Invalid，原状态保持。
存档未就绪/非主机：RECOVERABLE → Debug → Unavailable，零写入。
加载失败：BUG → Error → Unavailable，拒绝覆写。
内容/ID/日期比较冲突：RECOVERABLE → Info → Conflict，保留原条目。
容量已满或重复正文：RECOVERABLE → Info → CapacityFull/Duplicate，原状态保持。
SaveData序列化错误：BUG → Error → StorageFailed，未发布内存保持旧值。
SaveData I/O失败：BOUNDARY → Error → StorageFailed；准备journal保留供恢复，不把错误报告成成功。
    </failure_paths>
  </implementation>
  <verification>
    <criteria>Diff锚点为新入口，旧调用无签名变化。使用既有测试环境/受控IDataHelper验证成功后才发布、写入失败保留旧内容、重复create幂等、比较冲突、同日多卡片、满容量及更新保留日期/ID。若无现成受控Helper，测试本票内部确定性比较/副本步骤，SMAPI实测写失败由Supervisor完成；不为测试扩展生产公共API。过滤DailyTimelineCommitTests；master真实hash。</criteria>
  </verification>
  <extensions if-applicable="true"><execution_control><sequence>4</sequence><parallelizable_with>none</parallelizable_with><stop_conditions>无法观察WriteSaveData的失败；需要改变其他tier写入路径；副本包含对原条目的可变共享引用。</stop_conditions></execution_control><open_questions>无。</open_questions></extensions>
</ticket>
```

### DD404B — 自动升档的保源提交

```xml
<ticket id="DD404B-CONDENSATION-COMMIT">
  <scope>
    <target_files>新建 H:\ValleytalkReborn\src\Memory\TimelineCondensationCommit.cs；H:\ValleytalkReborn\src\Memory\MemoryManager.cs（新升档入口）；新建 H:\ValleytalkReborn\ValleytalkReborn.Tests\TimelineCondensationCommitTests.cs</target_files>
    <dependencies>D6全部声明；E07/E20的时间线/归档字典、SaveData keys、容量及日期；DD404副本写入方式。此缺口已通过MCP确认，不留给Worker决定。</dependencies>
    <forbidden_actions>原手动Add/Archive/Remove/Restore调用签名保持；不重写Prompt、不增加容量、不把归档副本共享给活跃条目、不安装事务框架。</forbidden_actions>
  </scope>
  <contract>
    <signatures>D6精确声明。请求及副本Memory；正文与归档仍使用现有两个SaveData key。源列表为IReadOnlyList&lt;string&gt;，ID/hash按同一索引对应。</signatures>
    <boundaries><lifecycle>主机存档加载完成、MemoryManager.IsLoaded且_loadFailed为false。</lifecycle><threading>全部比较/副本/两次WriteSaveData/发布主线程同步；无网络或后台游戏访问。</threading><harmony>N/A</harmony></boundaries>
  </contract>
  <implementation>
    <flow>
1. TargetTier只接受Weekly/Chronicle(Season同值)/Yearly；NPC、EntryId、目标日、正文完整，源ID/hash等长、源ID唯一且至少2条。正文按现有SmartTruncate至MaxMemoryLength后作为正式待写值，避免改变旧高层长度规则。
2. 首先判定重放：已存在该聚合EntryId且tier/目标日/正文相同、捕获源均已不在活跃列表→Unchanged。其余分支查找全部捕获源；逐项核对ID、正文hash及源tier（Daily→Weekly、Weekly→Chronicle、Chronicle→Yearly）。Weekly源日期在TargetDay-6..TargetDay；Chronicle同目标年/季；Yearly同目标年。任何不匹配→Conflict，零写入。
3. 同目标周期已有聚合、ID重复→Conflict；容量满或重复正文→CapacityFull/Duplicate。沿用PeriodAlreadyCovered的周期定义。
4. 构建归档字典副本和源条目的完整属性副本，副本设置同一个ArchivedAt和ArchiveReason="Distilled"；同ID且同正文的已有归档只保留一份，同ID异正文→Conflict。按既有30条归档容量截去最旧项，不修改活跃源。
5. 构建活跃时间线副本：加入固定EntryId聚合，删除且仅删除捕获源ID，保留新加入的其他源。新条目属性/标签按既有目标tier规则生成。
6. 直接WriteSaveData归档副本，确认调用成功后发布归档副本；失败即StorageFailed，零活跃列表写入。再以一次WriteSaveData写活跃副本（新聚合与移除源在同一payload）；成功才发布活跃副本并返回Applied。
7. 第二次写失败→StorageFailed，原活跃聚合/源保持；归档副本允许已存在作为安全备份，记录archivePrepared=true。再次尝试同源不重复归档。不声称两库物理原子；只承诺任何可观察失败均不通过单独删源继续执行。
8. 新入口不调用吞异常的SaveTimeline/SaveArchivedTimeline，也不在外层再调用RemoveTimelineMemories。明确失败结果由DD408消费。
    </flow>
    <failure_paths>
内部请求/并行列表不合法：BUG → Error → Invalid，零写入。
存档/主机不可用：RECOVERABLE → Debug → Unavailable；加载错误BUG → Error → Unavailable。
源变化/缺失/同周期或归档ID冲突：RECOVERABLE → Info → Conflict，源保持。
容量/重复正文：RECOVERABLE → Info → CapacityFull/Duplicate，源保持。
序列化故障：BUG → Error → StorageFailed；IO故障：BOUNDARY → Error → StorageFailed。日志区分归档阶段/活跃阶段，第二阶段失败明确保留活跃源。
    </failure_paths>
  </implementation>
  <verification>
    <criteria>Diff锚点新提交入口。受控存储验证第一次写失败不写活跃库、第二次失败保留全部活跃源、成功payload同时新增聚合并移除捕获源、生成期间新增源保留、hash冲突零写入、重放不产生重复归档/聚合。测试归档副本不修改原条目的ArchivedAt/ArchiveReason。过滤TimelineCondensationCommitTests通过；master真实hash。</criteria>
  </verification>
  <extensions if-applicable="true"><execution_control><sequence>5</sequence><parallelizable_with>none</parallelizable_with><stop_conditions>需要沿用吞异常写入口；副本写失败后删除源；需要扩展到手动归档恢复流程。</stop_conditions></execution_control><open_questions>无。</open_questions></extensions>
</ticket>
```

### DD405 — 日记 Prompt 与严格输出

```xml
<ticket id="DD405-GENERATION">
  <scope>
    <target_files>新建 H:\ValleytalkReborn\src\Memory\DailyDistillationRequest.cs；H:\ValleytalkReborn\src\Memory\MemoryExtractService.cs（新增独立Daily路径）；新建 H:\ValleytalkReborn\ValleytalkReborn.Tests\DailyDistillationPromptTests.cs</target_files>
    <dependencies>D4；E08/E10/E15的MemoryExtractResult、RunInferenceAsync；DD402快照。BuildPersonaSlice仅由主线程调用方预先执行。</dependencies>
    <forbidden_actions>既有提炼/浓缩入口及Prompt保持；新路径不用旧宽松ParseAndCollectResult，不在后台读取Config、I18n、Constants.SaveFolderName、Game1或NPC，不增加Provider重试/Provider实现。</forbidden_actions>
  </scope>
  <contract>
    <signatures>D4全部精确声明；BuildDailyPrompts与ParseDailyResult为纯函数。请求与结果均Memory；Provider是主线程捕获的既有服务引用。后台调用的是E10可取消签名，responseStart="["，n_predict=256，cacheContext沿用既有NoTools，allowRetry=false。</signatures>
    <boundaries><lifecycle>主线程SaveLoaded后捕获请求；生成阶段只消费快照。</lifecycle><threading>异步await网络，完成后由调用方投递MainThreadActionQueue；服务不写游戏状态。</threading><harmony>N/A</harmony></boundaries>
  </contract>
  <implementation>
    <flow>
1. 验证Provider、名称、Snapshot、TimeoutSeconds均有效；Rows为空返回NoHistory。使用captured语言和日期生成Prompt，人设切片/旧日记/所选行放入明确资料段。
2. 采用下方正向Prompt contract。首次材料只形成礼貌路过且没有可落笔的持续印象时，JSON [] 是Empty；已有日记时产出一条延续或演化后的文本，[]在该分支为无效响应。相同正文允许Success，由提交阶段判定Unchanged。
3. 链接传入取消令牌和request.TimeoutSeconds（合法范围15–120）；调用捕获Provider.RunInferenceAsync。保留真实Provider Task并await其结束；不以WaitAsync弃等后假装释放占用。超时令牌已触发时，迟到文本不接受。
4. Provider忽略取消时记录Debug一次，继续异步观察底层Task；该调用的返回代表底层Task已结束。传入ct取消→Cancelled；只有超时→Failed/ErrorDetail=timeout。Provider取消响应同样按令牌原因分类。
5. ParseDailyResult严格解析整个trim后的JSON数组。非数组、附加解释、非字符串、多于1条均Failed；[]→Empty。唯一字符串trim后必须非空、单行、按SanitizeForStorage清洗前后相同。
6. 全部正文同时满足现有MaxMemoryLength的120个UTF-16 code unit上限。中文另限30个Unicode text element且含“我”；英文另限18个空白分词且有I/me/my/mine的独立词或I'm/I’ve等第一人称缩写。英语边界忽略大小写；中文以文字“我”检查。名称检查对中文显示名采用完整子串，对ASCII代码名/显示名采用完整词边界；空名称不参与。
7. 验证失败返回Failed与具体ErrorDetail，无候选；成功Candidates恰好1条。结构检查不宣称证明人物视角或事实忠实，Supervisor依据Prompt契约复核样本。
    </flow>
    <failure_paths>
请求内部字段非法：BUG → Error → Failed/ErrorDetail以BUG:开头；调度器暂停该日期并升级。
用户取消、跨日或切档取消：RECOVERABLE → Debug → Cancelled；保持原正文，预算已消耗。
超时：RECOVERABLE → Debug → Failed/timeout；等待底层结束后按剩余预算重试。
网络/Provider故障、空响应或响应格式/长度不符：BOUNDARY → Warn → Failed，保留旧正文。
调用/解析内部逻辑异常：BUG → Error → Failed/BUG:详情；调度器不重试并升级。
初次交流无持续印象：RECOVERABLE → Debug → Empty；调用方记录已评估快照，等待新材料。
    </failure_paths>
  </implementation>
  <verification>
    <criteria>Diff锚点新Daily独立入口。测试正向Prompt四字段、中英文、目标昨日日期、旧日记+原始材料定位；严格拒绝多条/解释/控制标签/超长/姓名/缺乏第一人称标记；[]与相同正文语义。受控Provider分别覆盖支持取消与忽略取消，后者Task结束前GenerateDailyAsync不得完成；真实网络无测试调用。过滤DailyDistillationPromptTests；master真实hash。</criteria>
  </verification>
  <extensions if-applicable="true">
    <prompt_contract>
Perspective anchor：你是该NPC，反思明确TargetDateLabel那一天与农夫交谈后留下的感受，以“我”的声音表达；最终整理意味着该日交流已结束。
Information domain：事实来自本轮选中并标有时刻的对话；早前日记是已经形成的印象，人设是口吻参考。采样标志明确材料是保留历史的预算内选段。
Register and audience：私人简短日记，符合角色口吻；另一方称“农夫”或“the farmer”。有早前日记时把新的触动融入同一条余韵；只有礼貌路过的首次材料以[]表达尚未形成印象。
Verifiable boundary：[]仅适用于首次无印象分支；其余为包含恰好1个非空单行字符串的完整JSON数组。全部≤120个UTF-16 code unit，中文另限≤30个Unicode text element，英文另限≤18词；满足程序规定的第一人称标记与姓名检查；人工样本复核事实锚点和口吻。
资料段正向定位：对话资料用于取事实，旧日记用于延续感受，人设用于定调，任务段定义输出格式。
    </prompt_contract>
    <execution_control><sequence>6</sequence><parallelizable_with>none</parallelizable_with><stop_conditions>后台调用BuildPersonaSlice；Provider真实Task尚未结束就报告可启动下一请求；需要扩大到Provider底层改造。</stop_conditions></execution_control><open_questions>无。</open_questions>
  </extensions>
</ticket>
```

### DD406 — 主线程候选规划与请求准入

```xml
<ticket id="DD406-ADMISSION">
  <scope>
    <target_files>H:\ValleytalkReborn\src\Memory\TimelineAutoSummaryScheduler.cs（Daily候选与准入辅助代码、字段）；新建 H:\ValleytalkReborn\ValleytalkReborn.Tests\DailyDistillationAdmissionTests.cs</target_files>
    <dependencies>D5的ProbeDailyWork、CanAdmitDailyWork、集合与任务字段；DD401–DD405；E09/E11/E12/E18。</dependencies>
    <forbidden_actions>本票不启用新事件路径、不发送网络、不修改MenuChanged、不改变高层浓缩；新辅助代码在DD408才接入事件。</forbidden_actions>
  </scope>
  <contract>
    <signatures>D5候选/准入方法和Memory集合。统一tuple键为NPC代码名.ToUpperInvariant()+TargetDay；不使用显示名作为身份。任务唯一描述为NPC、日期、Daily类型，不把旧正文长期固定在排队描述中。</signatures>
    <boundaries><lifecycle>完整SaveLoaded后的一秒事件；MemoryManager已加载。</lifecycle><threading>规划、NPC读写、persona采集与预算预留均主线程；本票无异步状态突变。</threading><harmony>N/A</harmony></boundaries>
  </contract>
  <implementation>
    <flow>
1. 每5个一秒事件取得GetHistoryNpcNames与主机friendshipData.Keys的去重并集，逐NPC解析对象和ledger，Reconcile；NPC不存在暂跳过。保存恢复变化；解析失败NPC加入本会话blocked集合。候选日下界为max(1,当前一基日历日-7)，首个游戏日前的日期不调用快照构建。
2. 仅单人/主机、EnableMod开启、MemoryManager已加载时规划。Disabled→不规划Daily；Overnight→只规划过去日；Intraday→规划今日及最终整理。
3. 今日候选：日期未暂停；InputFingerprint与LastEvaluatedFingerprint不同；有未覆盖合格NPC行；无EntryId时QualifyingCount≥阈值，有EntryId时UncoveredQualifyingCount≥阈值；IntradayAttempts低于当前上限。其他条件不满足不入队。
4. 最终候选：昨天历史即使没有ledger也检查；另检查ledger中过去7日未关闭日期。无卡片首次要求QualifyingCount≥阈值；已有自动卡片只要求UncoveredQualifyingCount>0。FinalAttempts&lt;2，且未暂停/未关闭时入队。
5. 过去日期无未覆盖NPC行且有自动卡片→关闭Unchanged；没有卡片且不足阈值→关闭BelowThreshold；当前fingerprint已Empty评估且无新合格行→关闭Empty；暂停→关闭SkippedManual；最终预算耗尽→关闭FailedBudget，保留CoveredRowKeys原值。关闭均先TryWrite；失败不作为已settled。
6. _dailyPending按唯一键合并，重复探测不累积任务；运行中的键不再入第二个运行任务。新资料留待下一轮规划。排序：过去日期最终整理在前（日期升序），今日在后；同日按既有配偶/心数排序，最后NPC代码名排序。
7. CanAdmit：主机世界就绪、模组开启、Daily模式允许、LlmDisabled不为true、无activeClickableMenu、无dialogueUp、无eventUp、AsyncBuilder既不AwaitingGeneration也不IsGeneratingDialogue、PendingChoiceStore无待挂载载荷、自动调度器无运行任务/底层drain，冷却为0。任一交互条件不满足→闲置归0；连续满足10个一秒事件才准入。
8. 准入时再次读取ledger和快照，重新核验所有权/阈值/预算。若条件已变化撤销或替换候选，零扣费。生成之前的实际扣费由DD407执行；初次Empty已覆盖的行不反复计入阈值。
    </flow>
    <failure_paths>
NPC不存在/世界未就绪/交互仍活跃/Provider禁用：RECOVERABLE → Trace（状态变化一次）→ 延后，零请求、零扣费。
ledger不可读或不可写：BUG → Error → NPC本会话blocked，零网络。
未达阈值：RECOVERABLE → Trace → 等待新材料；过去日期明确BelowThreshold。
预算耗尽：RECOVERABLE → Info（每NPC/日期一次）→ 今日等待最终整理；过去日期FailedBudget关闭。
找不到未关闭旧日资料：RECOVERABLE → Warn → NoHistory关闭，保留原卡片；不编造日记。
    </failure_paths>
  </implementation>
  <verification>
    <criteria>Diff锚点新规划辅助方法，既有事件入口暂不变。受控输入验证三档、统一NPC统计、阈值、首发/更新共享预算、最后不足阈值尾段仍最终更新、只有玩家发言不触发、重复探测唯一任务、自定义选项/生成/事件均阻止准入。使用受控普通输入验证判断，不修改生产公共API以测试Game1；UI条件由最终SMAPI矩阵验证。过滤DailyDistillationAdmissionTests；master真实hash。</criteria>
  </verification>
  <extensions if-applicable="true"><execution_control><sequence>7</sequence><parallelizable_with>none</parallelizable_with><stop_conditions>需要把关框当作唯一业务边界；准入阶段就推进CoveredRowKeys；需要本票启用网络或事件。</stop_conditions></execution_control><open_questions>无。</open_questions></extensions>
</ticket>
```

### DD407 — 请求执行、epoch 与恢复式提交

```xml
<ticket id="DD407-EXECUTION-COMMIT">
  <scope>
    <target_files>H:\ValleytalkReborn\src\Memory\TimelineAutoSummaryScheduler.cs（StartDailyTask、CompleteDailyTask、InvalidateSaveSession及运行状态）；新建 H:\ValleytalkReborn\ValleytalkReborn.Tests\DailyDistillationExecutionTests.cs</target_files>
    <dependencies>D5相应精确签名；DD403–DD406；E08/E10/E13；既有BuildPersonaSlice、GenerateDateLabel只在主线程使用；本类已有DateToDayNumber(date)+1计算统一TargetDay，CurrentGameDay为private，外部不调用。</dependencies>
    <forbidden_actions>旧日报尚不在本票替换，DD408才启用；不清除全局MainThreadActionQueue，不改Provider、不创建Harmony、不增加统一后台执行框架。</forbidden_actions>
  </scope>
  <contract>
    <signatures>D5执行/完成/失效声明。配置epoch覆盖模式、阈值、预算、超时、语言及Provider实例变化；存档epoch只在新的SaveLoaded、返回标题、Cleanup产生失效。所有集合/标志/冷却是Memory，由主线程写；账本字段是ModData。</signatures>
    <boundaries><lifecycle>世界加载完整后准入；过期任务在任何阶段均无写权限。</lifecycle><threading>主线程预留和捕获→后台GenerateDailyAsync→EnqueueMainThread→主线程验证和提交。后台finally不修改_isProcessing、队列或冷却。</threading><harmony>N/A</harmony></boundaries>
  </contract>
  <implementation>
    <flow>
1. StartDailyTask按DD406再次验证，并在扣费前完成下面的同步捕获和请求完整性校验；准备失败零扣费且升级。然后复制ledger，实际调用Provider前IntradayAttempts或FinalAttempts加1，TryWrite成功后才发起。一次调用只预留一次；调用后取消或失败不退次数。
2. 同步捕获名称、显示名、目标日期标签、语言、persona、Provider引用、超时、完整快照和当前正文hash。首次创建分配固定Guid字符串作为NewEntryId。记录存档/配置epoch及IsFinalDaily；捕获发生在步骤1的预留之前，预留后设置运行占用并立即异步执行。
3. _dailyTransportDrain引用实际GenerateDailyAsync Task。任务返回后无论成功/失败，都以捕获task身份投递完成回调。观察底层异常并记录；即使切档仍观察，不把旧回调释放新存档任务占用。
4. InvalidateSaveSession递增epoch、取消sessionCts、清理存档队列/运行键/闲置计数；保留未完成drain句柄。生成新Cts在世界加载后使用。旧回调看到epoch不匹配直接丢弃业务结果；只在句柄身份相同时清除已经结束的drain。新存档不因旧finally复位运行标志。
5. CompleteDailyTask先验证epoch、当前主机/世界/EnableMod、模式资格和配置epoch。今日任务若目标日不等于当前日，丢弃并待最终规划；最终任务只允许当前日前7日。若玩家重新进入交互，把已生成结果保留在_dailyCompletions，后续一秒事件空闲时消费；不在同一个ProcessMainThreadQueue循环里递归入队，不再次生成/计费，消费时重验epoch。
6. 非Success：Failed/Cancelled保留CoveredRowKeys及原正文，按剩余次数重规划；BUG:标记日期暂停并升级。初次Empty则一次TryWrite记录全部快照Rows为已评估范围和LastEvaluatedFingerprint；最终分支关闭Empty。不写卡片/HUD。
7. Success先核对卡片所有权，再持久化PendingCommit（包含固定EntryId、old/new hash、input fingerprint、CoveredRowKeys、IsFinal）。TryWrite失败则本会话block，不调用正文提交。
8. 调用CommitDailyTimeline。Applied/Unchanged→在副本ledger设置EntryId/hash、快照全部CoveredRowKeys、LastEvaluatedFingerprint，最终分支关闭Committed/Unchanged，清pending后TryWrite。ack失败保留准备journal，block直到恢复。Conflict→暂停Edited/MultipleEntries；CapacityFull/Duplicate→标记该fingerprint本会话blocked，最终关闭CapacityFull/Duplicate，正文保持；StorageFailed→保留journal并暂停本会话，等待Supervisor或读档恢复；Unavailable→不ack，待恢复；Invalid→BUG暂停升级。
9. 成功ack后Applied才弹HUD（日间首发/更新或最终更新，按捕获语言），日志[DailyDistill] Committed/Updated/Finalized；Unchanged只记录日志且推进已评估进度，不新增卡片、不弹HUD。预算不再增加。
10. 当前任务有效且真实底层已结束时，释放对应运行键/_isProcessing并设置35个一秒事件冷却。即使格式失败也冷却；未结束drain继续阻止任何自动请求。
    </flow>
    <failure_paths>
存档/配置epoch不匹配、跨日、取消：RECOVERABLE → Debug → 丢弃结果、零正文写入，已预留次数保持。
玩家内容或ID变更：RECOVERABLE → Info → 持久Suspended；零覆盖。
请求超时/Provider失败/输出边界失败：沿用DD405类别及日志 → 原正文与进度保持；仅剩余预算允许重试。
prepared/ack状态写入失败：BUG → Error → block，保留journal/旧进度供恢复；无成功HUD。
正文存储失败：BOUNDARY或BUG按DD404 → Error → 原内存保持、journal保留、本会话停止该日期自动请求。
内部状态违反唯一运行任务或epoch约束：BUG → Error → 明确暂停状态，Supervisor升级；不使用catch-all成功回落。
    </failure_paths>
  </implementation>
  <verification>
    <criteria>Diff锚点预算预留、主线程完成区及epoch。验证入队不扣费、实际请求一次扣费、失败不推进覆盖、取消不退费；同档重载epoch更新；同名存档重载与A→B→A均拒绝旧结果；旧任务回调不释放新任务标志；prepared后存储失败、成功写正文但ack失败的恢复；玩家编辑冲突；Unchanged幂等。过滤DailyDistillationExecutionTests；SMAPI观察请求次数与journal、日志/HUD一致；master真实hash。</criteria>
  </verification>
  <extensions if-applicable="true"><execution_control><sequence>8</sequence><parallelizable_with>none</parallelizable_with><stop_conditions>后台直接改任何游戏状态或调度集合；不能识别旧回调；未ack却记录成功HUD；单票需要扩大Provider实现。</stop_conditions></execution_control><open_questions>无。</open_questions></extensions>
</ticket>
```

### DD408 — 生命周期启用、次日依赖与最终验收

```xml
<ticket id="DD408-LIFECYCLE-HIERARCHY">
  <scope>
    <target_files>H:\ValleytalkReborn\src\Memory\TimelineAutoSummaryScheduler.cs；H:\ValleytalkReborn\src\Core\ModEntry.cs（返回标题的早期失效）；新建 H:\ValleytalkReborn\ValleytalkReborn.Tests\DailyDistillationHierarchyTests.cs</target_files>
    <dependencies>DD401–DD407及DD404B；D5的HasUnsettledDailyDependency/源快照字段/_higherPendingKeys；E04/E09/E13/E20。既有valleytalk.autosummary-lastscan仅保留扫描提示，不作为任务提交证明。</dependencies>
    <forbidden_actions>MenuChanged与Harmony保持；不重写周/季/年Prompt、容量或UI；不删除非捕获源，不把日哨兵视为Daily已完成。</forbidden_actions>
  </scope>
  <contract>
    <signatures>保持E09既有Initialize、Cleanup、OnDayStarted、OnSaveLoaded、OnOneSecondUpdateTicked、ProcessNextTaskAsync、CompleteTask、TryScanAndEnqueue签名。新增D5方法。高层队列与捕获源ID/hash列表为Memory；日哨兵继续player ModData；新增Daily权威仍NPC ModData。</signatures>
    <boundaries><lifecycle>Initialize只订阅；OnSaveLoaded失效会话并设置_needsRebuild；在完整事件结束后的首个世界就绪且MemoryManager.IsLoaded一秒事件重建。OnDayStarted在主线程使日间旧任务失效并规划最终整理。返回标题在ModEntry.Cleanup之前先InvalidateSaveSession。</lifecycle><threading>一秒事件及完成区主线程；LLM await续体仅投递回调。既有高层_isProcessing/finally也调整为主线程完成回调复位。</threading><harmony>N/A；不修改既有Harmony补丁。</harmony></boundaries>
  </contract>
  <implementation>
    <flow>
1. 将DD406/407接入现有事件。SaveLoaded handler不立即扫描或写ledger；首个安全一秒事件重建，其他订阅者已完成History.Load/MemoryManager.Load。新存档必定核对，不因日哨兵相同跳过。
2. 替换旧Daily扫描和ExtractAsync调用为新的规划/请求/提交路径；Daily不通过PeriodAlreadyCovered的“已有卡片就跳过”守卫。旧周/季/年继续用该守卫。
3. 日间运行任务跨天取消并使对应旧日期日间完成失效；保留旧日ledger/attempts，过去日期重新规划为最终整理。切档/重载清Memory候选并从ModData重建；同档已保存的尝试次数不复位。尚未结束的drain继续阻止自动请求。
4. 每秒更新交互空闲条件，每5秒探测；35秒冷却与10秒连续空闲分别判断。所有自动任务发起前检查无运行任务/无drain，避免高层与Daily并发；高层入场同样使用交互空闲守卫。
5. 自动任务优先级为Daily最终整理、Weekly、Season、Yearly、今日Daily；同类沿用主机亲密度排序。高层候选用规范NPC/类型/目标日去重。重新读档时重新发现当天周期任务并由PeriodAlreadyCovered去重，扫描哨兵不阻止恢复丢失的Memory队列。
6. Weekly先检查其7日窗口中该NPC的最终整理依赖，任何待排队/执行/未完成ack都延后。Season等待同NPC相关Weekly候选结束；Yearly等待相关Season结束。生成失败且预算耗尽的Daily闭合为FailedBudget，明确允许使用保留的旧正文继续升档；日志写“最终整理未完成，使用已保留源”。Daily关闭不等于正文已被完整更新。
7. 模式Disabled时Daily依赖在Memory中视为SkippedDisabled，允许高层沿用现有卡片；不持久写FinalizationClosed，未来启用仍可规划。LLM整体禁用时暂停全部生成、保留候选；容量/手动冲突等最终终止状态释放依赖并记录原因。
8. 高层任务开始时主线程捕获ResolveSourceEntities返回的实际源ID和内容hash，分配固定NewEntryId，并用这些源内容生成；TargetDay统一为DateToDayNumber(TargetDate)+1。少于2条只在依赖全部闭合后判定终止。完成时复查epoch、目标周期及每个捕获源ID/hash。任一源消失或变化→保留全部源、丢弃该生成并撤销本次候选；Trace/Info记录，等待下一次发现，不写错误聚合。
9. 高层完成用DD404B.CommitTimelineCondensation取代旧Add→Archive→Remove调用链；传固定NewEntryId、捕获源ID/hash、正文和目标日期/tier。只有Applied产生成功日志/HUD；Unchanged仅日志。Conflict撤销结果并待重新发现；CapacityFull/Duplicate保留源并结束该周期本会话尝试；StorageFailed/Invalid本会话block该NPC周期并升级。任何分支均不再另调用RemoveTimelineMemories。
10. 清理/标题/配置开关变化具有幂等失效效果；不同handler重复调用不破坏drain或新的epoch。整个完成/冷却释放在主线程。日志必须带NPC代码名、TargetDay、epoch、attempt类型/次数、结果与覆盖fingerprint前缀，不输出完整对话资料。
    </flow>
    <failure_paths>
存档依赖尚未加载：RECOVERABLE → Trace → 延后扫描、零写入。
日/高层依赖仍在途或pending ack：RECOVERABLE → Trace（变化时一次）→ 延后该高层任务。
日记最终预算耗尽/手动保护/容量满：RECOVERABLE → Warn或Info（预算耗尽Warn，其余Info）→ 记录终止原因，允许高层使用保留正文。
源ID/hash变化：RECOVERABLE → Info → 撤销本次生成、保留所有源；不删除完成时新解析出来的条目。
高层存储记录失败：BOUNDARY（I/O）或BUG（schema/逻辑）→ Error → 停止删源并升级；现有内部错误不能被忽略为验收成功。
主线程完成区异常：BUG → Error → 明确失败并保留源；按任务身份释放占用，Supervisor升级。
    </failure_paths>
  </implementation>
  <verification>
    <criteria>
Diff锚点是事件延后重建、新Daily路由、依赖顺序与捕获源核对。执行所有新增DailyDistillation测试、dotnet build src/ValleytalkReborn.csproj，以及现有测试项目；只修本轮造成的失败，既有失败交Supervisor分类。
SMAPI矩阵：三档；旧true/false配置；自定义选项换壳后继续聊；直接退出/沉默结束；持续连聊期间无自动入场；首发+更新ID不变；日间预算耗尽后晚间尾段次日更新；未达阈值无卡片；手动编辑/删除/多卡片保护；30条满容量保留；超时与格式失败；Provider忽略取消无重叠自动请求；跨天/同档重载/跨档旧结果失效；保存后重载预算保持；prepared恢复；周一/季初/年初最终日记先于升档；生成期间源手动修改/新增后均不误删。
日志与NPC ModData应能解释每个skip/attempt/commit/final outcome；只成功ack且Applied产生HUD。正常重载不新增同日卡片、不重复消费已保存请求。记录最后master hash及各前置票hash，附实测日志片段；无法进行SMAPI实测时明确“尚未实测”，不能关闭功能验收。
    </criteria>
  </verification>
  <extensions if-applicable="true"><execution_control><sequence>9</sequence><parallelizable_with>none</parallelizable_with><stop_conditions>新DD404B提交入口仍吞存储失败；出现主线程等待网络、跨档写入、同日自动重复卡片；需要把单票扩大到手动存储路径重构。</stop_conditions></execution_control><open_questions>无。</open_questions></extensions>
</ticket>
```

## 已决升级规则

任何票遇到stop_conditions，Supervisor提供本会话新MCP证据、票号及违反的契约行；Architect以escalation_answer给出决定与contract_delta。Worker不扩票、不以吞异常或删源抑制症状。
已确认的旧高层吞存储错误由DD404B解决，DD408明确改用新入口；这是已决定的范围，不留给Worker再选择。

## Supervisor 派发与验收记录

| 工单 | Worker提交hash（master） | 自动检查 | SMAPI/存档证据 | 状态 |
| --- | --- | --- | --- | --- |
| DD401 | 待执行 | 待执行 | 待执行 | 未派发 |
| DD402 | 待执行 | 待执行 | 待执行 | 未派发 |
| DD403 | 待执行 | 待执行 | 待执行 | 未派发 |
| DD404 | 待执行 | 待执行 | 待执行 | 未派发 |
| DD404B | 待执行 | 待执行 | 待执行 | 未派发 |
| DD405 | 待执行 | 待执行 | 待执行 | 未派发 |
| DD406 | 待执行 | 待执行 | 待执行 | 未派发 |
| DD407 | 待执行 | 待执行 | 待执行 | 未派发 |
| DD408 | 待执行 | 待执行 | 待执行 | 未派发 |
