using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using FileFormat.Chm;

namespace Compression.Tests.Chm;

/// <summary>
/// CHM files compiled by Microsoft's HTML Help compiler, against 7-Zip. <c>7-zip.chm</c> (7-Zip 26.04's own
/// help: LZX window 2^17, reset every 4 frames) is committed with 7-Zip's extraction of it
/// (<c>7zip-listing.txt</c>: path, size, SHA-256). CHMs installed on the machine are compared with 7-Zip
/// live. See ReferenceVectors/README.md.
/// </summary>
[TestFixture]
public class ChmRealFileTests {

  private static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"ChmVectors.{name}")
      ?? throw new FileNotFoundException($"Embedded CHM vector '{name}' is missing from the test assembly.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

  [Test, Category("ThirdPartyVector")]
  public void Given_HhcCompiledChm_When_EveryFileExtracted_Then_MatchesSevenZip() {
    var r = new ChmReader(new MemoryStream(Vector("7-zip.chm")));
    var byPath = r.Entries.ToDictionary(e => e.Path.TrimStart('/'), StringComparer.Ordinal);
    var rows = Encoding.ASCII.GetString(Vector("7zip-listing.txt"))
      .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(l => l.Split(' ')).ToList();
    Assert.That(rows, Has.Count.EqualTo(94));
    Assert.That(rows.Count(row => byPath[row[0]].Section == 1), Is.GreaterThan(70), "most of the help is LZX-compressed");
    foreach (var row in rows) {
      var data = r.Extract(byPath[row[0]]);
      Assert.That(data.Length.ToString(), Is.EqualTo(row[1]), row[0]);
      Assert.That(Sha(data), Is.EqualTo(row[2]), row[0]);
    }
  }

  private static ChmEntry Meta(byte[] chm, string path)
    => new ChmReader(new MemoryStream(chm)).Entries.Single(e => e.Path == path);

  private const string ResetTablePath =
    "::DataSpace/Storage/MSCompressed/Transform/{7FC28940-9D31-11D0-9B27-00A0C91E9C7C}/InstanceData/ResetTable";

  [Test, Category("EdgeCase")]
  public void Given_ResetTableListingTooFewFrames_When_LzxEntryExtracted_Then_InvalidData() {
    var bytes = Vector("7-zip.chm");
    var table = Meta(bytes, ResetTablePath);
    var at = (int)(FindContentOffset(bytes) + table.Offset) + 4; // numEntries
    BitConverter.GetBytes(1u).CopyTo(bytes, at);
    var r = new ChmReader(new MemoryStream(bytes));
    Assert.Throws<InvalidDataException>(() => r.Extract(r.Entries.First(e => e.Section == 1 && e.Size > 0)));
  }

  [Test, Category("EdgeCase")]
  public void Given_ControlDataWithoutLzxcMagic_When_LzxEntryExtracted_Then_InvalidData() {
    var bytes = Vector("7-zip.chm");
    var ctrl = Meta(bytes, "::DataSpace/Storage/MSCompressed/ControlData");
    bytes[(int)(FindContentOffset(bytes) + ctrl.Offset) + 4] = (byte)'X';
    var r = new ChmReader(new MemoryStream(bytes));
    Assert.Throws<InvalidDataException>(() => r.Extract(r.Entries.First(e => e.Section == 1 && e.Size > 0)));
  }

  [Test, Category("EdgeCase")]
  public void Given_FrameOffsetPastTheContent_When_LzxEntryExtracted_Then_InvalidData() {
    var bytes = Vector("7-zip.chm");
    var table = Meta(bytes, ResetTablePath);
    var firstOffset = (int)(FindContentOffset(bytes) + table.Offset) + 40;
    BitConverter.GetBytes(0x7FFFFFFFUL).CopyTo(bytes, firstOffset);
    var r = new ChmReader(new MemoryStream(bytes));
    Assert.Throws<InvalidDataException>(() => r.Extract(r.Entries.First(e => e.Section == 1 && e.Size > 0)));
  }

  private static long FindContentOffset(byte[] chm) => (long)BitConverter.ToUInt64(chm, 0x58);

  // ── live: every CHM installed on this machine against 7-Zip ──────────

  private static IEnumerable<string> InstalledChms() {
    var roots = new List<string>();
    if (Environment.GetEnvironmentVariable("CWB_CHM_DIR") is { Length: > 0 } env) roots.Add(env);
    if (OperatingSystem.IsWindows()) {
      roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Help"));
      roots.Add(@"D:\PortableApps\7-ZipPortable\App\7-Zip64");
    }
    foreach (var root in roots.Where(Directory.Exists))
      foreach (var f in Directory.EnumerateFiles(root, "*.chm", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Take(40))
        yield return f;
  }

  [Test, Category("EndToEnd")]
  public void Given_InstalledChms_When_ExtractedByUsAndSevenZip_Then_Identical() {
    if (!FsInteropToolbox.SevenZipAvailable) Assert.Ignore("7-Zip not available.");
    var chms = InstalledChms().ToList();
    if (chms.Count == 0) Assert.Ignore("No CHM found (set CWB_CHM_DIR).");
    var work = Path.Combine(Path.GetTempPath(), $"cwb_chm_{Guid.NewGuid():N}");
    try {
      var compared = 0;
      foreach (var chm in chms) {
        var dir = Path.Combine(work, compared.ToString());
        var (_, _, exit) = FsInteropToolbox.Run7z($"x -y -o\"{dir}\" \"{chm}\"");
        if (exit != 0) continue; // 7-Zip itself refuses it; nothing to compare against
        var r = new ChmReader(File.OpenRead(chm));
        var byPath = r.Entries.ToDictionary(e => e.Path.TrimStart('/'), StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) {
          var rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
          Assert.That(byPath.TryGetValue(rel, out var entry), Is.True, $"{chm}: {rel} not listed");
          Assert.That(Sha(r.Extract(entry!)), Is.EqualTo(Sha(File.ReadAllBytes(file))), $"{chm}: {rel}");
        }
        ++compared;
      }
      Assert.That(compared, Is.GreaterThan(0));
    } finally {
      if (Directory.Exists(work)) Directory.Delete(work, true);
    }
  }

  [TestCase(1)]
  [TestCase(32768)]
  [TestCase(65536 + 7)]
  [Category("EndToEnd")]
  public void Given_LzxSectionAtFrameBoundary_When_ExtractedBySevenZip_Then_Identical(int length) {
    if (!FsInteropToolbox.SevenZipAvailable) Assert.Ignore("7-Zip not available.");
    var work = Path.Combine(Path.GetTempPath(), $"cwb_chmb_{Guid.NewGuid():N}");
    Directory.CreateDirectory(work);
    try {
      var data = Enumerable.Range(0, length).Select(i => (byte)(i % 251)).ToArray();
      var w = new ChmWriter();
      w.AddFile("/data.bin", data);
      var path = Path.Combine(work, "ours.chm");
      using (var fs = File.Create(path)) w.WriteTo(fs, useLzx: true);
      Assert.That(new ChmReader(new MemoryStream(File.ReadAllBytes(path))).Extract(
        new ChmReader(new MemoryStream(File.ReadAllBytes(path))).Entries.Single(e => e.Path == "/data.bin")), Is.EqualTo(data));
      var (stdout, _, exit) = FsInteropToolbox.Run7z($"x -y -o\"{Path.Combine(work, "out")}\" \"{path}\"");
      Assert.That(exit, Is.Zero, stdout);
      Assert.That(File.ReadAllBytes(Path.Combine(work, "out", "data.bin")), Is.EqualTo(data));
    } finally {
      Directory.Delete(work, true);
    }
  }

  [TestCase(true)]
  [TestCase(false)]
  [Category("EndToEnd")]
  public void Given_OurChm_When_ExtractedBySevenZip_Then_Identical(bool lzx) {
    if (!FsInteropToolbox.SevenZipAvailable) Assert.Ignore("7-Zip not available.");
    var work = Path.Combine(Path.GetTempPath(), $"cwb_chmw_{Guid.NewGuid():N}");
    Directory.CreateDirectory(work);
    try {
      var big = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 9000).Select(i => $"<p>line {i}</p>\n")));
      var w = new ChmWriter();
      w.AddFile("/index.html", "<html>hi</html>"u8.ToArray());
      w.AddFile("/big.html", big);
      var path = Path.Combine(work, "ours.chm");
      using (var fs = File.Create(path)) w.WriteTo(fs, useLzx: lzx);
      var (stdout, _, exit) = FsInteropToolbox.Run7z($"x -y -o\"{Path.Combine(work, "out")}\" \"{path}\"");
      Assert.That(exit, Is.Zero, stdout);
      Assert.That(File.ReadAllBytes(Path.Combine(work, "out", "big.html")), Is.EqualTo(big));
      Assert.That(File.ReadAllText(Path.Combine(work, "out", "index.html")), Is.EqualTo("<html>hi</html>"));
    } finally {
      Directory.Delete(work, true);
    }
  }
}
