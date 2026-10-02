using System.Collections.Generic;

namespace ValleytalkReborn
{
    /// <summary>
    /// DD404B：自动周/季/年浓缩的两阶段保源提交结果状态（D6 声明）。
    /// Applied=归档与活跃两库均已写入并发布；Unchanged=重放命中；Conflict/CapacityFull/Duplicate=比较冲突；
    /// StorageFailed=某一阶段 SaveData 写入失败（任何可观察失败均不通过单独删源继续执行）；
    /// Unavailable=存档/加载状态不满足；Invalid=请求非法。
    /// </summary>
    internal enum TimelineCondensationCommitStatus
    {
        Applied,
        Unchanged,
        Conflict,
        CapacityFull,
        Duplicate,
        StorageFailed,
        Unavailable,
        Invalid
    }

    /// <summary>
    /// 浓缩提交请求。源列表为 ID 与 hash 两个平行数组，按同一索引对应；
    /// SourceContentHashes 为捕获时各源正文的 HashText 产物。
    /// </summary>
    internal sealed class TimelineCondensationCommitRequest
    {
        public string NpcName { get; init; }
        public int TargetDay { get; init; }
        public MemoryTier TargetTier { get; init; }
        public string EntryId { get; init; }
        public string NewContent { get; init; }
        public IReadOnlyList<string> SourceEntryIds { get; init; }
        public IReadOnlyList<string> SourceContentHashes { get; init; }
    }

    /// <summary>D6 结果仅含状态、聚合 EntryId 与错误详情；两阶段进度通过 ErrorDetail 与日志表达。</summary>
    internal sealed class TimelineCondensationCommitResult
    {
        public TimelineCondensationCommitStatus Status { get; init; }
        public string EntryId { get; init; }
        public string ErrorDetail { get; init; }
    }
}
