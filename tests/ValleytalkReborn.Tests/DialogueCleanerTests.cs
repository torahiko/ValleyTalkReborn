using Xunit;
using System.Collections.Generic;
using ValleyTalk;

namespace ValleyTalk.Tests
{
    /// <summary>
    /// Unit tests for the DialogueCleaner static class.
    /// Covers string manipulation, tag cleaning, and formatting logic.
    /// </summary>
    public class DialogueCleanerTests
    {
        // ============================================================
        // RemoveDotSuffixes Tests
        // ============================================================

        [Theory]
        [InlineData("Abigail-", "Abigail")]
        [InlineData("Sebastian·", "Sebastian")]
        [InlineData("Penny•", "Penny")]
        [InlineData("Penny", "Penny")]
        [InlineData("", "")]
        [InlineData(null, null)]
        [InlineData("---", "")]
        [InlineData("···", "")]
        [InlineData("•••", "")]
        [InlineData("Abigail-·•", "Abigail")]
        [InlineData("  Abigail-", "  Abigail")]
        public void RemoveDotSuffixes_ShouldRemoveSpecificTrailingCharacters(string input, string expected)
        {
            // Act
            var result = DialogueCleaner.RemoveDotSuffixes(input);

            // Assert
            Assert.Equal(expected, result);
        }

        // ============================================================
        // CommonCleanup Tests
        // ============================================================

        [Theory]
        [InlineData(null, "")]
        [InlineData("", "")]
        [InlineData("   ", "")]
        [InlineData("Hello World", "Hello World")]
        public void CommonCleanup_NullOrEmpty_ReturnsEmptyString(string input, string expected)
        {
            var result = DialogueCleaner.CommonCleanup(input);
            Assert.Equal(expected, result);
        }

        [Fact]
        public void CommonCleanup_ShouldTrimLeadingSymbols()
        {
            Assert.Equal("Hello", DialogueCleaner.CommonCleanup("   Hello"));
            Assert.Equal("Hello", DialogueCleaner.CommonCleanup("- Hello"));
            Assert.Equal("Hello", DialogueCleaner.CommonCleanup("% Hello"));
            Assert.Equal("Hello", DialogueCleaner.CommonCleanup("\"Hello"));
        }

        [Fact]
        public void CommonCleanup_ShouldRemoveHashBWrappers()
        {
            Assert.Equal("Hello world", DialogueCleaner.CommonCleanup("#$b#Hello world"));
            Assert.Equal("Hello world", DialogueCleaner.CommonCleanup("Hello world#$b#"));
            Assert.Equal("Hello", DialogueCleaner.CommonCleanup("#$b#Hello#$b#"));
        }

        [Fact]
        public void CommonCleanup_ShouldRemoveHashEWrappers()
        {
            Assert.Equal("Hello world", DialogueCleaner.CommonCleanup("#$e#Hello world"));
            Assert.Equal("Hello world", DialogueCleaner.CommonCleanup("Hello world#$e#"));
        }

        [Fact]
        public void CommonCleanup_ShouldRemoveQuotes()
        {
            Assert.Equal("Hello", DialogueCleaner.CommonCleanup("\"Hello\""));
            Assert.Equal("Hello world", DialogueCleaner.CommonCleanup("Hello\"world"));
        }

        // ============================================================
        // DialogueLineCleanup Tests - Portrait Preservation
        // ============================================================

        [Fact]
        public void DialogueLineCleanup_ShouldPreserveValidPortraits()
        {
            // Arrange
            var input = "Hello! #$h I am happy. #$s";
            var validPortraits = new List<string> { "h", "s" };

            // Act
            var result = DialogueCleaner.DialogueLineCleanup(input, validPortraits, false);

            // Assert
            Assert.Contains("$h", result);
            Assert.Contains("$s", result);
            Assert.DoesNotContain("#$h", result);
            Assert.DoesNotContain("#$s", result);
        }

        [Fact]
        public void DialogueLineCleanup_ShouldNotConvertInvalidPortraitTags()
        {
            // Arrange
            var input = "Hello! #$x How are you?";
            var validPortraits = new List<string> { "h", "s" };

            // Act
            var result = DialogueCleaner.DialogueLineCleanup(input, validPortraits, false);

            // Assert
            // #$x is not in validPortraits so it should remain as #$x (not converted to $x)
            // but the regex will strip $x since it is not a valid portrait or engine tag
            Assert.DoesNotContain("$x", result);
        }

