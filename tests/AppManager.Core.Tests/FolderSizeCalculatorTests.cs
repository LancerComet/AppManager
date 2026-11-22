using System.Diagnostics;
using AppManager.Core.Models;
using AppManager.Core.Scanning;

namespace AppManager.Core.Tests;

public class FolderSizeCalculatorTests : IDisposable
{
    private readonly string _root;

    public FolderSizeCalculatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "amtest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Write(string rel, int bytes)
    {
        var path = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public void Measure_SumsNestedFiles()
    {
        Write("a.bin", 100);
        Write(Path.Combine("sub", "b.bin"), 200);
        Write(Path.Combine("sub", "deep", "c.bin"), 50);

        var usage = FolderSizeCalculator.Measure(_root, FolderCategory.Custom, CancellationToken.None);

        Assert.Equal(350, usage.SizeBytes);
        Assert.Equal(3, usage.FileCount);
    }

    [Fact]
    public void Measure_DoesNotFollowJunctions()
    {
        Write("a.bin", 100);
        var sub = Path.Combine(_root, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllBytes(Path.Combine(sub, "b.bin"), new byte[200]);

        // Junction "sub\loop" -> _root would recurse forever if followed.
        var link = Path.Combine(sub, "loop");
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{_root}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        };
        var p = Process.Start(psi)!;
        p.WaitForExit(10_000);
        Assert.True(Directory.Exists(link), "junction was not created");

        var usage = FolderSizeCalculator.Measure(_root, FolderCategory.Custom, CancellationToken.None);

        Assert.Equal(300, usage.SizeBytes);
        Assert.Equal(2, usage.FileCount);
    }

    [Fact]
    public void Measure_CancellationThrows()
    {
        for (var i = 0; i < 10; i++)
            Write($"f{i}.bin", 10);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            FolderSizeCalculator.Measure(_root, FolderCategory.Custom, cts.Token));
    }

    [Fact]
    public void Measure_InvokesBytesCallback()
    {
        Write("a.bin", 6 * 1024 * 1024); // > 4 MiB report interval
        var calls = 0;
        var usage = FolderSizeCalculator.Measure(
            _root, FolderCategory.Custom, CancellationToken.None, _ => calls++);
        Assert.True(calls >= 1);
        Assert.Equal(6 * 1024 * 1024, usage.SizeBytes);
    }
}
