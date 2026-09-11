using System;
using System.Threading;
using System.Threading.Tasks;

namespace ValleytalkReborn;

/// <summary>
/// LLM 请求网关：统一超时与日志。
/// 不做并发限制——旧版 DynamicBarkManager 中每个 FetchBarksAsync / FetchA2AScriptAsync
/// 各自独立调用 llm.RunInference()，无全局信号量，此处保持一致。
/// Gateway 不得访问 Game1、NPC 或其他游戏对象。
///
/// 变更说明：移除了原先的 IsValidLlmContent 黑名单式前置拦截。
/// 那种"先假设内容合法，再逐条排除已知坏格式"的做法天然滞后——
/// 模型每换一种包装方式（新语言的寒暄语、新的伪代码壳），就得跟进
/// 新的排除规则，本质是打地鼠打不完。
/// 现在校验责任完全交给 DialogueParsing.ParseBarkJson：它的第一级
/// 用真正的括号结构扫描 + 即时反序列化验证来判断内容是否可用，
/// "能否提取出合法 JSON" 本身就是唯一标准，不再需要一层单独的
/// 内容合法性猜测。Gateway 只负责把原始响应原样透传给调用方。
///
/// 另一处根治性修复：RunInference 的 Prefill 参数由 "[" 改为 ""。
/// 根因：不少底层推理实现（尤其中转/逆向协议）不会把 Prefill 字符
/// 拼接进返回的 Response 文本，但模型自己"记得"已经输出过这个 [，
/// 于是接着写出与之配对的收尾符号——这正是日志中
/// import("json")]({"json":[...]}) 这类畸形输出的根本成因：模型把
/// 被吞掉的 Prefill "[" 误当成了 Markdown 链接语法的开头一部分。
/// 不再塞入这个 Prefill 后，这一整类畸形壳从源头上不会再产生。
/// </summary>
internal sealed class LlmRequestGateway
{
    private readonly int _timeoutSeconds;

    /// <param name="timeoutSeconds">超时秒数。传入 0 或负数则使用默认 30 秒。</param>
    internal LlmRequestGateway(int timeoutSeconds = 30)
    {
        _timeoutSeconds = timeoutSeconds > 0 ? timeoutSeconds : 30;
    }

    /// <summary>
    /// 执行 LLM 推理请求。超时或取消时返回 null，由调用方决定 fallback。
    /// 内容是否可用交给调用方的解析器判断，Gateway 不再预判。
    /// </summary>
    internal async Task<LlmResponse> ExecuteAsync(
        string source,
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken)
    {
        var llm = Llm.Instance;
        if (llm == null)
            return null;

        using var timeoutCts =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

        try
        {
            var response = await llm.RunInference(
                    systemPrompt,
                    "",
                    "",
                    userPrompt,
                    "",
                    cacheContext: source)
                .WaitAsync(timeoutCts.Token);

            if (response == null)
                return null;

            ModEntry.SMonitor?.Log(
                $"[{source}] LLM output:\n{response.Text}",
                StardewModdingAPI.LogLevel.Debug);

            return response;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}
