namespace ValleytalkReborn
{
    /// <summary>
    /// DD404：Daily 专用比较提交结果状态（D3 声明）。
    /// Applied=已落盘并发布；Unchanged=重放命中无需再写；Conflict/CapacityFull/Duplicate=比较冲突；
    /// StorageFailed=SaveData 写入失败（内存保持旧值）；Unavailable=存档/加载状态不满足；Invalid=请求非法。
    /// </summary>
    internal enum DailyTimelineCommitStatus
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

    /// <summary>Daily 提交请求。Request/Result 均为 Memory 对象，不持有游戏实例。</summary>
    internal sealed class DailyTimelineCommitRequest
    {
        public string NpcName { get; init; }
        public int TargetDay { get; init; }
        public string EntryId { get; init; }
        public bool IsCreate { get; init; }
        public string ExpectedContentHash { get; init; }
        public string NewContent { get; init; }
    }

    /// <summary>Daily 提交结果。ContentHash 为请求正文的 HashText 产物（小写十六进制 SHA256）。</summary>
    internal sealed class DailyTimelineCommitResult
    {
        public DailyTimelineCommitStatus Status { get; init; }
        public string EntryId { get; init; }
        public string ContentHash { get; init; }
        public string ErrorDetail { get; init; }
    }
}