        // ============================================================
        // DialogueLineCleanup Tests - Invalid Tag Removal
        // ============================================================

        [Fact]
        public void DialogueLineCleanup_ShouldRemoveInvalidDollarTags()
        {
            // Arrange
            var input = "Hello! $badtag Here is more text.";
            var validPortraits = new List<string> { "h" };

            // Act
            var result = DialogueCleaner.DialogueLineCleanup(input, validPortraits, false);

            // Assert
            Assert.DoesNotContain("$badtag", result);
            Assert.DoesNotContain("badtag", result);
        }

        [Fact]
        public void DialogueLineCleanup_ShouldRemoveRandomInvalidMarkers()
        {
            // Arrange
            var input = "Test $x and $abc and $z text.";
            var validPortraits = new List<string> { "h" };

            // Act
            var result = DialogueCleaner.DialogueLineCleanup(input, validPortraits, false);

            // Assert
            Assert.DoesNotContain("$x", result);
            Assert.DoesNotContain("$abc", result);
            Assert.DoesNotContain("$z", result);
        }

        // ============================================================
        // DialogueLineCleanup Tests - Engine Tags Preservation
        // ============================================================

        [Fact]
        public void DialogueLineCleanup_ShouldPreserveEngineTags_E()
        {
            // Arrange
            var input = "Hello $e world";
            var validPortraits = new List<string> { "h" };

            // Act
            var result = DialogueCleaner.DialogueLineCleanup(input, validPortraits, false);

            // Assert - $e should be preserved (it is a valid engine tag)
            Assert.Contains("$e", result);
        }

        [Fact]
        public void DialogueLineCleanup_ShouldPreserveEngineTags_B()
        {
            // Arrange
            var input = "Hello $b world";
            var validPortraits = new List<string> { "h" };

            // Act
            var result = DialogueCleaner.DialogueLineCleanup(input, validPortraits, false);

            // Assert - $b should be preserved
            Assert.Contains("$b", result);
        }

        [Fact]
        public void DialogueLineCleanup_ShouldPreserveEngineTags_C()
        {
            // Arrange
            var input = "Hello $c world";
            var validPortraits = new List<string> { "h" };

            // Act
            var result = DialogueCleaner.DialogueLineCleanup(input, validPortraits, false);

            // Assert - $c should be preserved
            Assert.Contains("$c", result);
        }

        [Fact]
        public void DialogueLineCleanup_ShouldRemoveHashCDirective()
        {
            // Arrange
            var input = "Hello #$c .5# world";
            var validPortraits = new List<string> { "h" };

            // Act
            var result = DialogueCleaner.DialogueLineCleanup(input, validPortraits, false);

            // Assert - "#$c .5#" should be removed
            Assert.DoesNotContain("#$c .5#", result);
            Assert.Contains("Hello", result);
            Assert.Contains("world", result);
        }

        // ============================================================
        // DialogueLineCleanup Tests - Punctuation Fixing
        // ============================================================

        [Fact]
        public void DialogueLineCleanup_ShouldAddPeriodToEnglishWithoutPunctuation()
        {
            // Arrange
            var input = "Hello world";
            var validPortraits = new List<string> { "h" };

            // Act
            var result = DialogueCleaner.DialogueLineCleanup(input, validPortraits, true);

            // Assert
            Assert.EndsWith(".", result);
            Assert.Contains("Hello world.", result);
        }

        [Fact]
        public void DialogueLineCleanup_ShouldAddPeriodToChineseWithoutPunctuation()
        {
            // Arrange
            var input = "你好世界";
            var validPortraits = new List<string> { "h" };

            // Act
            var result = DialogueCleaner.DialogueLineCleanup(input, validPortraits, true);

            // Assert
            Assert.EndsWith(".", result);
        }

        [Fact]
        public void DialogueLineCleanup_ShouldNotDuplicateExistingEnglishPeriod()
        {
            // Arrange
            var input = "Hello world.";
            var validPortraits = new List<string> { "h" };

            // Act
            var result = DialogueCleaner.DialogueLineCleanup(input, validPortraits, true);

            // Assert
            Assert.Equal("Hello world.", result);
        }

        [Fact]
        public void DialogueLineCleanup_ShouldNotDuplicateExistingChinesePeriod()
        {
            // Arrange
            var input = "你好世界。";
            var validPortraits = new List<string> { "h" };

            // Act
            var result = DialogueCleaner.DialogueLineCleanup(input, validPortraits, true);

            // Assert
            Assert.Equal("你好世界。", result);
        }

