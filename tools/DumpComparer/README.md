# DumpComparer — VT3-C1 离线比对工具

`compare.py` 是 VT3-C1 的 G2 门机械执行器。输入两组 dump 目录，输出 PASS/FAIL + 差异锚点。
不接入生产构建；仅依赖 Python 3 标准库。

## 输入格式

每组 dump 目录结构（与 `vt_dump_topology` 产出一致）：

```
dumps/
├── run1/
│   ├── manifest.json      # 可选：{"branches":["Normal","StoodUp","Date","Greeting"]}
│   ├── Normal.txt
│   ├── StoodUp.txt
│   ├── Date.txt
│   └── Greeting.txt
└── run2/
    └── (同上)
```

分支文件命名支持：`<branch>.txt` / `<branch>.dump` / `<branch>.md` / `<branch>/full.txt`。

## 模式 A — 噪声基线

同 commit 两次运行比对，建立"实际变化行分类"（预期随机项：Preoccupation 50% roll、gossip 候选、每日关系抽选、情绪快照）。

```bash
python compare.py --mode A \
  --run1 ./dumps/run1 --run2 ./dumps/run2 \
  --out ./baseline.json
```

输出：各分支的 `random_blocks` 与 `structural_blocks` 计数 + 详情。

## 模式 B — 门判定

pre-C1 vs post-C1 比对。差异 ⊆ 噪声基线 = PASS；任何超出 = FAIL + 差异块锚点。

```bash
python compare.py --mode B \
  --pre ./dumps/pre --post ./dumps/post \
  --baseline ./baseline.json \
  --out ./gate.json
```

退出码：PASS=0，FAIL=1（便于 CI 集成）。

## 变化行分类

启发式分类（辅助，以 R1vsR2 实测为准）：

| 类别 | 匹配 |
|---|---|
| random | `preoccupation`、`pending_thought`、`gossip`、`biographyRelationships`、`情绪`/`emotion`、关系 heading 行 |
| structural | 其余所有变化 |

## 注意事项

- 本工具仅做机械比对与分类，不解释差异语义。
- 模式 B 的 PASS 结论需结合模式 A 的噪声基线解读。
- 四分支夹具（Normal/StoodUp/Date/Greeting）与 `PromptTopologyDumper` 一致。
