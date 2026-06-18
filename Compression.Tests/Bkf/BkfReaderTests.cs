using System.Buffers.Binary;
using System.Text;

namespace Compression.Tests.Bkf;

/// <summary>
/// Read-only Microsoft NTBackup (MTF) tests. Constructs synthetic MTF streams
/// via <see cref="MtfBuilder"/> and verifies the reader walks DBLKs and
/// extracts STAN payloads.
/// </summary>
[TestFixture]
public class BkfReaderTests {

  private const int FlbSize = 1024;
  private const int CbhSize = 52;
  private const int StreamHdr = 22;

  [Test, Category("HappyPath")]
  public void Detection_TapeMagicAtOffsetZero() {
    var desc = new FileFormat.Bkf.BkfFormatDescriptor();
    Assert.That(desc.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(desc.MagicSignatures[0].Offset, Is.EqualTo(0));
    Assert.That(desc.MagicSignatures[0].Bytes, Is.EqualTo("TAPE"u8.ToArray()));
    Assert.That(desc.Id, Is.EqualTo("Bkf"));
    Assert.That(desc.Extensions, Does.Contain(".bkf"));
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_SingleFile_InRootSet() {
    var content = "Hello, MTF!"u8.ToArray();
    var bkf = new MtfBuilder()
      .AddTape()
      .AddSset()
      .AddVolb()
      .AddFile("test.txt", content)
      .AddEset()
      .AddEotm()
      .Build();

    using var ms = new MemoryStream(bkf);
    var r = new FileFormat.Bkf.BkfReader(ms);
    var files = r.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(files, Has.Count.EqualTo(1));
    Assert.That(files[0].Name, Is.EqualTo("test.txt"));
    Assert.That(files[0].Size, Is.EqualTo(content.Length));
    Assert.That(r.Extract(files[0]), Is.EqualTo(content));
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_MultipleFiles_InRootSet() {
    var data1 = "Alpha"u8.ToArray();
    var data2 = "Bravo data"u8.ToArray();
    var bkf = new MtfBuilder()
      .AddTape().AddSset().AddVolb()
      .AddFile("a.txt", data1)
      .AddFile("b.bin", data2)
      .AddEset().AddEotm()
      .Build();

    using var ms = new MemoryStream(bkf);
    var r = new FileFormat.Bkf.BkfReader(ms);
    var files = r.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(files, Has.Count.EqualTo(2));
    Assert.That(files[0].Name, Is.EqualTo("a.txt"));
    Assert.That(files[1].Name, Is.EqualTo("b.bin"));
    Assert.That(r.Extract(files[0]), Is.EqualTo(data1));
    Assert.That(r.Extract(files[1]), Is.EqualTo(data2));
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_FileUnderDirectory() {
    var content = "nested"u8.ToArray();
    var bkf = new MtfBuilder()
      .AddTape().AddSset().AddVolb()
      .AddDirb("subdir")
      .AddFile("inner.txt", content)
      .AddEset().AddEotm()
      .Build();

    using var ms = new MemoryStream(bkf);
    var r = new FileFormat.Bkf.BkfReader(ms);
    var dirs = r.Entries.Where(e => e.IsDirectory).ToList();
    var files = r.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(dirs, Has.Count.EqualTo(1));
    Assert.That(dirs[0].Name, Does.Contain("subdir"));
    Assert.That(files, Has.Count.EqualTo(1));
    Assert.That(files[0].Name, Is.EqualTo("subdir/inner.txt"));
    Assert.That(r.Extract(files[0]), Is.EqualTo(content));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_And_Extract() {
    var content = "descriptor listing"u8.ToArray();
    var bkf = new MtfBuilder()
      .AddTape().AddSset().AddVolb()
      .AddFile("doc.txt", content)
      .AddEset().AddEotm()
      .Build();

    using var ms = new MemoryStream(bkf);
    var desc = new FileFormat.Bkf.BkfFormatDescriptor();
    var entries = desc.List(ms, null);
    Assert.That(entries.Any(e => e.Name == "doc.txt" && !e.IsDirectory), Is.True);

    using var ms2 = new MemoryStream(bkf);
    var outDir = Path.Combine(Path.GetTempPath(), "bkf_test_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      desc.Extract(ms2, outDir, null, null);
      var extracted = File.ReadAllBytes(Path.Combine(outDir, "doc.txt"));
      Assert.That(extracted, Is.EqualTo(content));
    } finally {
      if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
    }
  }

  [Test, Category("EdgeCase")]
  public void EmptyFile_RoundTrips() {
    var bkf = new MtfBuilder()
      .AddTape().AddSset().AddVolb()
      .AddFile("empty.bin", [])
      .AddEset().AddEotm()
      .Build();

    using var ms = new MemoryStream(bkf);
    var r = new FileFormat.Bkf.BkfReader(ms);
    var files = r.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(files, Has.Count.EqualTo(1));
    Assert.That(files[0].Name, Is.EqualTo("empty.bin"));
    Assert.That(files[0].Size, Is.EqualTo(0));
    Assert.That(r.Extract(files[0]), Is.Empty);
  }

  [Test, Category("EdgeCase")]
  public void LargeFile_SpansMultipleLogicalBlocks() {
    var content = new byte[5000];
    for (var i = 0; i < content.Length; ++i) content[i] = (byte)(i & 0xFF);
    var bkf = new MtfBuilder()
      .AddTape().AddSset().AddVolb()
      .AddFile("big.bin", content)
      .AddEset().AddEotm()
      .Build();

    using var ms = new MemoryStream(bkf);
    var r = new FileFormat.Bkf.BkfReader(ms);
    var files = r.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(files, Has.Count.EqualTo(1));
    Assert.That(r.Extract(files[0]), Is.EqualTo(content));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_FileTooSmall_Throws() {
    using var ms = new MemoryStream(new byte[10]);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Bkf.BkfReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_MissingTapeMagic_Throws() {
    var data = new byte[1024];
    Encoding.ASCII.GetBytes("XXXX").CopyTo(data, 0);
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Bkf.BkfReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_NullStream_Throws() {
    Assert.Throws<ArgumentNullException>(() => _ = new FileFormat.Bkf.BkfReader(null!));
  }

  [Test, Category("ErrorHandling")]
  public void Extract_NullEntry_Throws() {
    var bkf = new MtfBuilder().AddTape().AddSset().AddVolb()
      .AddFile("f.txt", "x"u8.ToArray()).AddEset().AddEotm().Build();
    using var ms = new MemoryStream(bkf);
    var r = new FileFormat.Bkf.BkfReader(ms);
    Assert.Throws<ArgumentNullException>(() => r.Extract(null!));
  }

  [Test, Category("EquivalenceClass")]
  public void Descriptor_Capabilities_ListExtractModify() {
    var desc = new FileFormat.Bkf.BkfFormatDescriptor();
    Assert.That(desc.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanList), Is.True);
    Assert.That(desc.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanExtract), Is.True);
    Assert.That(desc.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True);
    Assert.That(desc.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanModify), Is.True);
    Assert.That(desc, Is.InstanceOf<Compression.Registry.IArchiveModifiable>());
  }

  // ── Synthetic MTF builder ───────────────────────────────────────────────

  private sealed class MtfBuilder {
    private readonly MemoryStream _ms = new();

    public MtfBuilder AddTape() {
      var block = new byte[FlbSize];
      WriteCbh(block, "TAPE", stringType: 1);
      // FLB size at offset 52 (uint32 LE)
      BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(52), FlbSize);
      _ms.Write(block, 0, block.Length);
      return this;
    }

    public MtfBuilder AddSset() => this.AddContainer("SSET");
    public MtfBuilder AddVolb() => this.AddContainer("VOLB");
    public MtfBuilder AddEset() => this.AddContainer("ESET");

    public MtfBuilder AddEotm() {
      var block = new byte[FlbSize];
      WriteCbh(block, "EOTM", stringType: 0);
      _ms.Write(block, 0, block.Length);
      return this;
    }

    public MtfBuilder AddDirb(string path) {
      var block = new byte[FlbSize];
      WriteCbh(block, "DIRB", stringType: 1);
      // Attach a PNAM stream right after CBH.
      var nameBytes = Encoding.Latin1.GetBytes(path);
      WriteStream(block, CbhSize, "PNAM", nameBytes);
      _ms.Write(block, 0, block.Length);
      return this;
    }

    public MtfBuilder AddFile(string name, byte[] content) {
      // FILE blocks: CBH (52) + FNAM stream + STAN stream. Round total up to FLB multiple.
      var nameBytes = Encoding.Latin1.GetBytes(name);
      var fnamFootprint = StreamFootprint(nameBytes.Length);
      var stanFootprint = StreamFootprint(content.Length);
      var rawSize = CbhSize + fnamFootprint + stanFootprint;
      var paddedSize = RoundUp(rawSize, FlbSize);
      var block = new byte[paddedSize];

      WriteCbh(block, "FILE", stringType: 1);
      var afterFnam = WriteStream(block, CbhSize, "FNAM", nameBytes);
      WriteStream(block, afterFnam, "STAN", content);

      _ms.Write(block, 0, block.Length);
      return this;
    }

    public byte[] Build() => _ms.ToArray();

    private MtfBuilder AddContainer(string type) {
      var block = new byte[FlbSize];
      WriteCbh(block, type, stringType: 1);
      _ms.Write(block, 0, block.Length);
      return this;
    }

    private static void WriteCbh(byte[] block, string blockType, ushort stringType) {
      Encoding.ASCII.GetBytes(blockType).CopyTo(block, 0);
      // Block attrs = 0 at [4..8]
      // OffsetToFirstEvent (CbhSize) at [8..10]
      BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(8), CbhSize);
      // OS_ID = 14 (NT) at [10], OS_Ver = 1 at [11]
      block[10] = 14;
      block[11] = 1;
      // String type at offset 46
      BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(46), stringType);
      // Checksum at [50..52] — left zero; the reader does not verify.
    }

    private static int WriteStream(byte[] block, int offset, string streamId, byte[] payload) {
      Encoding.ASCII.GetBytes(streamId).CopyTo(block, offset);
      // FS attrs (0), Media attrs (0) at [4..8] of stream header.
      // Length at offset+8 (uint64 LE)
      BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(offset + 8), (ulong)payload.Length);
      // Encryption (0), Compression (0), Checksum (0) at [16..22].
      var dataStart = offset + StreamHdr;
      if (payload.Length > 0) Array.Copy(payload, 0, block, dataStart, payload.Length);
      var end = dataStart + payload.Length;
      return RoundUp(end, 4);
    }

    private static int StreamFootprint(int payloadLength) => RoundUp(StreamHdr + payloadLength, 4);

    private static int RoundUp(int value, int alignment) {
      var rem = value % alignment;
      return rem == 0 ? value : value + (alignment - rem);
    }
  }
}
