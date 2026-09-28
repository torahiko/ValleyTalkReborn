using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ValleytalkReborn;

/// <summary>
/// 模型发现失败分类（LOCAL-003-R1）。把 /v1/models 的空结果压扁为可诊断原因。
/// </summary>
internal enum ModelDiscoveryFailure
{
    None,
    MissingApiKey,
    InvalidUrl,
    Transport,
    Http,
    EmptyResponse,
    InvalidJson,
    UnsupportedSchema
}

/// <summary>
/// 模型发现结果（一次性对象，仅驻留内存，不写入 Config / 存档 / ModData）。
/// </summary>
internal sealed record ModelDiscoveryResult(
    string[] ModelNames,
    ModelDiscoveryFailure Failure,
    int StatusCode,
    string Detail);

/// <summary>
/// 诊断能力接口。仅 LlmOpenAiBase 实现，LlmOAICompatible 等子类经继承自动获得；
/// 非 OpenAI 族 Provider 继续走 legacy 路径。
/// </summary>
internal interface IModelDiscoveryDiagnostics
{
    Task<ModelDiscoveryResult> GetModelNamesWithDiagnosticsAsync();
}

internal static class ModelDiscovery
{
    /// <summary>Detail 上限：仅保留截断的服务端摘要，避免 UI / 日志承载完整响应体。</summary>
    internal const int MaxDetailLength = 200;

    internal static ModelDiscoveryResult Success(string[] modelNames) =>
        new ModelDiscoveryResult(modelNames ?? Array.Empty<string>(), ModelDiscoveryFailure.None, 0, string.Empty);

    internal static ModelDiscoveryResult Failed(ModelDiscoveryFailure failure, string detail, int statusCode = 0) =>
        new ModelDiscoveryResult(Array.Empty<string>(), failure, statusCode, TruncateDetail(detail));

    internal static string TruncateDetail(string detail)
    {
        if (string.IsNullOrEmpty(detail)) return string.Empty;

        string collapsed = detail.Trim();

        return collapsed.Length <= MaxDetailLength
            ? collapsed
            : collapsed.Substring(0, MaxDetailLength);
    }

    /// <summary>
    /// 纯函数：解析 /v1/models 响应体。不做网络请求、不读配置、不写状态。
    /// data 缺失或条目无 id 视为服务端 schema 不符；data 为空数组视为合法空列表。
    /// </summary>
    internal static ModelDiscoveryResult ParseModelList(string jsonString)
    {
        if (string.IsNullOrWhiteSpace(jsonString))
            return Failed(ModelDiscoveryFailure.EmptyResponse, "Response body is empty.");

        JObject responseJson;

        try
        {
            responseJson = JObject.Parse(jsonString);
        }
        catch (Exception ex)
        {
            return Failed(ModelDiscoveryFailure.InvalidJson, "JSON parse failed: " + ex.Message);
        }

        var modelsToken = responseJson["data"] as JArray;

        if (modelsToken == null)
            return Failed(ModelDiscoveryFailure.UnsupportedSchema, jsonString);

        if (modelsToken.Count == 0)
            return Failed(ModelDiscoveryFailure.EmptyResponse, "Server advertised no models.");

        var modelNames = new List<string>();

        foreach (JToken model in modelsToken)
        {
            if (model is not JObject modelObject) continue;

            JToken idToken = modelObject["id"];

            if (idToken != null && !string.IsNullOrWhiteSpace(idToken.ToString()))
            {
                modelNames.Add(idToken.ToString());
            }
        }

        if (modelNames.Count == 0)
            return Failed(ModelDiscoveryFailure.UnsupportedSchema, jsonString);

        return Success(modelNames.ToArray());
    }
}
