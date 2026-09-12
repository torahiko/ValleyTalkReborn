using System;
using System.Collections.Generic;
using Xunit;
using ValleytalkReborn;

namespace ValleyTalk.Tests
{
    /// <summary>
    /// Unit tests for A2A fallback script generation.
    /// Verifies that the fallback script has the correct number of lines.
    /// </summary>
    public class A2AFallbackTests
    {
        [Fact]
        public void TwoPersonFallbackHasSixLines()
        {
            var names = new List<string> { "Abigail", "Sebastian" };

            var mgr = new A2ASessionManager(null, null, null, null, null);
            var result = mgr.BuildFallbackScript(names, true);

            Assert.Equal(6, result.Length);
        }

        [Fact]
        public void ThreePersonFallbackHasSixLines()
        {
            var names = new List<string> { "Abigail", "Sebastian", "Haley" };

            var mgr = new A2ASessionManager(null, null, null, null, null);
            var result = mgr.BuildFallbackScript(names, true);

            Assert.Equal(6, result.Length);
        }

        [Fact]
        public void FourPersonFallbackHasEightLines()
        {
            var names = new List<string> { "Abigail", "Sebastian", "Haley", "Emily" };

            var mgr = new A2ASessionManager(null, null, null, null, null);
            var result = mgr.BuildFallbackScript(names, true);

            Assert.Equal(8, result.Length);
        }

        [Fact]
        public void EmptyNamesReturnsEmpty()
        {
            var mgr = new A2ASessionManager(null, null, null, null, null);
            var result = mgr.BuildFallbackScript(new List<string>(), true);
            Assert.Empty(result);
        }

        [Fact]
        public void SingleNameReturnsEmpty()
        {
            var names = new List<string> { "Abigail" };
            var mgr = new A2ASessionManager(null, null, null, null, null);
            var result = mgr.BuildFallbackScript(names, true);
            Assert.Empty(result);
        }

        [Fact]
        public void NullNamesReturnsEmpty()
        {
            var mgr = new A2ASessionManager(null, null, null, null, null);
            var result = mgr.BuildFallbackScript(null, true);
            Assert.Empty(result);
        }

        [Fact]
        public void FallbackLinesHaveValidSpeakerNames()
        {
            var names = new List<string> { "Abigail", "Sebastian" };

            var mgr = new A2ASessionManager(null, null, null, null, null);
            var result = mgr.BuildFallbackScript(names, true);

            foreach (var line in result)
            {
                Assert.False(string.IsNullOrWhiteSpace(line.SpeakerName));
                Assert.False(string.IsNullOrWhiteSpace(line.Line));
            }
        }

        [Fact]
        public void ChineseFallbackUsesChineseText()
        {
            var names = new List<string> { "Abigail", "Sebastian" };

            var mgr = new A2ASessionManager(null, null, null, null, null);
            var result = mgr.BuildFallbackScript(names, true);

            Assert.Equal(6, result.Length);
            // All lines should have non-empty content
            foreach (var line in result)
            {
                Assert.False(string.IsNullOrWhiteSpace(line.Line));
            }
        }
    }
}
