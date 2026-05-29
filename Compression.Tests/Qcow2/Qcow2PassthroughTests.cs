using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.Qcow2;

/// <summary>
/// Tests that QCOW2 containers transparently pass-through R/W and
/// defrag operations to the inner filesystem via <see cref="FileFormat.Qcow2.Qcow2Stream"/>.
/// </summary>
[TestFixture]
public class Qcow2PassthroughTests {

  [OneTimeSetUp]
  public void EnsureRegistry() => FormatRegistration.EnsureInitialized();

  // ── List / Extract pass-through ────────────────────────────────────

  [Test, Category("HappyPath")]
  public void List_Qcow2_SeesInnerFatFiles() {
    using var qcow2 = BuildQcow2WithFat("HELLO.TXT", "world"u8.ToArray(),
                                         "DATA.BIN", new byte[] { 1, 2, 3 });
    var desc = new FileFormat.Qcow2.Qcow2FormatDescriptor();
    var entries = desc.List(qcow2, null);

    Assert.That(entries, Has.Count.GreaterThanOrEqualTo(2));
    Assert.That(entries.Any(e => e.Name.Contains("HELLO", StringComparison.OrdinalIgnoreCase)), Is.True,
      "Should see HELLO.TXT from inner FAT");
    Assert.That(entries.Any(e => e.Name.Contains("DATA", StringComparison.OrdinalIgnoreCase)), Is.True,
      "Should see DATA.BIN from inner FAT");
  }

  [Test, Category("HappyPath")]
  public void Extract_Qcow2_ExtractsInnerFatFiles() {
    using var qcow2 = BuildQcow2WithFat("HELLO.TXT", "world"u8.ToArray());
    var desc = new FileFormat.Qcow2.Qcow2FormatDescriptor();
    var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    try {
      Directory.CreateDirectory(tmpDir);
      desc.Extract(qcow2, tmpDir, null, null);
      var extracted = Directory.GetFiles(tmpDir, "*", SearchOption.AllDirectories);
      Assert.That(extracted, Has.Length.GreaterThanOrEqualTo(1), "Should extract at least one file");
      var helloFile = extracted.FirstOrDefault(f => Path.GetFileName(f)
        .Contains("HELLO", StringComparison.OrdinalIgnoreCase));
      Assert.That(helloFile, Is.Not.Null, "Should find HELLO.TXT");
      Assert.That(File.ReadAllBytes(helloFile!), Is.EqualTo("world"u8.ToArray()));
    } finally {
      if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
    }
  }

  // ── Add pass-through ──────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Add_Qcow2_AddsFileToInnerFat() {
    using var qcow2 = BuildQcow2WithFat("EXIST.TXT", "existing"u8.ToArray());
    var desc = new FileFormat.Qcow2.Qcow2FormatDescriptor();
    var modifiable = (IArchiveModifiable)desc;

    var tmpFile = WriteTempBytes("new content"u8.ToArray());
    try {
      modifiable.Add(qcow2, [new ArchiveInputInfo(tmpFile, "NEW.TXT", false)]);

      qcow2.Position = 0;
      var entries = desc.List(qcow2, null);
      Assert.That(entries.Any(e => e.Name.Contains("EXIST", StringComparison.OrdinalIgnoreCase)), Is.True,
        "Original file should still be present");
      Assert.That(entries.Any(e => e.Name.Contains("NEW", StringComparison.OrdinalIgnoreCase)), Is.True,
        "Newly added file should be present");
    } finally {
      File.Delete(tmpFile);
    }
  }

  // ── Remove pass-through ───────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Remove_Qcow2_RemovesFileFromInnerFat() {
    using var qcow2 = BuildQcow2WithFat("KEEP.TXT", "keep"u8.ToArray(),
                                         "REMOVE.TXT", "remove"u8.ToArray());
    var desc = new FileFormat.Qcow2.Qcow2FormatDescriptor();
    var modifiable = (IArchiveModifiable)desc;

    modifiable.Remove(qcow2, ["REMOVE.TXT"]);

    qcow2.Position = 0;
    var entries = desc.List(qcow2, null);
    Assert.That(entries.Any(e => e.Name.Contains("KEEP", StringComparison.OrdinalIgnoreCase)), Is.True,
      "KEEP.TXT should survive");
    Assert.That(entries.Any(e => e.Name.Contains("REMOVE", StringComparison.OrdinalIgnoreCase)), Is.False,
      "REMOVE.TXT should be gone");
  }

  // ── Defragment pass-through ───────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Defragment_Qcow2_DefragmentsInnerFat() {
    using var qcow2 = BuildQcow2WithFat("A.TXT", "aaa"u8.ToArray(),
                                         "B.TXT", "bbb"u8.ToArray());
    var desc = new FileFormat.Qcow2.Qcow2FormatDescriptor();
    var defrag = (IArchiveDefragmentable)desc;

    defrag.Defragment(qcow2);

    qcow2.Position = 0;
    var entries = desc.List(qcow2, null);
    Assert.That(entries.Count, Is.GreaterThanOrEqualTo(2));
    Assert.That(entries.Any(e => e.Name.Contains("A", StringComparison.OrdinalIgnoreCase)), Is.True);
    Assert.That(entries.Any(e => e.Name.Contains("B", StringComparison.OrdinalIgnoreCase)), Is.True);
  }

  // ── IFilesystemExtentMap pass-through ─────────────────────────────

  [Test, Category("HappyPath")]
  public void EnumerateExtents_Qcow2_DelegatesToInnerFs() {
    using var qcow2 = BuildQcow2WithFat("FILE.TXT", "data"u8.ToArray());
    var desc = new FileFormat.Qcow2.Qcow2FormatDescriptor();
    var extentMap = (IFilesystemExtentMap)desc;

    var extents = extentMap.EnumerateExtents(qcow2).ToList();
    Assert.That(extents, Has.Count.GreaterThan(0), "Should emit extents from inner FS");
    Assert.That(extents.Any(e => e.Kind == DefragBlockKind.Used), Is.True,
      "Should have at least one Used block");
  }

  // ── Fallback when inner FS not detected ───────────────────────────

  [Test, Category("HappyPath")]
  public void List_Qcow2_NoInnerFs_FallsBackToRawEntry() {
    var rawData = new byte[4096];
    new Random(42).NextBytes(rawData);
    var w = new FileFormat.Qcow2.Qcow2Writer();
    w.SetDiskImage(rawData);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    var qcow2Bytes = ms.ToArray();
    using var qcow2 = new MemoryStream(qcow2Bytes);
    qcow2.Position = 0;

    var desc = new FileFormat.Qcow2.Qcow2FormatDescriptor();
    var entries = desc.List(qcow2, null);

    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("disk.img"));
  }

  // ── Helpers ────────────────────────────────────────────────────────

  private static MemoryStream BuildQcow2WithFat(params object[] nameDataPairs) {
    var fatWriter = new FileSystem.Fat.FatWriter();
    for (var i = 0; i < nameDataPairs.Length; i += 2) {
      var name = (string)nameDataPairs[i];
      var data = (byte[])nameDataPairs[i + 1];
      fatWriter.AddFile(name, data);
    }
    var fatImage = fatWriter.Build();

    var qcow2Writer = new FileFormat.Qcow2.Qcow2Writer();
    qcow2Writer.SetDiskImage(fatImage);
    var ms = new MemoryStream();
    qcow2Writer.WriteTo(ms);
    ms.Position = 0;
    return ms;
  }

  private static string WriteTempBytes(byte[] data) {
    var path = Path.GetTempFileName();
    File.WriteAllBytes(path, data);
    return path;
  }
}
