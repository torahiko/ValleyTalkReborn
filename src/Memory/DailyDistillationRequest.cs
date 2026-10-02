namespace ValleytalkReborn
{
    /// <summary>
    /// DD405：日记蒸馏后台生成请求。Request/Result 均为 Memory 对象；
    /// Provider 是主线程捕获的既有服务引用，人设切片由调用方在主线程预先执行
    /// <see cref="MemoryExtractService.BuildPersonaSlice"/> 后填入——后台阶段只消费快照，
    /// 不读取 Config、I18n、Constants.SaveFolderName、Game1 或 NPC。
    /// </summary>
    internal sealed class DailyDistillationRequest
    {
        public Llm Provider { get; init; }
        public string NpcName { get; init; }
        public string NpcDisplayName { get; init; }
        public string PersonaSlice { get; init; }
        public string ExistingContent { get; init; }
        public string TargetDateLabel { get; init; }
        public DailyDistillationSnapshot Snapshot { get; init; }
        public bool IsChinese { get; init; }
        public bool IsFinal { get; init; }
        public int TimeoutSeconds { get; init; }
    }
}
