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
  public void Build_WritesTheTapeBlockFieldsWhereMtf100aPutsThem() {
    var bkf = BkfWriter.Build([File("a.txt", "x"u8.ToArray())]);
    Assert.Multiple(() => {
      Assert.That(Encoding.ASCII.GetString(bkf, 0, 4), Is.EqualTo("TAPE"));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(bkf.AsSpan(84)), Is.EqualTo(FlbSize), "Format Logical Block Size at 84");
      Assert.That(bkf[93], Is.EqualTo(1), "MTF major version at 93");
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(bkf.AsSpan(56)) & 1, Is.EqualTo(1), "soft filemarks");
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(bkf.AsSpan(80)), Is.GreaterThan(0), "software name");
      Assert.That(bkf[48], Is.EqualTo(2), "Unicode strings");
      Assert.That(bkf.Length % FlbSize, Is.EqualTo(0), "whole Format Logical Blocks");
    });
  }

  [Test, Category("HappyPath")]
  public void Build_LaysOutTapeFilemarkDataSetFilemarkEsetFilemark() {
    var bkf = BkfWriter.Build([File("a.txt", "x"u8.ToArray()), File("d/b.txt", "y"u8.ToArray())]);
    Assert.That(Blocks(bkf).Select(b => b.Type), Is.EqualTo(new[] {
      "TAPE", "SFMB", "SSET", "VOLB", "DIRB", "FILE", "DIRB", "FILE", "SFMB", "ESET", "SFMB",
    }));
  }

  [Test, Category("HappyPath")]
  public void Build_SealsEveryHeaderAndNumbersTheDataSet() {
    var bkf = BkfWriter.Build([File("a.txt", new byte[3000]), File("d/b.txt", "y"u8.ToArray())]);
    var blocks = Blocks(bkf);
    var sset = blocks.First(b => b.Type == "SSET").Offset;
    var inSet = blocks.SkipWhile(b => b.Type != "SSET").TakeWhile(b => b.Type != "SFMB").ToList();
    Assert.Multiple(() => {
      foreach (var b in blocks)
        Assert.That(Xor16(bkf.AsSpan(b.Offset, 50)), Is.EqualTo(BinaryPrimitives.ReadUInt16LittleEndian(bkf.AsSpan(b.Offset + 50))), $"{b.Type} at {b.Offset}: header checksum");
      for (var i = 0; i < inSet.Count; ++i) {
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(bkf.AsSpan(inSet[i].Offset + 36)), Is.EqualTo((uint)i), $"{inSet[i].Type}: Control Block ID");
        Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(bkf.AsSpan(inSet[i].Offset + 20)), Is.EqualTo((ulong)((inSet[i].Offset - sset) / FlbSize)), $"{inSet[i].Type}: Format Logical Address");
      }
      var stan = bkf.AsSpan().IndexOf("STAN"u8);
      Assert.That(Xor16(bkf.AsSpan(stan, 20)), Is.EqualTo(BinaryPrimitives.ReadUInt16LittleEndian(bkf.AsSpan(stan + 20))), "STAN stream header checksum");
      Assert.That(stan % 4, Is.Zero, "streams on 4-byte boundaries");
    });
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
    Assert.That(Blocks(bkf).Select(b => b.Type), Is.EqualTo(new[] { "TAPE", "SFMB", "SSET", "VOLB", "DIRB", "SFMB", "ESET", "SFMB" }));
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
  public void Build_InvalidFlbSize_Throws([Values(0, 256, 1000, 2048, 131072)] int flb) {
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

  private static ushort Xor16(ReadOnlySpan<byte> bytes) {
    ushort x = 0;
    for (var i = 0; i + 1 < bytes.Length; i += 2) x ^= BinaryPrimitives.ReadUInt16LittleEndian(bytes[i..]);
    return x;
  }

  /// <summary>The DBLKs on Format Logical Block boundaries whose header checksum holds.</summary>
  private static List<(string Type, int Offset)> Blocks(byte[] bkf) {
    var result = new List<(string, int)>();
    for (var pos = 0; pos + 52 <= bkf.Length; pos += FlbSize)
      if (Xor16(bkf.AsSpan(pos, 50)) == BinaryPrimitives.ReadUInt16LittleEndian(bkf.AsSpan(pos + 50)) && bkf[pos] is >= (byte)'A' and <= (byte)'Z')
        result.Add((Encoding.ASCII.GetString(bkf, pos, 4), pos));
    return result;
  }
}
