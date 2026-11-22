using AppManager.Core.Matching;

namespace AppManager.Core.Tests;

public class NameNormalizerTests
{
    [Theory]
    [InlineData(".vscode", "vscode")]
    [InlineData("VS Code", "vscode")]
    [InlineData("JetBrains", "jetbrains")]
    [InlineData("JetBrains s.r.o.", "jetbrains")]
    [InlineData("JetBrains s.r.o", "jetbrains")]
    [InlineData("Python 3.12.1 (64-bit)", "python")]
    [InlineData("Python 3.12.1", "python")]
    [InlineData("Node.js", "nodejs")]
    [InlineData("Foo Bar 2024.1", "foobar")]
    [InlineData("Microsoft Corporation", "microsoft")]
    [InlineData("Adobe Inc.", "adobe")]
    [InlineData("GitHub, Inc.", "github")]
    [InlineData("npm-cache", "npmcache")]
    [InlineData(".vagrant.d", "vagrant")]
    [InlineData("Some App (x64)", "someapp")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Normalize_ProducesComparableTokens(string? input, string expected)
    {
        Assert.Equal(expected, NameNormalizer.Normalize(input));
    }

    [Fact]
    public void Normalize_StripsLocaleSuffix()
    {
        Assert.Equal("wechat", NameNormalizer.Normalize("WeChat - 中文(简体)"));
    }
}
