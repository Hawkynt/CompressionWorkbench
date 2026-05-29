using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.Vhd;

/// <summary>
/// Tests that VHD containers transparently pass-through R/W and defrag
/// operations to the inner filesystem. A fixed VHD wrapping a FAT image
/// is the primary test case.
/// </summary>
[TestFixture]
public class VhdPassthroughTests {

  [OneTimeSetUp]
  public void EnsureRegistry() => FormatRegistration.EnsureInitialized();

  // ── List / Extract pass-through ────────────────────────────────────

  [Test, Category("HappyPath")]
  public void List_FixedVhd_SeesInnerFatFiles() {
    using var vhd = BuildFixedVhdWithFat("HELLO.TXT", "world"u8.ToArray(),
                                          "DATA.BIN", new byte[] { 1, 2, 3 });
    var desc = new FileFormat.Vhd.VhdFormatDescriptor();
    var entries = desc.List(vhd, null);

    Assert.That(entries, Has.Count.GreaterThanOrEqualTo(2));
    Assert.That(entries.Any(e => e.Name.Contains("HELLO", StringComparison.OrdinalIgnoreCase)), Is.True,
      "Should see HELLO.TXT from inner FAT");
    Assert.That(entries.Any(e => e.Name.Contains("DATA", StringComparison.OrdinalIgnoreCase)), Is.True,
      "Should see DATA.BIN from inner FAT");
  }

