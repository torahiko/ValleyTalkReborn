#!/usr/bin/env python3
"""
DumpComparer — VT3-C1 离线比对工具（G2 门机械执行器）。

输入：两组 dump 目录（每组含 4 分支文件 + manifest.json）。
输出：门判定（PASS/FAIL）+ 差异块锚点。

模式 A（噪声基线）：同 commit 两次运行比对 → 产出"实际变化行分类"。
模式 B（门判定）：pre-C1 vs post-C1 比对 → 差异 ⊆ 噪声基线 = PASS；超出 = FAIL + 锚点。

不接入生产构建；独立 Python 脚本（仅使用标准库 + 可选 yaml）。

用法：
    python compare.py --mode A --run1 ./dumps/run1 --run2 ./dumps/run2 --out ./baseline.json
    python compare.py --mode B --pre ./dumps/pre --post ./dumps/post --baseline ./baseline.json --out ./gate.json
"""

import argparse
import json
import os
import re
import sys
from pathlib import Path
from typing import Dict, List, Set, Tuple

# 分支夹具（与 PromptTopologyDumper 四分支一致）
DEFAULT_BRANCHES = ["Normal", "StoodUp", "Date", "Greeting"]

# 随机项启发式（噪声基线候选）— 以 R1vsR2 实测为准，此处仅作分类辅助
RANDOM_HEURISTICS = [
    # Preoccupation 50% roll（出席/缺席整块变化）
    re.compile(r"preoccupation", re.IGNORECASE),
    re.compile(r"pending_thought", re.IGNORECASE),
    # gossip / 每日关系 / 情绪快照（整段可能出现/消失）
    re.compile(r"gossip", re.IGNORECASE),
    re.compile(r"biographyRelationships", re.IGNORECASE),
    re.compile(r"情绪|emotion", re.IGNORECASE),
    # 关系抽选 heading（每日轮换）
    re.compile(r"\*\*[^*]+\*\*:[^*]+\n", re.IGNORECASE),
]


def load_manifest(dump_dir: Path) -> List[str]:
    """读 manifest.json 获取分支列表；缺则用 DEFAULT_BRANCHES。"""
    mf = dump_dir / "manifest.json"
    if mf.exists():
        with open(mf, "r", encoding="utf-8") as f:
            data = json.load(f)
        branches = data.get("branches") or data.get("routes") or []
        if branches:
            return list(branches)
    return list(DEFAULT_BRANCHES)


def branch_files(dump_dir: Path, branches: List[str]) -> Dict[str, Path]:
    """定位每个分支的 dump 文件。支持 <branch>.txt / <branch>.dump / <branch>/full.txt。"""
    found = {}
    for b in branches:
        candidates = [
            dump_dir / f"{b}.txt",
            dump_dir / f"{b}.dump",
            dump_dir / f"{b}.md",
            dump_dir / b / "full.txt",
            dump_dir / b / "dump.txt",
        ]
        for c in candidates:
            if c.exists():
                found[b] = c
                break
    return found


def read_lines(path: Path) -> List[str]:
    with open(path, "r", encoding="utf-8") as f:
        return f.readlines()


def classify_line(line: str) -> str:
    """对单行做随机项启发式分类；返回类别标签或 'structural'。"""
    for pat in RANDOM_HEURISTICS:
        if pat.search(line):
            return "random"
    return "structural"


def diff_branch(old_lines: List[str], new_lines: List[str]) -> List[Dict]:
    """简易行级 diff（基于 unified diff 语义），返回变更块列表。"""
    import difflib
    blocks = []
    matcher = difflib.SequenceMatcher(None, old_lines, new_lines)
    for tag, i1, i2, j1, j2 in matcher.get_opcodes():
        if tag == "equal":
            continue
        block = {
            "tag": tag,
            "old_start": i1 + 1,
            "old_end": i2,
            "new_start": j1 + 1,
            "new_end": j2,
            "old_lines": [l.rstrip("\n") for l in old_lines[i1:i2]],
            "new_lines": [l.rstrip("\n") for l in new_lines[j1:j2]],
        }
        blocks.append(block)
    return blocks


def classify_blocks(blocks: List[Dict]) -> Tuple[List[Dict], List[Dict]]:
    """将变更块分为 random / structural。"""
    random_blocks, structural_blocks = [], []
    for b in blocks:
        # 取 new_lines 做分类（新增/变更的内容）
        sample = "\n".join(b["new_lines"] if b["tag"] != "delete" else b["old_lines"])
        b["classification"] = classify_line(sample)
        if b["classification"] == "random":
            random_blocks.append(b)
        else:
            structural_blocks.append(b)
    return random_blocks, structural_blocks


