#pragma warning disable CS1591
using System.Security.Cryptography;
using Compression.Registry;
using FileSystem.ProDos;

namespace Compression.Tests.ProDos;

/// <summary>
/// CiderPress II (<c>cp2</c>, Apache-2.0) extracts what the ProDOS writer and in-place
/// editor produce, and must get every file back byte-exact. Skips unless <c>cp2</c> is on
/// the WSL PATH (the linux-x64 self-contained release of CiderPress II 1.2.0 runs as is).
/// </summary>
[TestFixture]
[Category("ExternalFsInterop")]
[Category("Wsl")]
public sealed class ProDosCiderPressTests {

  private static byte[] Pattern(int size, int seed) {
    var data = new byte[size];
    for (var i = 0; i < size; ++i) data[i] = (byte)(i * 29 + seed + (i >> 9));
    return data;
  }

  [Test]
  public void GivenAVolumeTheWriterMadeAndTheEditorExtended_WhenCiderPressExtractsIt_ThenEveryFileComesBackByteExact() {
    if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool("cp2"))
      Assert.Ignore("cp2 (CiderPress II) is not on the WSL PATH.");
    var files = new Dictionary<string, byte[]> {
      ["SEEDLING"] = Pattern(300, 1),
      ["SAPLING"] = Pattern(9_000, 2),
      ["TREE"] = Pattern(140_000, 3),
      ["NOTE"] ="written by the writer\r"u8.ToArray(),
    };
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_cp2_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try {
      var d = new ProDosFormatDescriptor();
      using var ms = new MemoryStream();
      d.Create(ms, files.Select(f => ArchiveInputInfo.InMemory(f.Key, f.Value)).ToList(), new FormatCreateOptions());
      var added = Pattern(1_234, 4);
      d.Add(ms, [ArchiveInputInfo.InMemory("ADDED", added)]);
      files["ADDED"] = added;
      var img = Path.Combine(dir, "w.po");
      File.WriteAllBytes(img, ms.ToArray());

      var outDir = Path.Combine(dir, "out");
      Directory.CreateDirectory(outDir);
      var r = FsInteropToolbox.RunWsl($"cd {FsInteropToolbox.WinToWsl(outDir)} && cp2 extract {FsInteropToolbox.WinToWsl(img)} 2>&1; cp2 catalog {FsInteropToolbox.WinToWsl(img)} 2>&1");
      Assert.That(r.ExitCode, Is.Zero, r.StdOut + r.StdErr);
      foreach (var (name, data) in files) {
        var path = Path.Combine(outDir, name.Replace('/', Path.DirectorySeparatorChar));
        Assert.That(File.Exists(path), Is.True, $"{name} not extracted:\n{r.StdOut}");
        Assert.That(SHA256.HashData(File.ReadAllBytes(path)), Is.EqualTo(SHA256.HashData(data)), name);
      }
      Assert.That(r.StdOut, Does.Not.Contain("damage").IgnoreCase.And.Not.Contain("warning").IgnoreCase, r.StdOut);
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }
}