  [Test, Category("HappyPath")]
  public void Extract_FixedVhd_ExtractsInnerFatFiles() {
    using var vhd = BuildFixedVhdWithFat("HELLO.TXT", "world"u8.ToArray());
    var desc = new FileFormat.Vhd.VhdFormatDescriptor();
    var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    try {
      Directory.CreateDirectory(tmpDir);
      desc.Extract(vhd, tmpDir, null, null);
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
  public void Add_FixedVhd_AddsFileToInnerFat() {
    using var vhd = BuildFixedVhdWithFat("EXIST.TXT", "existing"u8.ToArray());
    var desc = new FileFormat.Vhd.VhdFormatDescriptor();
    var modifiable = (IArchiveModifiable)desc;

    var tmpFile = WriteTempBytes("new content"u8.ToArray());
    try {
      modifiable.Add(vhd, [new ArchiveInputInfo(tmpFile, "NEW.TXT", false)]);

      // Re-list should now show both files
      vhd.Position = 0;
      var entries = desc.List(vhd, null);
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
  public void Remove_FixedVhd_RemovesFileFromInnerFat() {
    using var vhd = BuildFixedVhdWithFat("KEEP.TXT", "keep"u8.ToArray(),
                                          "REMOVE.TXT", "remove"u8.ToArray());
    var desc = new FileFormat.Vhd.VhdFormatDescriptor();
    var modifiable = (IArchiveModifiable)desc;

    modifiable.Remove(vhd, ["REMOVE.TXT"]);

    vhd.Position = 0;
    var entries = desc.List(vhd, null);
    Assert.That(entries.Any(e => e.Name.Contains("KEEP", StringComparison.OrdinalIgnoreCase)), Is.True,
      "KEEP.TXT should survive");
    Assert.That(entries.Any(e => e.Name.Contains("REMOVE", StringComparison.OrdinalIgnoreCase)), Is.False,
      "REMOVE.TXT should be gone");
  }

  // ── Defragment pass-through ───────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Defragment_FixedVhd_DefragmentsInnerFat() {
    using var vhd = BuildFixedVhdWithFat("A.TXT", "aaa"u8.ToArray(),
                                          "B.TXT", "bbb"u8.ToArray());
    var desc = new FileFormat.Vhd.VhdFormatDescriptor();
    var defrag = (IArchiveDefragmentable)desc;

    // Defragment should not throw and the VHD should remain valid
    defrag.Defragment(vhd);

    // Verify still readable
    vhd.Position = 0;
    var entries = desc.List(vhd, null);
    Assert.That(entries.Count, Is.GreaterThanOrEqualTo(2));
    Assert.That(entries.Any(e => e.Name.Contains("A", StringComparison.OrdinalIgnoreCase)), Is.True);
    Assert.That(entries.Any(e => e.Name.Contains("B", StringComparison.OrdinalIgnoreCase)), Is.True);
  }

  // ── VHD validity after operations ─────────────────────────────────

  [Test, Category("HappyPath")]
  public void VhdRemainsValidAfterModification() {
    using var vhd = BuildFixedVhdWithFat("ORIG.TXT", "original"u8.ToArray());
    var desc = new FileFormat.Vhd.VhdFormatDescriptor();
    var modifiable = (IArchiveModifiable)desc;

    var tmpFile = WriteTempBytes("added"u8.ToArray());
    try {
      modifiable.Add(vhd, [new ArchiveInputInfo(tmpFile, "ADDED.TXT", false)]);

      // Verify VHD container is still valid: footer at EOF should have "conectix"
      vhd.Position = vhd.Length - 512;
      var footer = new byte[8];
      vhd.ReadExactly(footer);
      Assert.That(System.Text.Encoding.ASCII.GetString(footer), Is.EqualTo("conectix"),
        "VHD footer should remain intact after inner FS modification");

      // Verify disk size hasn't changed
      vhd.Position = 0;
      var reader = new FileFormat.Vhd.VhdReader(vhd);
      Assert.That(reader.Entries, Has.Count.EqualTo(1));
    } finally {
      File.Delete(tmpFile);
    }
  }

  // ── IFilesystemExtentMap pass-through ─────────────────────────────

  [Test, Category("HappyPath")]
  public void EnumerateExtents_FixedVhd_DelegatesToInnerFs() {
    using var vhd = BuildFixedVhdWithFat("FILE.TXT", "data"u8.ToArray());
    var desc = new FileFormat.Vhd.VhdFormatDescriptor();
    var extentMap = (IFilesystemExtentMap)desc;

    var extents = extentMap.EnumerateExtents(vhd).ToList();
    Assert.That(extents, Has.Count.GreaterThan(0), "Should emit extents from inner FS");
    Assert.That(extents.Any(e => e.Kind == DefragBlockKind.Used), Is.True,
      "Should have at least one Used block");
  }

  // ── Fallback to raw when inner FS not detected ────────────────────

  [Test, Category("HappyPath")]
  public void List_FixedVhd_NoInnerFs_FallsBackToRawEntry() {
    // Create a VHD with random data (not a valid filesystem)
    var rawData = new byte[4096];
    new Random(42).NextBytes(rawData);
    var w = new FileFormat.Vhd.VhdWriter();
    w.SetDiskData(rawData);
    var vhdBytes = w.Build();
    using var vhd = new MemoryStream(vhdBytes);
    vhd.Position = 0;

    var desc = new FileFormat.Vhd.VhdFormatDescriptor();
    var entries = desc.List(vhd, null);

    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("disk.img"));
  }

  // ── Existing VHD tests still pass (regression guard) ──────────────

  [Test, Category("RoundTrip")]
  public void ExistingRoundTrip_FixedVhd_StillWorks() {
    var data = new byte[512 * 10];
    new Random(42).NextBytes(data);
    var w = new FileFormat.Vhd.VhdWriter();
    w.SetDiskData(data);
    var vhd = w.Build();

    Assert.That(vhd.Length, Is.EqualTo(data.Length + 512));

    using var ms = new MemoryStream(vhd);
    var r = new FileFormat.Vhd.VhdReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("disk.img"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(data.Length));
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  // ── Helpers ────────────────────────────────────────────────────────

  /// <summary>
  /// Builds a fixed VHD containing a FAT filesystem with the given files.
  /// Returns a writable MemoryStream positioned at 0.
  /// </summary>
  private static MemoryStream BuildFixedVhdWithFat(params object[] nameDataPairs) {
    var fatWriter = new FileSystem.Fat.FatWriter();
    for (var i = 0; i < nameDataPairs.Length; i += 2) {
      var name = (string)nameDataPairs[i];
      var data = (byte[])nameDataPairs[i + 1];
      fatWriter.AddFile(name, data);
    }
    var fatImage = fatWriter.Build();

    var vhdWriter = new FileFormat.Vhd.VhdWriter();
    vhdWriter.SetDiskData(fatImage);
    var vhdBytes = vhdWriter.Build();
    var ms = new MemoryStream();
    ms.Write(vhdBytes);
    ms.Position = 0;
    return ms;
  }

  private static string WriteTempBytes(byte[] data) {
    var path = Path.GetTempFileName();
    File.WriteAllBytes(path, data);
    return path;
  }
}
