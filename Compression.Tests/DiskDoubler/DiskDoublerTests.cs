using System.Buffers.Binary;
using Compression.Core.Checksums;
using Compression.Core.Streams;
using FileFormat.Compress;
using FileFormat.DiskDoubler;

namespace Compression.Tests.DiskDoubler;

[TestFixture]
public class DiskDoublerTests {

  // ── Builders ──────────────────────────────────────────────────────────────────

  /// <summary>Builds a DiskDoubler file in the real 84-byte layout around already-encoded forks.</summary>
  internal static byte[] BuildDdFile(
      byte dataMethod, byte[] dataBytes, uint dataSize,
      byte rsrcMethod = 0, byte[]? rsrcBytes = null, uint rsrcSize = 0,
      ushort dataChecksum = 0, ushort dataDelta = 0, byte info1 = 0x2A, bool zeroHeaderCrc = false) {
    rsrcBytes ??= [];
    var hdr = new byte[84];
    BinaryPrimitives.WriteUInt32BigEndian(hdr, 0xABCD0054);
    BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(4), dataSize);
    BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(8), (uint)dataBytes.Length);
    BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(12), rsrcSize);
    BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(16), (uint)rsrcBytes.Length);
    hdr[20] = dataMethod;
    hdr[21] = rsrcMethod;
    hdr[22] = info1;
    "TEXT"u8.CopyTo(hdr.AsSpan(32));
    "ttxt"u8.CopyTo(hdr.AsSpan(36));
    BinaryPrimitives.WriteUInt16BigEndian(hdr.AsSpan(48), dataChecksum);
    BinaryPrimitives.WriteUInt16BigEndian(hdr.AsSpan(54), dataDelta);
    if (!zeroHeaderCrc)
      BinaryPrimitives.WriteUInt16BigEndian(hdr.AsSpan(82), Crc16Ccitt.Compute(hdr.AsSpan(0, 82)));
    return [.. hdr, .. dataBytes, .. rsrcBytes];
  }

  private static DiskDoublerReader Open(byte[] file, string name = "doc") => new(new MemoryStream(file), name: name);

  /// <summary>Encodes a method-1 fork: a .Z stream of the XOR-masked data, its header masked too.</summary>
  private static (byte[] Fork, ushort Checksum) CompressFork(byte[] data) {
    var masked = data.Select(b => (byte)(b ^ 0x5A)).ToArray();
    using var ms = new MemoryStream();
    using (var z = new CompressStream(ms, CompressionStreamMode.Compress, leaveOpen: true))
      z.Write(masked);
    var fork = ms.ToArray();
    var checksum = (ushort)(data.Sum(b => (int)b) + fork[0] + fork[1] + fork[2]);
    for (var i = 0; i < 3; ++i) fork[i] ^= 0x5A;
    return (fork, checksum);
  }

  /// <summary>Builds one "AD" block header for a block body of <paramref name="compSize"/> bytes.</summary>
  private static byte[] AdBlockHeader(int compSize, int uncompSize, byte flags) {
    var h = new byte[12];
    BinaryPrimitives.WriteUInt16BigEndian(h, (ushort)compSize);
    BinaryPrimitives.WriteUInt16BigEndian(h.AsSpan(2), (ushort)uncompSize);
    h[9] = flags;
    byte x = 0;
    for (var i = 0; i < 11; ++i) x ^= h[i];
    h[11] = x;
    return h;
  }

  private static byte[] PackBits(string bits) {
    bits = bits.Replace(" ", "");
    var result = new byte[(bits.Length + 7) / 8];
    for (var i = 0; i < bits.Length; ++i)
      if (bits[i] == '1') result[i >> 3] |= (byte)(0x80 >> (i & 7));
    return result;
  }

  // ── Header ────────────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Writer_Layout_MagicSizesAndHeaderCrc() {
    var payload = "hello diskdoubler"u8.ToArray();
    var w = new DiskDoublerWriter();
    w.SetFile(payload, "TEXT", "ttxt");
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    var file = ms.ToArray();

    Assert.That(file, Has.Length.EqualTo(84 + payload.Length));
    Assert.That(BinaryPrimitives.ReadUInt32BigEndian(file), Is.EqualTo(0xABCD0054u));
    Assert.That(BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(4)), Is.EqualTo((uint)payload.Length));
    Assert.That(BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(8)), Is.EqualTo((uint)payload.Length));
    Assert.That(file[20], Is.Zero, "stored");
    Assert.That(BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(82)), Is.EqualTo(Crc16Ccitt.Compute(file.AsSpan(0, 82))));
    Assert.That(file.AsSpan(84).SequenceEqual(payload), Is.True);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_Reader_RoundTrip() {
    var payload = new byte[4096];
    new Random(3).NextBytes(payload);
    var w = new DiskDoublerWriter();
    w.SetFile(payload);
    using var ms = new MemoryStream();
    w.WriteTo(ms);

    using var r = Open(ms.ToArray(), "test.txt");
    var e = r.Entries.Single();
    Assert.That((e.Name, e.IsDataFork, e.Method, e.OriginalSize), Is.EqualTo(("test.txt", true, 0, 4096L)));
    Assert.That(r.Extract(e), Is.EqualTo(payload));
  }

  [Test, Category("HappyPath")]
  public void Reader_TakesNameFromFileWithoutDdExtension() {
    var path = Path.Combine(Path.GetTempPath(), $"cwb_dd_{Guid.NewGuid():N}.txt.dd");
    try {
      File.WriteAllBytes(path, BuildDdFile(0, [1, 2, 3], 3));
      using var r = new DiskDoublerReader(File.OpenRead(path));
      Assert.That(r.Entries[0].Name, Is.EqualTo(Path.GetFileName(path)[..^3]));
    } finally {
      File.Delete(path);
    }
  }

  [Test, Category("HappyPath")]
  public void Reader_BothForks_SecondFollowsFirst() {
    var file = BuildDdFile(0, "data"u8.ToArray(), 4, 0, "resource"u8.ToArray(), 8);
    using var r = Open(file);
    Assert.That(r.Entries.Select(e => e.Name), Is.EqualTo(new[] { "doc", "doc.rsrc" }));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo("data"u8.ToArray()));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo("resource"u8.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void Reader_ResourceForkOnly_HasNoDataEntry() {
    using var r = Open(BuildDdFile(0, [], 0, 0, "rsrc"u8.ToArray(), 4));
    Assert.That(r.Entries.Single().IsDataFork, Is.False);
  }

  [Test, Category("HappyPath")]
  public void Reader_MacBinaryWrapped_IsFound() {
    var file = new byte[128].Concat(BuildDdFile(0, "wrapped"u8.ToArray(), 7)).ToArray();
    Assert.That(DiskDoublerReader.IsDiskDoubler(file), Is.True);
    using var r = Open(file);
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo("wrapped"u8.ToArray()));
  }

  [Test, Category("EdgeCase")]
  public void Reader_ZeroHeaderCrc_AcceptedAsOldFile() {
    using var r = Open(BuildDdFile(0, [9], 1, zeroHeaderCrc: true));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(new byte[] { 9 }));
  }

  [Test, Category("Exception")]
  public void Reader_WrongHeaderCrc_Throws() {
    var file = BuildDdFile(0, [9], 1);
    file[40] ^= 0x01; // Finder flags
    Assert.Throws<InvalidDataException>(() => _ = Open(file));
  }

  [Test, Category("Exception")]
  [TestCase(new byte[0])]
  [TestCase(new byte[] { 0xAB, 0xCD, 0x00, 0x54 })]
  [TestCase(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 })]
  public void Reader_NotDiskDoubler_Throws(byte[] bytes) =>
    Assert.Throws<InvalidDataException>(() => _ = Open(bytes));

  [Test, Category("Exception")]
  public void Reader_ForksPastEndOfFile_Throws() {
    var file = BuildDdFile(0, new byte[100], 100);
    Assert.Throws<InvalidDataException>(() => _ = Open(file[..^1]));
  }

  // ── Methods ───────────────────────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void Method1_Compress_DecodesAndChecksSum() {
    var data = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("DiskDoubler compress fork. ", 50)));
    var (fork, checksum) = CompressFork(data);
    using var r = Open(BuildDdFile(1, fork, (uint)data.Length, dataChecksum: checksum));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("Exception")]
  public void Method1_WrongChecksum_Throws() {
    var data = "some bytes to compress"u8.ToArray();
    var (fork, checksum) = CompressFork(data);
    using var r = Open(BuildDdFile(1, fork, (uint)data.Length, dataChecksum: (ushort)(checksum + 1)));
    Assert.Throws<InvalidDataException>(() => r.Extract(r.Entries[0]));
  }

  [Test, Category("HappyPath")]
  [TestCase((byte)6)]
  [TestCase((byte)9)]
  public void MethodAd_StoredAndCompressedBlocks(byte method) {
    // Block 1 stored: "ABC". Block 2 compressed: literal 'x', literal 'y', match distance 2 length 4
    // ("xyxy" overlapping), literal 'z'  →  "xyxyxyz".
    var block2 = PackBits(
      "0 01111000" +       // literal 'x'
      "0 01111001" +       // literal 'y'
      "1 0 00000010 101" + // match, near (8-bit) distance 2, length 4
      "0 01111010");       // literal 'z'
    var fork = AdBlockHeader(3, 3, 0x01).Concat("ABC"u8.ToArray())
      .Concat(AdBlockHeader(block2.Length, 7, 0x08)).Concat(block2).ToArray();

    using var r = Open(BuildDdFile(method, fork, 10));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo("ABCxyxyxyz"u8.ToArray()));
  }

  [Test, Category("Boundary")]
  [TestCase("1 0 00000000 0", Description = "distance 0")]
  [TestCase("1 0 00000001 0", Description = "distance before the block start")]
  public void MethodAd_MatchOutsideBlock_Throws(string bits) {
    var body = PackBits(bits);
    var fork = AdBlockHeader(body.Length, 2, 0x08).Concat(body).ToArray();
    using var r = Open(BuildDdFile(6, fork, 2));
    Assert.Throws<InvalidDataException>(() => r.Extract(r.Entries[0]));
  }

  [Test, Category("Exception")]
  public void MethodAd_BadBlockHeaderXor_Throws() {
    var fork = AdBlockHeader(1, 1, 0x01).Concat(new byte[] { 0x41 }).ToArray();
    fork[11] ^= 0xFF;
    using var r = Open(BuildDdFile(6, fork, 1));
    Assert.Throws<InvalidDataException>(() => r.Extract(r.Entries[0]));
  }

  [Test, Category("Exception")]
  [TestCase((byte)2)]
  [TestCase((byte)3)]
  [TestCase((byte)4)]
  [TestCase((byte)5)]
  [TestCase((byte)7)]
  [TestCase((byte)8)]
  [TestCase((byte)10)]
  [TestCase((byte)11)]
  public void UndecodedMethods_AreRefusedNotReturnedRaw(byte method) {
    using var r = Open(BuildDdFile(method, [1, 2, 3, 4], 100));
    Assert.That(r.Entries[0].IsSupported, Is.False);
    Assert.Throws<NotSupportedException>(() => r.Extract(r.Entries[0]));
  }

  [Test, Category("Exception")]
  public void DeltaPreprocessing_IsRefused() {
    using var r = Open(BuildDdFile(0, [1, 2, 3], 3, dataDelta: 1));
    Assert.Throws<NotSupportedException>(() => r.Extract(r.Entries[0]));
  }

  [Test, Category("HappyPath")]
  public void Method_HighBitFlag_IsMasked() {
    using var r = Open(BuildDdFile(0x80, [7], 1));
    Assert.That(r.Entries[0].Method, Is.Zero);
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(new byte[] { 7 }));
  }

  // ── Descriptor ────────────────────────────────────────────────────────────────

  [Test, Category("Exception")]
  public void Extract_NullEntry_Throws() {
    using var r = Open(BuildDdFile(0, [1], 1));
    Assert.Throws<ArgumentNullException>(() => r.Extract(null!));
  }

  [Test, Category("Exception")]
  public void Dispose_ThenExtract_Throws() {
    var r = Open(BuildDdFile(0, [1], 1));
    var entry = r.Entries[0];
    r.Dispose();
    Assert.Throws<ObjectDisposedException>(() => r.Extract(entry));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Create_ThenList() {
    var d = new DiskDoublerFormatDescriptor();
    using var ms = new MemoryStream();
    ((Compression.Registry.IArchiveCreatable)d).Create(
      ms,
      [Compression.Registry.ArchiveInputInfo.InMemory("payload.bin", "dd descriptor"u8.ToArray())],
      new Compression.Registry.FormatCreateOptions());
    ms.Position = 0;
    var entries = d.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].OriginalSize, Is.EqualTo(13));
    Assert.That(entries[0].Method, Is.EqualTo("None"));
  }
}