def compare_runs(dir_a: Path, dir_b: Path) -> Dict:
    """比对两组 dump，按分支输出变更块分类。"""
    branches_a = load_manifest(dir_a) if (dir_a / "manifest.json").exists() else DEFAULT_BRANCHES
    files_a = branch_files(dir_a, branches_a)
    branches_b = load_manifest(dir_b) if (dir_b / "manifest.json").exists() else DEFAULT_BRANCHES
    files_b = branch_files(dir_b, branches_b)

    common = sorted(set(files_a) & set(files_b))
    only_a = sorted(set(files_a) - set(files_b))
    only_b = sorted(set(files_b) - set(files_a))

    result = {
        "dir_a": str(dir_a),
        "dir_b": str(dir_b),
        "branches_compared": common,
        "only_in_a": only_a,
        "only_in_b": only_b,
        "branches": {},
    }

    for b in common:
        lines_a = read_lines(files_a[b])
        lines_b = read_lines(files_b[b])
        blocks = diff_branch(lines_a, lines_b)
        rand, struct = classify_blocks(blocks)
        result["branches"][b] = {
            "total_change_blocks": len(blocks),
            "random_blocks": len(rand),
            "structural_blocks": len(struct),
            "random_details": rand[:20],   # 截断防膨胀
            "structural_details": struct[:20],
        }

    return result


def load_baseline(path: Path) -> Dict:
    with open(path, "r", encoding="utf-8") as f:
        return json.load(f)


def structural_change_keys(baseline: Dict) -> Set[str]:
    """从基线提取已知的结构性变化键（模式 A 应几乎全为 random；结构性变化需关注）。"""
    keys = set()
    for b, info in baseline.get("branches", {}).items():
        for d in info.get("structural_details", []):
            keys.add(f"{b}:{d.get('old_start', '?')}-{d.get('old_end', '?')}")
    return keys


def gate_judge(pre_dir: Path, post_dir: Path, baseline: Dict) -> Dict:
    """模式 B：pre vs post 差异 ⊆ 噪声基线 = PASS。"""
    cmp = compare_runs(pre_dir, post_dir)

    # 收集模式 B 的结构性变化
    b_structural = {}
    for b, info in cmp["branches"].items():
        if info["structural_blocks"] > 0:
            b_structural[b] = info["structural_details"]

    # 基线中已知结构性变化（模式 A 应极少）
    baseline_structural = structural_change_keys(baseline)

    # 模式 B 的结构性变化若超出基线 → FAIL
    failures = []
    for b, details in b_structural.items():
        for d in details:
            key = f"{b}:{d.get('old_start', '?')}-{d.get('old_end', '?')}"
            if key not in baseline_structural:
                failures.append({
                    "branch": b,
                    "new_start": d.get("new_start"),
                    "new_end": d.get("new_end"),
                    "sample_new": d.get("new_lines", [])[:3],
                    "sample_old": d.get("old_lines", [])[:3],
                })

    verdict = "PASS" if not failures else "FAIL"
    return {
        "verdict": verdict,
        "compared": cmp,
        "structural_failures": failures,
        "baseline_structural_keys": sorted(baseline_structural),
    }


def main():
    ap = argparse.ArgumentParser(description="DumpComparer — VT3-C1 offline gate tool")
    ap.add_argument("--mode", choices=["A", "B"], required=True,
                    help="A=noise baseline (same commit twice); B=gate (pre vs post)")
    ap.add_argument("--run1", help="[mode A] first run dump dir")
    ap.add_argument("--run2", help="[mode A] second run dump dir")
    ap.add_argument("--pre", help="[mode B] pre-C1 dump dir")
    ap.add_argument("--post", help="[mode B] post-C1 dump dir")
    ap.add_argument("--baseline", help="[mode B] baseline json from mode A")
    ap.add_argument("--out", required=True, help="output json path")
    args = ap.parse_args()

    out_path = Path(args.out)
    out_path.parent.mkdir(parents=True, exist_ok=True)

    if args.mode == "A":
        if not args.run1 or not args.run2:
            print("mode A requires --run1 and --run2", file=sys.stderr)
            sys.exit(2)
        result = compare_runs(Path(args.run1), Path(args.run2))
        with open(out_path, "w", encoding="utf-8") as f:
            json.dump(result, f, ensure_ascii=False, indent=2)
        # 摘要
        total_struct = sum(i["structural_blocks"] for i in result["branches"].values())
        total_rand = sum(i["random_blocks"] for i in result["branches"].values())
        print(f"[mode A] branches={result['branches_compared']} "
              f"random_blocks={total_rand} structural_blocks={total_struct}")
        print(f"baseline written to {out_path}")
        sys.exit(0)

    elif args.mode == "B":
        if not args.pre or not args.post or not args.baseline:
            print("mode B requires --pre, --post, --baseline", file=sys.stderr)
            sys.exit(2)
        baseline = load_baseline(Path(args.baseline))
        result = gate_judge(Path(args.pre), Path(args.post), baseline)
        with open(out_path, "w", encoding="utf-8") as f:
            json.dump(result, f, ensure_ascii=False, indent=2)
        print(f"[mode B] VERDICT = {result['verdict']}")
        if result["verdict"] == "FAIL":
            print(f"  structural failures: {len(result['structural_failures'])}")
            for fail in result["structural_failures"][:10]:
                print(f"    - {fail['branch']} @{fail['new_start']}-{fail['new_end']}: "
                      f"{fail['sample_new'][:1]}")
            sys.exit(1)
        sys.exit(0)


if __name__ == "__main__":
    main()
