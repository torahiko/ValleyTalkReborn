// ModelDiscoveryTests.cs
// LOCAL-003-R1 — 本地模型发现诊断分类测试。
// 覆盖：ParseModelList 五类输入（成功 / 空 data / 非法 JSON / 缺 data / 缺 id）、
// 空响应体、失败分类可区分、Detail 截断与不泄露完整响应体。
// 纯内存测试：无 HTTP 桩、无网络、无 DNS、无 ModEntry、无游戏实例
// （HTTP 传输层不可在进程内桩化，故仅测纯解析函数与结果语义）。

using System;
using System.Linq;
using ValleytalkReborn;
using Xunit;

namespace ValleytalkReborn.Tests;

public class ModelDiscoveryTests
{
    private const string ValidListJson =
        "{\"object\":\"list\",\"data\":[{\"id\":\"llama3.2:3b\"},{\"id\":\"qwen2.5:7b\"}]}";

    // ── ParseModelList：成功 ──

    [Fact]
    public void ParseModelList_ValidData_ReturnsSortedByIdOrderAndNoFailure()
    {
        ModelDiscoveryResult result = ModelDiscovery.ParseModelList(ValidListJson);

        Assert.Equal(ModelDiscoveryFailure.None, result.Failure);
        Assert.Equal(0, result.StatusCode);
        Assert.Equal(new[] { "llama3.2:3b", "qwen2.5:7b" }, result.ModelNames);
        Assert.Equal(string.Empty, result.Detail);
    }

    [Fact]
    public void ParseModelList_ValidData_SkipsBlankIds()
    {
        ModelDiscoveryResult result = ModelDiscovery.ParseModelList(
            "{\"data\":[{\"id\":\"m1\"},{\"id\":\"  \"},{\"id\":\"m2\"}]}");

        Assert.Equal(ModelDiscoveryFailure.None, result.Failure);
        Assert.Equal(new[] { "m1", "m2" }, result.ModelNames);
    }

    // ── ParseModelList：EmptyResponse ──

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseModelList_BlankBody_ReturnsEmptyResponse(string body)
    {
        ModelDiscoveryResult result = ModelDiscovery.ParseModelList(body);

        Assert.Equal(ModelDiscoveryFailure.EmptyResponse, result.Failure);
        Assert.Empty(result.ModelNames);
    }

    [Fact]
    public void ParseModelList_EmptyDataArray_ReturnsEmptyResponse()
    {
        ModelDiscoveryResult result = ModelDiscovery.ParseModelList("{\"object\":\"list\",\"data\":[]}");

        Assert.Equal(ModelDiscoveryFailure.EmptyResponse, result.Failure);
        Assert.Empty(result.ModelNames);
    }

    // ── ParseModelList：InvalidJson ──

    [Theory]
    [InlineData("<html>502 Bad Gateway</html>")]
    [InlineData("{ \"data\": [ ")]
    [InlineData("[{\"id\":\"m1\"}]")]
    public void ParseModelList_MalformedJson_ReturnsInvalidJson(string body)
    {
        ModelDiscoveryResult result = ModelDiscovery.ParseModelList(body);

        Assert.Equal(ModelDiscoveryFailure.InvalidJson, result.Failure);
        Assert.Empty(result.ModelNames);
        Assert.Contains("JSON parse failed", result.Detail);
    }

    // ── ParseModelList：UnsupportedSchema ──

    [Fact]
    public void ParseModelList_MissingData_ReturnsUnsupportedSchema()
    {
        ModelDiscoveryResult result = ModelDiscovery.ParseModelList("{\"models\":[\"llama3.2:3b\"]}");

        Assert.Equal(ModelDiscoveryFailure.UnsupportedSchema, result.Failure);
        Assert.Empty(result.ModelNames);
    }

    [Fact]
    public void ParseModelList_DataWithoutId_ReturnsUnsupportedSchema()
    {
        ModelDiscoveryResult result = ModelDiscovery.ParseModelList(
            "{\"data\":[{\"name\":\"llama3.2:3b\"},{\"name\":\"qwen2.5:7b\"}]}");

        Assert.Equal(ModelDiscoveryFailure.UnsupportedSchema, result.Failure);
        Assert.Empty(result.ModelNames);
    }

    [Fact]
    public void ParseModelList_EmptyResponseAndUnsupportedSchema_AreDistinguishable()
    {
        ModelDiscoveryResult empty = ModelDiscovery.ParseModelList("{\"data\":[]}");
        ModelDiscoveryResult schema = ModelDiscovery.ParseModelList("{\"data\":[{\"name\":\"m1\"}]}");

        Assert.NotEqual(empty.Failure, schema.Failure);
    }

    // ── 失败分类可区分 ──

    [Fact]
    public void FailureCategories_AreDistinctValues()
    {
        var failures = new[]
        {
            ModelDiscoveryFailure.None,
            ModelDiscoveryFailure.MissingApiKey,
            ModelDiscoveryFailure.InvalidUrl,
            ModelDiscoveryFailure.Transport,
            ModelDiscoveryFailure.Http,
            ModelDiscoveryFailure.EmptyResponse,
            ModelDiscoveryFailure.InvalidJson,
            ModelDiscoveryFailure.UnsupportedSchema
        };

        Assert.Equal(failures.Length, failures.Select(f => (int)f).Distinct().Count());
    }

    [Fact]
    public void FailedResult_CarriesEmptyNamesStatusCodeAndDetail()
    {
        ModelDiscoveryResult result = ModelDiscovery.Failed(ModelDiscoveryFailure.Http, "Unauthorized", 401);

        Assert.Empty(result.ModelNames);
        Assert.Equal(ModelDiscoveryFailure.Http, result.Failure);
        Assert.Equal(401, result.StatusCode);
        Assert.Equal("Unauthorized", result.Detail);
    }

    // ── Detail 截断：不得承载完整响应体 ──

    [Fact]
    public void FailedResult_TruncatesDetailToMaxLength()
    {
        string longBody = new string('x', 500) + "-SECRET-TAIL";

        ModelDiscoveryResult result = ModelDiscovery.Failed(ModelDiscoveryFailure.UnsupportedSchema, longBody);

        Assert.True(result.Detail.Length <= ModelDiscovery.MaxDetailLength);
        Assert.DoesNotContain("SECRET-TAIL", result.Detail);
    }

    [Fact]
    public void ParseModelList_UnsupportedSchemaDetail_IsTruncatedResponseBody()
    {
        string longBody = "{\"models\":\"" + new string('y', 400) + "\"}";

        ModelDiscoveryResult result = ModelDiscovery.ParseModelList(longBody);

        Assert.Equal(ModelDiscoveryFailure.UnsupportedSchema, result.Failure);
        Assert.True(result.Detail.Length <= ModelDiscovery.MaxDetailLength);
    }

    [Fact]
    public void SuccessResult_HasNoFailureAndKeepsNames()
    {
        ModelDiscoveryResult result = ModelDiscovery.Success(new[] { "gpt-4o" });

        Assert.Equal(ModelDiscoveryFailure.None, result.Failure);
        Assert.Equal(new[] { "gpt-4o" }, result.ModelNames);
    }
}