        [Fact]
        public void DialogueLineCleanup_ShouldNotDuplicateExclamation()
        {
            // Arrange
            var input = "Hello world!";
            var validPortraits = new List<string> { "h" };

            // Act
            var result = DialogueCleaner.DialogueLineCleanup(input, validPortraits, true);

            // Assert
            Assert.Equal("Hello world!", result);
        }

        [Fact]
        public void DialogueLineCleanup_ShouldNotDuplicateQuestionMark()
        {
            // Arrange
            var input = "How are you?";
            var validPortraits = new List<string> { "h" };

            // Act
            var result = DialogueCleaner.DialogueLineCleanup(input, validPortraits, true);

            // Assert
            Assert.Equal("How are you?", result);
        }

        [Fact]
        public void DialogueLineCleanup_ShouldNotAddPunctuationWhenDisabled()
        {
            // Arrange
            var input = "Hello world";
            var validPortraits = new List<string> { "h" };

            // Act
            var result = DialogueCleaner.DialogueLineCleanup(input, validPortraits, false);

            // Assert
            Assert.Equal("Hello world", result);
        }

        // ============================================================
        // DialogueLineCleanup Tests - Null/Empty/Whitespace
        // ============================================================

        [Fact]
        public void DialogueLineCleanup_NullInput_ReturnsEmptyString()
        {
            var result = DialogueCleaner.DialogueLineCleanup(null, new List<string> { "h" }, false);
            Assert.Equal(string.Empty, result);
        }

        [Fact]
        public void DialogueLineCleanup_EmptyInput_ReturnsEmptyString()
        {
            var result = DialogueCleaner.DialogueLineCleanup("", new List<string> { "h" }, false);
            Assert.Equal(string.Empty, result);
        }

        [Fact]
        public void DialogueLineCleanup_WhitespaceInput_ReturnsEmptyString()
        {
            var result = DialogueCleaner.DialogueLineCleanup("   ", new List<string> { "h" }, false);
            Assert.Equal(string.Empty, result);
        }

        // ============================================================
        // DialogueLineCleanup Tests - Double Hash Normalization
        // ============================================================

        [Fact]
        public void DialogueLineCleanup_ShouldNormalizeDoubleHashE()
        {
            // Arrange
            var input = "Hello ##$e world";
            var validPortraits = new List<string> { "h" };

            // Act
            var result = DialogueCleaner.DialogueLineCleanup(input, validPortraits, false);

            // Assert - ##$e should become #$e
            Assert.Contains("#$e", result);
            Assert.DoesNotContain("##$e", result);
        }

        [Fact]
        public void DialogueLineCleanup_ShouldNormalizeDoubleHashB()
        {
            // Arrange
            var input = "Hello ##$b world";
            var validPortraits = new List<string> { "h" };

            // Act
            var result = DialogueCleaner.DialogueLineCleanup(input, validPortraits, false);

            // Assert - ##$b should become #$b
            Assert.Contains("#$b", result);
            Assert.DoesNotContain("##$b", result);
        }

        // ============================================================
        // DialogueLineCleanup Tests - @ Symbol Deduplication
        // ============================================================

        [Fact]
        public void DialogueLineCleanup_ShouldDeduplicateAtSymbols()
        {
            // Arrange
            var input = "Hello @@ world";
            var validPortraits = new List<string> { "h" };

            // Act
            var result = DialogueCleaner.DialogueLineCleanup(input, validPortraits, false);

            // Assert
            Assert.Contains("@", result);
            Assert.DoesNotContain("@@", result);
        }

        // ============================================================
        // DialogueLineCleanup Tests - Null validPortraits
        // ============================================================

        [Fact]
        public void DialogueLineCleanup_NullValidPortraits_ShouldNotThrow()
        {
            // Arrange
            var input = "Hello world";

            // Act
            var result = DialogueCleaner.DialogueLineCleanup(input, null, false);

            // Assert
            Assert.NotNull(result);
            Assert.Contains("Hello world", result);
        }

        // ============================================================
        // DialogueLineCleanup Tests - Relaxed Validation
        // ============================================================

        [Fact]
        public void DialogueLineCleanup_RelaxedValidation_AllowsLongText()
        {
            // Arrange - create a string longer than 200 chars without punctuation
            var longText = new string('a', 250);
            var validPortraits = new List<string> { "h" };

            // Act
            var result = DialogueCleaner.DialogueLineCleanup(longText, validPortraits, false, relaxedValidation: true);

            // Assert - with relaxed validation, long text should not be removed
            Assert.NotNull(result);
            Assert.NotEmpty(result);
        }

