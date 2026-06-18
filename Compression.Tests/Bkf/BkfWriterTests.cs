using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.Bkf;

namespace Compression.Tests.Bkf;

/// <summary>
/// Writer tests for the BKF (Microsoft Tape Format) descriptor. Every test
/// proves the primary gate: <see cref="BkfWriter"/> output round-trips through
/// <see cref="BkfReader"/> with byte-identical payloads and correct paths.
/// </summary>
[TestFixture]
public class BkfWriterTests {

  private const int FlbSize = 1024;

  private static BkfWriter.Item File(string path, byte[] data) => new(path, data, IsDirectory: false);
  private static BkfWriter.Item Dir(string path) => new(path, [], IsDirectory: true);

  private static BkfReader Read(byte[] bkf) {
    var ms = new MemoryStream(bkf);
    return new BkfReader(ms);
  }

  // ── Structure / spec conformance ──────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Build_StartsWithTapeMagic_AndDeclaresFlb() {
    var bkf = BkfWriter.Build([File("a.txt", "x"u8.ToArray())]);
    Assert.That(Encoding.ASCII.GetString(bkf, 0, 4), Is.EqualTo("TAPE"));
    var flb = BinaryPrimitives.ReadUInt32LittleEndian(bkf.AsSpan(52));
    Assert.That(flb, Is.EqualTo(FlbSize));
    Assert.That(bkf.Length % FlbSize, Is.EqualTo(0), "Output must be a whole number of FLBs.");
  }

  [Test, Category("HappyPath")]
  public void Build_EmitsBlockChainInSpecOrder() {
    var bkf = BkfWriter.Build([File("a.txt", "x"u8.ToArray())]);
    var types = ScanDblkTypes(bkf);
    // TAPE, SSET, VOLB, FILE, ESET, EOTM
    Assert.That(types, Is.EqualTo(new[] { "TAPE", "SSET", "VOLB", "FILE", "ESET", "EOTM" }));
  }

  [Test, Category("HappyPath")]
  public void Build_LastBlockIsEotm() {
    var bkf = BkfWriter.Build([File("a.txt", "x"u8.ToArray())]);
    var lastPos = bkf.Length - FlbSize;
    Assert.That(Encoding.ASCII.GetString(bkf, lastPos, 4), Is.EqualTo("EOTM"));
  }

  // ── Round-trip: primary gate ──────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var content = "Hello, MTF writer!"u8.ToArray();
    var r = Read(BkfWriter.Build([File("test.txt", content)]));
    var files = r.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(files, Has.Count.EqualTo(1));
    Assert.That(files[0].Name, Is.EqualTo("test.txt"));
    Assert.That(files[0].Size, Is.EqualTo(content.Length));
    Assert.That(r.Extract(files[0]), Is.EqualTo(content));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_MultipleFiles_PreserveOrderAndBytes() {
    var d1 = "Alpha"u8.ToArray();
    var d2 = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01 };
    var d3 = "third file"u8.ToArray();
    var r = Read(BkfWriter.Build([File("a.txt", d1), File("b.bin", d2), File("c.txt", d3)]));
    var files = r.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(files.Select(f => f.Name), Is.EqualTo(new[] { "a.txt", "b.bin", "c.txt" }));
    Assert.That(r.Extract(files[0]), Is.EqualTo(d1));
    Assert.That(r.Extract(files[1]), Is.EqualTo(d2));
    Assert.That(r.Extract(files[2]), Is.EqualTo(d3));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_FileUnderDirectory() {
    var content = "nested payload"u8.ToArray();
    var r = Read(BkfWriter.Build([File("subdir/inner.txt", content)]));
    var files = r.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(files, Has.Count.EqualTo(1));
    Assert.That(files[0].Name, Is.EqualTo("subdir/inner.txt"));
    Assert.That(r.Extract(files[0]), Is.EqualTo(content));
    Assert.That(r.Entries.Any(e => e.IsDirectory && e.Name.Contains("subdir")), Is.True);
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_DeeplyNestedDirectory() {
    var content = "deep"u8.ToArray();
    var r = Read(BkfWriter.Build([File("a/b/c/file.dat", content)]));
    var files = r.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(files, Has.Count.EqualTo(1));
    Assert.That(files[0].Name, Is.EqualTo("a/b/c/file.dat"));
    Assert.That(r.Extract(files[0]), Is.EqualTo(content));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_BackslashSeparatorsAreNormalised() {
    var content = "winpath"u8.ToArray();
    var r = Read(BkfWriter.Build([File(@"docs\report.txt", content)]));
    var files = r.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(files, Has.Count.EqualTo(1));
    Assert.That(files[0].Name, Is.EqualTo("docs/report.txt"));
    Assert.That(r.Extract(files[0]), Is.EqualTo(content));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_MultipleDirsAndRootFiles() {
    var r = Read(BkfWriter.Build([
      File("root1.txt", "r1"u8.ToArray()),
      File("dir1/a.txt", "d1a"u8.ToArray()),
      File("dir1/b.txt", "d1b"u8.ToArray()),
      File("dir2/c.txt", "d2c"u8.ToArray()),
      File("root2.txt", "r2"u8.ToArray()),
    ]));
    var byName = r.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Name);
    Assert.Multiple(() => {
      Assert.That(Encoding.ASCII.GetString(r.Extract(byName["root1.txt"])), Is.EqualTo("r1"));
      Assert.That(Encoding.ASCII.GetString(r.Extract(byName["dir1/a.txt"])), Is.EqualTo("d1a"));
      Assert.That(Encoding.ASCII.GetString(r.Extract(byName["dir1/b.txt"])), Is.EqualTo("d1b"));
      Assert.That(Encoding.ASCII.GetString(r.Extract(byName["dir2/c.txt"])), Is.EqualTo("d2c"));
      Assert.That(Encoding.ASCII.GetString(r.Extract(byName["root2.txt"])), Is.EqualTo("r2"));
    });
  }

  // ── Boundary / edge cases ─────────────────────────────────────────────

  [Test, Category("EdgeCase")]
  public void EmptyBackup_NoFiles_RoundTrips() {
    var bkf = BkfWriter.Build([]);
    var types = ScanDblkTypes(bkf);
    Assert.That(types, Is.EqualTo(new[] { "TAPE", "SSET", "VOLB", "ESET", "EOTM" }));
    var r = Read(bkf);
    Assert.That(r.Entries.Where(e => !e.IsDirectory), Is.Empty);
  }

  [Test, Category("EdgeCase")]
  public void EmptyFilePayload_RoundTrips() {
    var r = Read(BkfWriter.Build([File("empty.bin", [])]));
    var files = r.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(files, Has.Count.EqualTo(1));
    Assert.That(files[0].Size, Is.EqualTo(0));
    Assert.That(r.Extract(files[0]), Is.Empty);
  }

  [Test, Category("EdgeCase")]
  public void ExplicitEmptyDirectory_EmitsDirb() {
    var r = Read(BkfWriter.Build([Dir("emptydir")]));
    Assert.That(r.Entries.Any(e => e.IsDirectory && e.Name.Contains("emptydir")), Is.True);
    Assert.That(r.Entries.Where(e => !e.IsDirectory), Is.Empty);
  }

  [Test, Category("BoundaryValue")]
  public void LargeFile_SpanningMultipleFlbs_RoundTrips() {
    var content = new byte[50_000];
    for (var i = 0; i < content.Length; ++i) content[i] = (byte)((i * 31 + 7) & 0xFF);
    var bkf = BkfWriter.Build([File("big.bin", content)]);
    var r = Read(bkf);
    var files = r.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(files, Has.Count.EqualTo(1));
    Assert.That(files[0].Size, Is.EqualTo(content.Length));
    Assert.That(r.Extract(files[0]), Is.EqualTo(content));
  }

  [Test, Category("BoundaryValue")]
  public void PayloadExactlyOneFlb_RoundTrips() {
    // A payload sized so the FILE DBLK lands right on an FLB boundary stresses padding.
    var content = new byte[FlbSize];
    for (var i = 0; i < content.Length; ++i) content[i] = (byte)i;
    var r = Read(BkfWriter.Build([File("exact.bin", content)]));
    var files = r.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(r.Extract(files[0]), Is.EqualTo(content));
  }

  [Test, Category("BoundaryValue")]
  public void PayloadSizesAroundAlignment_RoundTrip([Values(1, 2, 3, 4, 5, 21, 22, 23, 1023, 1025)] int size) {
    var content = new byte[size];
    for (var i = 0; i < size; ++i) content[i] = (byte)(i ^ 0x5A);
    var r = Read(BkfWriter.Build([File("f.bin", content)]));
    var files = r.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(r.Extract(files[0]), Is.EqualTo(content));
  }

  // ── Exception handling ────────────────────────────────────────────────

  [Test, Category("ErrorHandling")]
  public void Build_NullItems_Throws() {
    Assert.Throws<ArgumentNullException>(() => BkfWriter.Build(null!));
  }

  [Test, Category("ErrorHandling")]
  public void Build_InvalidFlbSize_Throws([Values(0, 256, 1000, 131072)] int flb) {
    Assert.Throws<ArgumentOutOfRangeException>(() => BkfWriter.Build([], flb));
  }

  [Test, Category("EquivalenceClass")]
  public void Build_CustomFlb_512_RoundTrips() {
    var content = new byte[1500];
    for (var i = 0; i < content.Length; ++i) content[i] = (byte)(i & 0xFF);
    var bkf = BkfWriter.Build([File("x.bin", content)], 512);
    var r = Read(bkf);
    Assert.That(r.LogicalBlockSize, Is.EqualTo(512));
    var files = r.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(r.Extract(files[0]), Is.EqualTo(content));
  }

  // ── Descriptor surface ────────────────────────────────────────────────

  [Test, Category("EquivalenceClass")]
  public void Descriptor_AdvertisesCanCreate() {
    var d = new BkfFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_Create_RoundTripsThroughListAndExtract() {
    var d = new BkfFormatDescriptor();
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("readme.txt", "read me"u8.ToArray()),
      ArchiveInputInfo.InMemory("data/values.bin", new byte[] { 1, 2, 3, 4, 5 }),
    };

    using var ms = new MemoryStream();
    ((IArchiveCreatable)d).Create(ms, inputs, new FormatCreateOptions());

    ms.Position = 0;
    var listed = d.List(ms, null);
    Assert.That(listed.Select(e => e.Name), Does.Contain("readme.txt"));
    Assert.That(listed.Select(e => e.Name), Does.Contain("data/values.bin"));

    ms.Position = 0;
    var outDir = Path.Combine(Path.GetTempPath(), "bkf_w_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      d.Extract(ms, outDir, null, null);
      Assert.That(System.IO.File.ReadAllText(Path.Combine(outDir, "readme.txt")), Is.EqualTo("read me"));
      Assert.That(System.IO.File.ReadAllBytes(Path.Combine(outDir, "data", "values.bin")),
        Is.EqualTo(new byte[] { 1, 2, 3, 4, 5 }));
    } finally {
      if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
    }
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_Create_ThenModify_RoundTrips() {
    var d = new BkfFormatDescriptor();
    using var ms = new MemoryStream();
    ((IArchiveCreatable)d).Create(ms,
      [ArchiveInputInfo.InMemory("first.txt", "one"u8.ToArray())],
      new FormatCreateOptions());

    ((IArchiveModifiable)d).Add(ms, [ArchiveInputInfo.InMemory("second.bin", new byte[] { 9, 8, 7 })]);

    ms.Position = 0;
    var names = d.List(ms, null).Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("first.txt"));
    Assert.That(names, Does.Contain("second.bin"));
  }

  // ── Helpers ───────────────────────────────────────────────────────────

  /// <summary>Walks the FLB-aligned DBLK chain and returns each block's 4CC type.</summary>
  private static string[] ScanDblkTypes(byte[] bkf) {
    var types = new List<string>();
    for (var pos = 0; pos + 4 <= bkf.Length; pos += FlbSize) {
      var t = Encoding.ASCII.GetString(bkf, pos, 4);
      types.Add(t);
      if (t == "EOTM") break;
    }
    return types.ToArray();
  }
}
