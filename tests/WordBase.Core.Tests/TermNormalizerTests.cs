using WordBase.Core.Text;
using Xunit;

namespace WordBase.Core.Tests;

public class TermNormalizerTests
{
    [Theory]
    [InlineData("Apple", "apple")]
    [InlineData("  apple  pie ", "apple pie")]
    [InlineData("ＡＢＣ", "abc")]
    [InlineData("ｱｲｳ", "アイウ")]
    [InlineData("生", "生")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Normalize_ShouldFoldWidthCaseAndWhitespace(string? input, string expected) =>
        Assert.Equal(expected, TermNormalizer.Normalize(input));

    [Fact]
    public void Tidy_ShouldKeepOriginalCaseAndWidth()
    {
        Assert.Equal("Apple Pie", TermNormalizer.Tidy("  Apple   Pie  "));
        Assert.Equal("ＡＢＣ", TermNormalizer.Tidy(" ＡＢＣ "));
    }

    [Fact]
    public void Normalize_ShouldNotStripMeaningfulPunctuation()
    {
        Assert.Equal("put on (clothes)", TermNormalizer.Normalize("Put on (clothes)"));
    }
}
