using System.Text.Json;
using AppManager.Core.Settings;
using Xunit;

namespace AppManager.Core.Tests;

public class SettingsPersistenceTests
{
    // Sample captured from a real settings.json written by the pre-AOT
    // (reflection-based) serializer: PascalCase, numeric enum, compact.
    private const string OldFormat =
        """{"Theme":2,"Language":"ja-JP","ExtraRoots":["D:\\Tools","E:\\Games"],"HideLowConfidence":true,"Window":{"X":10,"Y":20,"Width":1280,"Height":800,"IsMaximized":false}}""";

    [Fact]
    public void OldFormat_Deserializes()
    {
        var s = JsonSerializer.Deserialize(OldFormat, SettingsJsonContext.Default.PersistedSettings);
        Assert.NotNull(s);
        Assert.Equal(ThemeSetting.Dark, s.Theme);
        Assert.Equal("ja-JP", s.Language);
        Assert.Equal(new[] { @"D:\Tools", @"E:\Games" }, s.ExtraRoots);
        Assert.True(s.HideLowConfidence);
        Assert.NotNull(s.Window);
        Assert.Equal(10, s.Window!.X);
        Assert.Equal(20, s.Window.Y);
        Assert.Equal(1280, s.Window.Width);
        Assert.Equal(800, s.Window.Height);
        Assert.False(s.Window.IsMaximized);
    }

    [Fact]
    public void RoundTrip_ByteIdentical()
    {
        var s = JsonSerializer.Deserialize(OldFormat, SettingsJsonContext.Default.PersistedSettings);
        var json = JsonSerializer.Serialize(s, SettingsJsonContext.Default.PersistedSettings);
        Assert.Equal(OldFormat, json);
    }

    [Fact]
    public void RoundTrip_NoWindow()
    {
        const string json = """{"Theme":0,"Language":"system","ExtraRoots":[],"HideLowConfidence":false,"Window":null}""";
        var s = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.PersistedSettings);
        Assert.NotNull(s);
        Assert.Equal(ThemeSetting.System, s.Theme);
        Assert.Equal("system", s.Language);
        Assert.Null(s.Window);
        Assert.Equal(json, JsonSerializer.Serialize(s, SettingsJsonContext.Default.PersistedSettings));
    }
}
