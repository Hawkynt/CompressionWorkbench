using System.Diagnostics;
using System.Security.Cryptography;
using Compression.Registry;
using FileFormat.Dar;

namespace Compression.Tests.Dar;

/// <summary>
/// A member past every 32-bit boundary (5 GiB: more than an array holds, offsets and sizes past
/// 4 GiB, a 20-byte data checksum) written and read back in bounded memory, and judged by dar when
/// it is installed. Writes about 15 GiB of temporary files, so it is in the advisory Performance
/// tier and skips when the temp volume lacks room.
/// </summary>
[TestFixture]
[Category("Performance")]
public sealed class DarLargeFileTests {
  private const long FileSize = 5L << 30;

  [TestCase("stored", false, "")]
  [TestCase("gzip", true, "1G")]
  public void GivenAFileOf5GiB_WhenArchivedAndRestored_ThenMemoryStaysBoundedAndTheDataIsIntact(string method, bool tapeMarks, string sliceSize) {
    var temp = Path.GetTempPath();
    var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(temp))!).AvailableFreeSpace;
    if (free < 3 * FileSize + (2L << 30))
      Assert.Ignore($"needs {(3 * FileSize >> 30) + 2} GiB free in {temp}, has {free >> 30} GiB");

    var dir = Path.Combine(temp, "cwb_darbig_" + Guid.NewGuid().ToString("N")[..10]);
    Directory.CreateDirectory(dir);
    try {
      var source = Path.Combine(dir, "big.bin");
      byte[] sourceHash;
      using (var fs = File.Create(source))
      using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)) {
        var block = new byte[1 << 20];
        new Random(11).NextBytes(block.AsSpan(0, block.Length / 2)); // half noise, half zeros
        for (var i = 0; i < FileSize / block.Length; ++i) {
          block[^1] = (byte)i;
          block[0] = (byte)(i >> 8);
          fs.Write(block);
          sha.AppendData(block);
        }
        sourceHash = sha.GetHashAndReset();
      }

      var basePath = Path.Combine(dir, "big");
      var options = new FormatCreateOptions {
        MethodName = method,
        FormatSpecific = { [DarFormatDescriptor.TapeMarksOption] = tapeMarks.ToString(), [DarFormatDescriptor.SliceSizeOption] = sliceSize },
      };
      var before = GC.GetTotalMemory(forceFullCollection: true);
      var peakBefore = Process.GetCurrentProcess().PeakWorkingSet64;
      var watch = Stopwatch.StartNew();
      using (var fs = File.Create(basePath + ".1.dar"))
        new DarFormatDescriptor().Create(fs, [new ArchiveInputInfo(source, "big.bin", false)], options);
      TestContext.Out.WriteLine($"{method}: written in {watch.Elapsed}");
      var peakAfter = Process.GetCurrentProcess().PeakWorkingSet64;
      Assert.That(peakAfter - peakBefore, Is.LessThan(1L << 30), "writing must not hold the input in memory");
      Assert.That(GC.GetTotalMemory(forceFullCollection: true) - before, Is.LessThan(256L << 20));

      var slices = Enumerable.Range(1, 100).Select(n => $"{basePath}.{n}.dar").TakeWhile(File.Exists).ToArray();
      if (sliceSize.Length > 0)
        Assert.That(slices, Has.Length.GreaterThan(1));

      if (FsInteropToolbox.WslAvailable && FsInteropToolbox.WslHasTool("dar")) {
        var test = FsInteropToolbox.RunWsl($"dar -Q -t {FsInteropToolbox.WinToWsl(basePath)}");
        Assert.That(test.ExitCode, Is.EqualTo(0), $"dar -t refused the 5 GiB archive:\n{test.StdOut}\n{test.StdErr}");
      }

      watch.Restart();
      var outDir = Path.Combine(dir, "out");
      using (var fs = File.OpenRead(slices[^1]))
        new DarFormatDescriptor().Extract(fs, outDir, null, null);
      TestContext.Out.WriteLine($"{method}: extracted in {watch.Elapsed}");
      var restored = Path.Combine(outDir, "big.bin");
      Assert.That(new FileInfo(restored).Length, Is.EqualTo(FileSize));
      using (var fs = File.OpenRead(restored))
        Assert.That(SHA256.HashData(fs), Is.EqualTo(sourceHash));
    } finally {
      try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
  }
}
