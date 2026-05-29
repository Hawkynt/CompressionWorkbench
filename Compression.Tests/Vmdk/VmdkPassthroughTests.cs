using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.Vmdk;

/// <summary>
/// Tests that sparse VMDK containers transparently pass-through R/W and
/// defrag operations to the inner filesystem via <see cref="FileFormat.Vmdk.VmdkStream"/>.
/// </summary>
[TestFixture]
public class VmdkPassthroughTests {

  [OneTimeSetUp]
  public void EnsureRegistry() => FormatRegistration.EnsureInitialized();

  // ── List / Extract pass-through ────────────────────────────────────

  [Test, Category("HappyPath")]
  public void List_SparseVmdk_SeesInnerFatFiles() {
    using var vmdk = BuildSparseVmdkWithFat("HELLO.TXT", "world"u8.ToArray(),
                                             "DATA.BIN", new byte[] { 1, 2, 3 });
    var desc = new FileFormat.Vmdk.VmdkFormatDescriptor();
    var entries = desc.List(vmdk, null);

    Assert.That(entries, Has.Count.GreaterThanOrEqualTo(2));
    Assert.That(entries.Any(e => e.Name.Contains("HELLO", StringComparison.OrdinalIgnoreCase)), Is.True,
      "Should see HELLO.TXT from inner FAT");
    Assert.That(entries.Any(e => e.Name.Contains("DATA", StringComparison.OrdinalIgnoreCase)), Is.True,
      "Should see DATA.BIN from inner FAT");
  }

  [Test, Category("HappyPath")]
  public void Extract_SparseVmdk_ExtractsInnerFatFiles() {
    using var vmdk = BuildSparseVmdkWithFat("HELLO.TXT", "world"u8.ToArray());
    var desc = new FileFormat.Vmdk.VmdkFormatDescriptor();
    var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    try {
      Directory.CreateDirectory(tmpDir);
      desc.Extract(vmdk, tmpDir, null, null);
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
  public void Add_SparseVmdk_AddsFileToInnerFat() {
    using var vmdk = BuildSparseVmdkWithFat("EXIST.TXT", "existing"u8.ToArray());
    var desc = new FileFormat.Vmdk.VmdkFormatDescriptor();
    var modifiable = (IArchiveModifiable)desc;

    var tmpFile = WriteTempBytes("new content"u8.ToArray());
    try {
      modifiable.Add(vmdk, [new ArchiveInputInfo(tmpFile, "NEW.TXT", false)]);

      vmdk.Position = 0;
      var entries = desc.List(vmdk, null);
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
  public void Remove_SparseVmdk_RemovesFileFromInnerFat() {
    using var vmdk = BuildSparseVmdkWithFat("KEEP.TXT", "keep"u8.ToArray(),
                                             "REMOVE.TXT", "remove"u8.ToArray());
    var desc = new FileFormat.Vmdk.VmdkFormatDescriptor();
    var modifiable = (IArchiveModifiable)desc;

    modifiable.Remove(vmdk, ["REMOVE.TXT"]);

    vmdk.Position = 0;
    var entries = desc.List(vmdk, null);
    Assert.That(entries.Any(e => e.Name.Contains("KEEP", StringComparison.OrdinalIgnoreCase)), Is.True,
      "KEEP.TXT should survive");
    Assert.That(entries.Any(e => e.Name.Contains("REMOVE", StringComparison.OrdinalIgnoreCase)), Is.False,
      "REMOVE.TXT should be gone");
  }

  // ── Defragment pass-through ───────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Defragment_SparseVmdk_DefragmentsInnerFat() {
    using var vmdk = BuildSparseVmdkWithFat("A.TXT", "aaa"u8.ToArray(),
                                             "B.TXT", "bbb"u8.ToArray());
    var desc = new FileFormat.Vmdk.VmdkFormatDescriptor();
    var defrag = (IArchiveDefragmentable)desc;

    defrag.Defragment(vmdk);

    vmdk.Position = 0;
    var entries = desc.List(vmdk, null);
    Assert.That(entries.Count, Is.GreaterThanOrEqualTo(2));
    Assert.That(entries.Any(e => e.Name.Contains("A", StringComparison.OrdinalIgnoreCase)), Is.True);
    Assert.That(entries.Any(e => e.Name.Contains("B", StringComparison.OrdinalIgnoreCase)), Is.True);
  }

  // ── IFilesystemExtentMap pass-through ─────────────────────────────

  [Test, Category("HappyPath")]
  public void EnumerateExtents_SparseVmdk_DelegatesToInnerFs() {
    using var vmdk = BuildSparseVmdkWithFat("FILE.TXT", "data"u8.ToArray());
    var desc = new FileFormat.Vmdk.VmdkFormatDescriptor();
    var extentMap = (IFilesystemExtentMap)desc;

    var extents = extentMap.EnumerateExtents(vmdk).ToList();
    Assert.That(extents, Has.Count.GreaterThan(0), "Should emit extents from inner FS");
    Assert.That(extents.Any(e => e.Kind == DefragBlockKind.Used), Is.True,
      "Should have at least one Used block");
  }

  // ── Fallback when inner FS not detected ───────────────────────────

  [Test, Category("HappyPath")]
  public void List_SparseVmdk_NoInnerFs_FallsBackToRawEntry() {
    var rawData = new byte[4096];
    new Random(42).NextBytes(rawData);
    var w = new FileFormat.Vmdk.VmdkWriter();
    w.SetDiskData(rawData);
    var vmdkBytes = w.Build();
    using var vmdk = new MemoryStream(vmdkBytes);
    vmdk.Position = 0;

    var desc = new FileFormat.Vmdk.VmdkFormatDescriptor();
    var entries = desc.List(vmdk, null);

    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("disk.img"));
  }

  // ── Helpers ────────────────────────────────────────────────────────

  private static MemoryStream BuildSparseVmdkWithFat(params object[] nameDataPairs) {
    var fatWriter = new FileSystem.Fat.FatWriter();
    for (var i = 0; i < nameDataPairs.Length; i += 2) {
      var name = (string)nameDataPairs[i];
      var data = (byte[])nameDataPairs[i + 1];
      fatWriter.AddFile(name, data);
    }
    var fatImage = fatWriter.Build();

    var vmdkWriter = new FileFormat.Vmdk.VmdkWriter();
    vmdkWriter.SetDiskData(fatImage);
    var vmdkBytes = vmdkWriter.Build();
    var ms = new MemoryStream();
    ms.Write(vmdkBytes);
    ms.Position = 0;
    return ms;
  }

  private static string WriteTempBytes(byte[] data) {
    var path = Path.GetTempFileName();
    File.WriteAllBytes(path, data);
    return path;
  }
}
