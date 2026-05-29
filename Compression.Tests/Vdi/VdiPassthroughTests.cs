using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.Vdi;

/// <summary>
/// Tests that VDI containers transparently pass-through R/W and
/// defrag operations to the inner filesystem via <see cref="FileFormat.Vdi.VdiStream"/>.
/// </summary>
[TestFixture]
public class VdiPassthroughTests {

  [OneTimeSetUp]
  public void EnsureRegistry() => FormatRegistration.EnsureInitialized();

  // ── List / Extract pass-through ────────────────────────────────────

  [Test, Category("HappyPath")]
  public void List_Vdi_SeesInnerFatFiles() {
    using var vdi = BuildVdiWithFat("HELLO.TXT", "world"u8.ToArray(),
                                     "DATA.BIN", new byte[] { 1, 2, 3 });
    var desc = new FileFormat.Vdi.VdiFormatDescriptor();
    var entries = desc.List(vdi, null);

    Assert.That(entries, Has.Count.GreaterThanOrEqualTo(2));
    Assert.That(entries.Any(e => e.Name.Contains("HELLO", StringComparison.OrdinalIgnoreCase)), Is.True,
      "Should see HELLO.TXT from inner FAT");
    Assert.That(entries.Any(e => e.Name.Contains("DATA", StringComparison.OrdinalIgnoreCase)), Is.True,
      "Should see DATA.BIN from inner FAT");
  }

  [Test, Category("HappyPath")]
  public void Extract_Vdi_ExtractsInnerFatFiles() {
    using var vdi = BuildVdiWithFat("HELLO.TXT", "world"u8.ToArray());
    var desc = new FileFormat.Vdi.VdiFormatDescriptor();
    var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    try {
      Directory.CreateDirectory(tmpDir);
      desc.Extract(vdi, tmpDir, null, null);
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
  public void Add_Vdi_AddsFileToInnerFat() {
    using var vdi = BuildVdiWithFat("EXIST.TXT", "existing"u8.ToArray());
    var desc = new FileFormat.Vdi.VdiFormatDescriptor();
    var modifiable = (IArchiveModifiable)desc;

    var tmpFile = WriteTempBytes("new content"u8.ToArray());
    try {
      modifiable.Add(vdi, [new ArchiveInputInfo(tmpFile, "NEW.TXT", false)]);

      vdi.Position = 0;
      var entries = desc.List(vdi, null);
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
  public void Remove_Vdi_RemovesFileFromInnerFat() {
    using var vdi = BuildVdiWithFat("KEEP.TXT", "keep"u8.ToArray(),
                                     "REMOVE.TXT", "remove"u8.ToArray());
    var desc = new FileFormat.Vdi.VdiFormatDescriptor();
    var modifiable = (IArchiveModifiable)desc;

    modifiable.Remove(vdi, ["REMOVE.TXT"]);

    vdi.Position = 0;
    var entries = desc.List(vdi, null);
    Assert.That(entries.Any(e => e.Name.Contains("KEEP", StringComparison.OrdinalIgnoreCase)), Is.True,
      "KEEP.TXT should survive");
    Assert.That(entries.Any(e => e.Name.Contains("REMOVE", StringComparison.OrdinalIgnoreCase)), Is.False,
      "REMOVE.TXT should be gone");
  }

  // ── Defragment pass-through ───────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Defragment_Vdi_DefragmentsInnerFat() {
    using var vdi = BuildVdiWithFat("A.TXT", "aaa"u8.ToArray(),
                                     "B.TXT", "bbb"u8.ToArray());
    var desc = new FileFormat.Vdi.VdiFormatDescriptor();
    var defrag = (IArchiveDefragmentable)desc;

    defrag.Defragment(vdi);

    vdi.Position = 0;
    var entries = desc.List(vdi, null);
    Assert.That(entries.Count, Is.GreaterThanOrEqualTo(2));
    Assert.That(entries.Any(e => e.Name.Contains("A", StringComparison.OrdinalIgnoreCase)), Is.True);
    Assert.That(entries.Any(e => e.Name.Contains("B", StringComparison.OrdinalIgnoreCase)), Is.True);
  }

  // ── IFilesystemExtentMap pass-through ─────────────────────────────

  [Test, Category("HappyPath")]
  public void EnumerateExtents_Vdi_DelegatesToInnerFs() {
    using var vdi = BuildVdiWithFat("FILE.TXT", "data"u8.ToArray());
    var desc = new FileFormat.Vdi.VdiFormatDescriptor();
    var extentMap = (IFilesystemExtentMap)desc;

    var extents = extentMap.EnumerateExtents(vdi).ToList();
    Assert.That(extents, Has.Count.GreaterThan(0), "Should emit extents from inner FS");
    Assert.That(extents.Any(e => e.Kind == DefragBlockKind.Used), Is.True,
      "Should have at least one Used block");
  }

  // ── Fallback when inner FS not detected ───────────────────────────

  [Test, Category("HappyPath")]
  public void List_Vdi_NoInnerFs_FallsBackToRawEntry() {
    var rawData = new byte[4096];
    new Random(42).NextBytes(rawData);
    using var ms = new MemoryStream();
    using var w = new FileFormat.Vdi.VdiWriter(ms, leaveOpen: true, virtualSize: rawData.Length);
    w.Write(rawData);
    var vdiBytes = ms.ToArray();
    using var vdi = new MemoryStream(vdiBytes);
    vdi.Position = 0;

    var desc = new FileFormat.Vdi.VdiFormatDescriptor();
    var entries = desc.List(vdi, null);

    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("disk.img"));
  }

  // ── Helpers ────────────────────────────────────────────────────────

  private static MemoryStream BuildVdiWithFat(params object[] nameDataPairs) {
    var fatWriter = new FileSystem.Fat.FatWriter();
    for (var i = 0; i < nameDataPairs.Length; i += 2) {
      var name = (string)nameDataPairs[i];
      var data = (byte[])nameDataPairs[i + 1];
      fatWriter.AddFile(name, data);
    }
    var fatImage = fatWriter.Build();

    var ms = new MemoryStream();
    using var w = new FileFormat.Vdi.VdiWriter(ms, leaveOpen: true, virtualSize: fatImage.Length);
    w.Write(fatImage);
    ms.Position = 0;
    return ms;
  }

  private static string WriteTempBytes(byte[] data) {
    var path = Path.GetTempFileName();
    File.WriteAllBytes(path, data);
    return path;
  }
}
