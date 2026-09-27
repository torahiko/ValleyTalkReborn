// PromptTopologyDiagnosticTests.cs
// VT3-E-R2-VERIFY-002 — synthetic tests for the A2 parity classifier
// (PromptTopologyDumper.EvaluateA2Status). Pure-function tests: no game state,
// no ModEntry statics mutated.

using ValleytalkReborn;
using Xunit;

// A2 前缀链分类：Pass / Warn（已知空历史 SpokeJustNow 夹具过渡）/ Fail（BUG）。
public class PromptTopologyDiagnosticTests
{
    [Fact]
    public void EvaluateA2Status_PrefixChainPassed_ReturnsPass()
    {
        Assert.Equal(A2ParityStatus.Pass, PromptTopologyDumper.EvaluateA2Status(true, false, false));
    }

    [Fact]
    public void EvaluateA2Status_KnownSpokeJustNowTransition_ReturnsWarn()
    {
        Assert.Equal(A2ParityStatus.Warn, PromptTopologyDumper.EvaluateA2Status(false, true, true));
    }

    [Fact]
    public void EvaluateA2Status_NoSpokeJustNowTransition_ReturnsFail()
    {
        Assert.Equal(A2ParityStatus.Fail, PromptTopologyDumper.EvaluateA2Status(false, false, true));
    }
}
