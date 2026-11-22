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

    // ---- Characterization of HEAD semantics (MeasureMany must match) ----

    [Fact]
    public void Measure_NonexistentRoot_ReturnsEmpty()
    {
        var missing = Path.Combine(_root, "no-such-dir");

        var usage = FolderSizeCalculator.Measure(missing, FolderCategory.Custom, CancellationToken.None);

        Assert.Equal(0, usage.SizeBytes);
        Assert.Equal(0, usage.FileCount);
        // HEAD: the failed directory open goes through ContinueOnError once.
        Assert.Equal(1, usage.InaccessibleCount);
        Assert.Equal("no-such-dir", usage.Name);
    }

    private static void RunIcacls(string args)
    {
        var psi = new ProcessStartInfo("icacls.exe", args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var p = Process.Start(psi)!;
        p.WaitForExit(10_000);
        Assert.True(p.ExitCode == 0, $"icacls {args} failed: {p.StandardOutput.ReadToEnd()}{p.StandardError.ReadToEnd()}");
    }

    [Fact]
    public void Measure_DeniedSubdir_CountsInaccessible()
    {
        Write("ok.bin", 100);
        var denied = Path.Combine(_root, "denied");
        Directory.CreateDirectory(denied);
        File.WriteAllBytes(Path.Combine(denied, "x.bin"), new byte[500]);

        var user = Environment.UserName;
        RunIcacls($"\"{denied}\" /deny \"{user}:(OI)(CI)F\"");
        try
        {
            var usage = FolderSizeCalculator.Measure(_root, FolderCategory.Custom, CancellationToken.None);

            Assert.Equal(100, usage.SizeBytes);
            Assert.Equal(1, usage.FileCount);
            Assert.True(usage.InaccessibleCount >= 1, $"expected >=1 inaccessible, got {usage.InaccessibleCount}");
        }
        finally
        {
            RunIcacls($"\"{denied}\" /remove:d \"{user}\"");
        }
    }

    // ---- MeasureMany ----

    private static FolderCandidate Candidate(string path)
        => new() { Path = path, Category = FolderCategory.Custom };

    [Fact]
    public void MeasureMany_ThreeRoots_ExactTotalsOrderedCallbacks()
    {
        // Root 1: deep tree, ~200 nested dirs, one file per level.
        var deep = Path.Combine(_root, "deep");
        var dir = deep;
        for (var i = 0; i < 200; i++)
            dir = Path.Combine(dir, $"d{i}");
        Directory.CreateDirectory(dir);
        var cursor = deep;
        for (var i = 0; i < 200; i++)
        {
            File.WriteAllBytes(Path.Combine(cursor, $"f{i}.bin"), new byte[10]);
            cursor = Path.Combine(cursor, $"d{i}");
        }
        // Root 2: wide tree, ~500 sibling dirs with files.
        var wide = Path.Combine(_root, "wide");
        for (var i = 0; i < 500; i++)
        {
            var w = Path.Combine(wide, $"w{i}");
            Directory.CreateDirectory(w);
            File.WriteAllBytes(Path.Combine(w, "f.bin"), new byte[4]);
        }
        // Root 3: empty dir.
        var empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);

        var roots = new[] { Candidate(deep), Candidate(wide), Candidate(empty) };
        var completions = new int[3];
        long deltaSum = 0;

        var result = FolderSizeCalculator.MeasureMany(
            roots, 8, CancellationToken.None,
            onFolderCompleted: (c, _) =>
            {
                var idx = Array.FindIndex(roots, r => r.Path == c.Path);
                System.Threading.Interlocked.Increment(ref completions[idx]);
            },
            onBytesDelta: d => System.Threading.Interlocked.Add(ref deltaSum, d));

        Assert.Equal(3, result.Count);
        Assert.Equal(deep, result[0].FullPath);
        Assert.Equal(wide, result[1].FullPath);
        Assert.Equal(empty, result[2].FullPath);

        Assert.Equal(200 * 10, result[0].SizeBytes);
        Assert.Equal(200, result[0].FileCount);
        Assert.Equal(500 * 4, result[1].SizeBytes);
        Assert.Equal(500, result[1].FileCount);
        Assert.Equal(0, result[2].SizeBytes);
        Assert.Equal(0, result[2].FileCount);

        Assert.Equal(new[] { 1, 1, 1 }, completions);
        Assert.Equal(result.Sum(u => u.SizeBytes), deltaSum);
    }

    [Fact]
    public void MeasureMany_DoesNotFollowJunctions()
    {
        Write("a.bin", 100);
        var sub = Path.Combine(_root, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllBytes(Path.Combine(sub, "b.bin"), new byte[200]);

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

        var result = FolderSizeCalculator.MeasureMany(
            new[] { Candidate(_root) }, 4, CancellationToken.None);

        Assert.Equal(300, result[0].SizeBytes);
        Assert.Equal(2, result[0].FileCount);
    }

    [Fact]
    public void MeasureMany_PreCancelled_Throws()
    {
        Write("f.bin", 10);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            FolderSizeCalculator.MeasureMany(
                new[] { Candidate(_root) }, 4, cts.Token));
    }

    [Fact]
    public async Task MeasureMany_CancelInsideCompletedCallback_ThrowsFast()
    {
        // Small first root completes quickly; the huge second root keeps working.
        var small = Path.Combine(_root, "small");
        Directory.CreateDirectory(small);
        File.WriteAllBytes(Path.Combine(small, "f.bin"), new byte[10]);
        var big = Path.Combine(_root, "big");
        for (var i = 0; i < 3000; i++)
        {
            var w = Path.Combine(big, $"b{i}");
            Directory.CreateDirectory(w);
            File.WriteAllBytes(Path.Combine(w, "f.bin"), new byte[8]);
        }

        using var cts = new CancellationTokenSource();
        var completions = 0;
        var task = Task.Run(() => FolderSizeCalculator.MeasureMany(
            new[] { Candidate(small), Candidate(big) }, 8, cts.Token,
            onFolderCompleted: (_, _) =>
            {
                if (System.Threading.Interlocked.Increment(ref completions) == 1)
                    cts.Cancel();
            }));

        var ex = await Record.ExceptionAsync(() => task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.IsType<OperationCanceledException>(ex);
    }
}
