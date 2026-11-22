using AppManager.Core.Inventory;

namespace AppManager.Core.Tests;

public class RegistryValueParserTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\Foo\\foo.exe\",0", "C:\\Program Files\\Foo\\foo.exe")]
    [InlineData("C:\\x\\y.ico", "C:\\x\\y.ico")]
    [InlineData("\"C:\\Tools\\a b\\run.exe\",-1", "C:\\Tools\\a b\\run.exe")]
    public void ParseIconPath_StripsQuotesAndIndex(string input, string expected)
    {
        Assert.Equal(expected, RegistryValueParser.ParseIconPath(input));
    }

    [Fact]
    public void ParseIconPath_ExpandsEnvironmentVariables()
    {
        var result = RegistryValueParser.ParseIconPath("%SystemRoot%\\notepad.exe");
        Assert.NotNull(result);
        Assert.EndsWith("notepad.exe", result);
        Assert.DoesNotContain("%", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]          // not a rooted path
    [InlineData("not-a-path.txt")]   // not rooted
    public void ParseIconPath_GarbageReturnsNull(string? input)
    {
        Assert.Null(RegistryValueParser.ParseIconPath(input));
    }

    [Theory]
    [InlineData("C:\\")]                       // drive root
    [InlineData("C:")]                         // drive root
    [InlineData("C:\\Program Files")]          // Program Files root itself
    [InlineData("C:\\Program Files\\")]        // same, trailing slash
    [InlineData("%ProgramFiles%")]             // env var form
    [InlineData("%WINDIR%\\System32")]         // under Windows dir
    [InlineData("%WINDIR%")]                   // Windows dir itself
    [InlineData("%USERPROFILE%")]              // user profile root
    [InlineData("%LOCALAPPDATA%\\Programs")]   // Programs root itself
    public void NormalizeInstallLocation_RejectsTooBroadRoots(string input)
    {
        Assert.Null(RegistryValueParser.NormalizeInstallLocation(input));
    }

    [Fact]
    public void NormalizeInstallLocation_AcceptsProgramFilesSubfolder()
    {
        var pf = Environment.GetEnvironmentVariable("ProgramFiles")!;
        var result = RegistryValueParser.NormalizeInstallLocation(Path.Combine(pf, "Foo"));
        Assert.Equal(Path.Combine(pf, "Foo"), result);
    }

    [Fact]
    public void NormalizeInstallLocation_TrimsQuotesAndTrailingSlash()
    {
        var pf = Environment.GetEnvironmentVariable("ProgramFiles")!;
        var result = RegistryValueParser.NormalizeInstallLocation($"\"{pf}\\Foo\\\"");
        Assert.Equal(Path.Combine(pf, "Foo"), result);
    }

    [Fact]
    public void DeriveInstallLocationFromIcon_FirstLevelUnderProgramFiles()
    {
        var pf = Environment.GetEnvironmentVariable("ProgramFiles")!;
        var result = RegistryValueParser.DeriveInstallLocationFromIcon(
            Path.Combine(pf, "Foo", "bin", "foo.exe"));
        Assert.Equal(Path.Combine(pf, "Foo"), result);
    }

    [Fact]
    public void DeriveInstallLocationFromIcon_NonExeOrOutsideRoots_ReturnsNull()
    {
        var pf = Environment.GetEnvironmentVariable("ProgramFiles")!;
        Assert.Null(RegistryValueParser.DeriveInstallLocationFromIcon(Path.Combine(pf, "Foo", "foo.ico")));
        Assert.Null(RegistryValueParser.DeriveInstallLocationFromIcon(@"D:\Random\foo.exe"));
        Assert.Null(RegistryValueParser.DeriveInstallLocationFromIcon(null));
    }

    [Fact]
    public void DeriveInstallLocationFromUninstallString_FirstLevelUnderProgramFiles()
    {
        var pf = Environment.GetEnvironmentVariable("ProgramFiles")!;
        var result = RegistryValueParser.DeriveInstallLocationFromUninstallString(
            $"\"{pf}\\Foo\\unins000.exe\" /SILENT");
        Assert.Equal(Path.Combine(pf, "Foo"), result);
    }

    [Fact]
    public void DeriveInstallLocationFromUninstallString_NonSystemDir_UsesExeDirectory()
    {
        var result = RegistryValueParser.DeriveInstallLocationFromUninstallString(
            "\"E:\\Tools\\Foo\\uninstall.exe\"");
        Assert.Equal("E:\\Tools\\Foo", result);
    }

    [Fact]
    public void DeriveInstallLocationFromUninstallString_RejectsMsiexecAndRundll()
    {
        Assert.Null(RegistryValueParser.DeriveInstallLocationFromUninstallString(
            "MsiExec.exe /I{12345678-1234-1234-1234-123456789012}"));
        Assert.Null(RegistryValueParser.DeriveInstallLocationFromUninstallString(
            "rundll32.exe foo.dll,Uninstall"));
        Assert.Null(RegistryValueParser.DeriveInstallLocationFromUninstallString(null));
    }

    [Fact]
    public void DeriveInstallLocationFromUninstallString_RejectsLauncherAndProtocol()
    {
        // "D:\Steam\steam.exe" steam://uninstall/123 -> the launcher's dir is
        // not the app's install dir.
        Assert.Null(RegistryValueParser.DeriveInstallLocationFromUninstallString(
            "\"D:\\Steam\\steam.exe\" steam://uninstall/123456"));
        Assert.Null(RegistryValueParser.DeriveInstallLocationFromUninstallString(
            "\"E:\\Games\\EpicGamesLauncher.exe\" /uninstall"));
    }

    [Fact]
    public void ExtractExePath_HandlesQuotedAndUnquoted()
    {
        Assert.Equal("C:\\Tools\\a b\\u.exe",
            RegistryValueParser.ExtractExePath("\"C:\\Tools\\a b\\u.exe\" /S"));
        Assert.Equal("C:\\Tools\\u.exe",
            RegistryValueParser.ExtractExePath("C:\\Tools\\u.exe --remove"));
        Assert.Null(RegistryValueParser.ExtractExePath("regedit /s foo.reg"));
    }
}