        // ============================================================
        // ResponseLineCleanup Tests
        // ============================================================

        [Fact]
        public void ResponseLineCleanup_ShouldRemoveHashes()
        {
            var result = DialogueCleaner.ResponseLineCleanup("Hello#world#", false);
            Assert.Equal("Helloworld", result);
        }

        [Fact]
        public void ResponseLineCleanup_ShouldRemoveDollarCommands()
        {
            var result = DialogueCleaner.ResponseLineCleanup("Hello $h world", false);
            Assert.Contains("Hello", result);
            Assert.Contains("world", result);
            Assert.DoesNotContain("$h", result);
        }

        [Fact]
        public void ResponseLineCleanup_ShouldAddPeriodWhenFixPunctuationEnabled()
        {
            var result = DialogueCleaner.ResponseLineCleanup("Hello world", true);
            Assert.Equal("Hello world.", result);
        }

        [Fact]
        public void ResponseLineCleanup_ShouldNotAddPeriodWhenDisabled()
        {
            var result = DialogueCleaner.ResponseLineCleanup("Hello world", false);
            Assert.Equal("Hello world", result);
        }

        [Fact]
        public void ResponseLineCleanup_ShouldNotDuplicateExistingPeriod()
        {
            var result = DialogueCleaner.ResponseLineCleanup("Hello world.", true);
            Assert.Equal("Hello world.", result);
        }

        [Fact]
        public void ResponseLineCleanup_LongText_ReturnsEmptyString()
        {
            // Arrange - create a string longer than 90 chars
            var longText = new string('a', 91);

            // Act
            var result = DialogueCleaner.ResponseLineCleanup(longText, false);

            // Assert
            Assert.Equal(string.Empty, result);
        }

        [Fact]
        public void ResponseLineCleanup_NinetyChars_ReturnsNormally()
        {
            // Arrange - exactly 90 chars
            var text = new string('a', 90);

            // Act
            var result = DialogueCleaner.ResponseLineCleanup(text, false);

            // Assert
            Assert.Equal(90, result.Length);
        }

        [Fact]
        public void ResponseLineCleanup_ShouldTrimWhitespace()
        {
            var result = DialogueCleaner.ResponseLineCleanup("  Hello world  ", false);
            Assert.Equal("Hello world", result);
        }

        // ============================================================
        // DialogueLineCleanup Tests - Complex Integration Scenarios
        // ============================================================

        [Fact]
        public void DialogueLineCleanup_ComplexScenario_PortraitsAndTagsAndPunctuation()
        {
            // Arrange - a realistic complex input
            var input = "#$h Hello! #$s How are you$badtag doing?$x";
            var validPortraits = new List<string> { "h", "s" };

            // Act
            var result = DialogueCleaner.DialogueLineCleanup(input, validPortraits, true);

            // Assert
            Assert.Contains("$h", result);
            Assert.Contains("$s", result);
            Assert.DoesNotContain("#$h", result);
            Assert.DoesNotContain("#$s", result);
            Assert.DoesNotContain("$badtag", result);
            Assert.DoesNotContain("$x", result);
        }

        [Fact]
        public void DialogueLineCleanup_EngineTags_EAndB_PreservedWithPunctuation()
        {
            // Arrange
            var input = "Hello $e world $b test";
            var validPortraits = new List<string> { "h" };

            // Act
            var result = DialogueCleaner.DialogueLineCleanup(input, validPortraits, true);

            // Assert
            Assert.Contains("$e", result);
            Assert.Contains("$b", result);
        }

        [Fact]
        public void DialogueLineCleanup_ChinesePunctuation_AllTypes()
        {
            // Arrange & Act - Chinese full-width punctuation should be recognized
            var input1 = "你好世界。";
            var input2 = "你好世界！";
            var input3 = "你好世界？";
            var validPortraits = new List<string> { "h" };

            var result1 = DialogueCleaner.DialogueLineCleanup(input1, validPortraits, true);
            var result2 = DialogueCleaner.DialogueLineCleanup(input2, validPortraits, true);
            var result3 = DialogueCleaner.DialogueLineCleanup(input3, validPortraits, true);

            // Assert - should not add extra period
            Assert.Equal("你好世界。", result1);
            Assert.Equal("你好世界！", result2);
            Assert.Equal("你好世界？", result3);
        }
    }
}
