using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.Zfs;

namespace Compression.Tests.Zfs;

[TestFixture]
public class ZfsTests {

  [Test, Category("HappyPath")]
  public void Descriptor_IsReadOnly() {
    var desc = new ZfsFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("Zfs"));
    Assert.That(desc.DefaultExtension, Is.EqualTo(".zfs"));
    Assert.That(desc.Extensions, Does.Contain(".zpool"));
    Assert.That(desc.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(desc.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False);
    Assert.That(desc.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
    Assert.That(desc, Is.Not.InstanceOf<IArchiveCreatable>());
    Assert.That(desc, Is.Not.InstanceOf<IArchiveModifiable>());
  }

  [Test, Category("HappyPath"), Category("Oracle")]
  public void Descriptor_ListAndExtract_RealPool() {
    var desc = new ZfsFormatDescriptor();
    using var image = ZfsReferencePools.Open("cwbv28");
    var list = desc.List(image, null);
    Assert.That(list.Any(e => e.Name == "text.txt" && e.OriginalSize > 0), Is.True);
    Assert.That(list.Single(e => e.Name == "link").IsSymlink, Is.True);
    Assert.That(list.Any(e => e.Name == "metadata.ini"), Is.True);

    var dir = Path.Combine(Path.GetTempPath(), "cwb_zfs_" + Guid.NewGuid().ToString("N"));
    try {
      image.Position = 0;
      desc.Extract(image, dir, null, ["text.txt", "metadata.ini"]);
      var expected = ZfsReferencePools.Manifest("cwbv28").Single(m => m.Path == "text.txt").Sha256;
      Assert.That(ZfsReferencePools.Sha256Of(File.ReadAllBytes(Path.Combine(dir, "text.txt"))), Is.EqualTo(expected));
      var metadata = File.ReadAllText(Path.Combine(dir, "metadata.ini"));
      Assert.That(metadata, Does.Contain("parse_status=ok"));
      Assert.That(metadata, Does.Contain("pool=cwbv28"));
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }

  [TestCase(0)]
  [TestCase(100)]
  [TestCase(512 * 1024)]
  [TestCase(1024 * 1024)]
  [Category("ErrorHandling")]
  public void Descriptor_List_NeverThrowsOnGarbage(int length) {
    var bytes = new byte[length];
    new Random(length).NextBytes(bytes);
    var list = new ZfsFormatDescriptor().List(new MemoryStream(bytes), null);
    Assert.That(list.Select(e => e.Name), Is.EquivalentTo(new[] { "FULL.zfs", "metadata.ini" }));
  }

  [Test, Category("ErrorHandling"), Category("Oracle")]
  public void Descriptor_List_TruncatedPoolFallsBack() {
    var truncated = ZfsReferencePools.ImageBytes("cwbmain").AsSpan(0, 600 * 1024).ToArray();
    var list = new ZfsFormatDescriptor().List(new MemoryStream(truncated), null);
    Assert.That(list.Select(e => e.Name), Does.Contain("FULL.zfs"));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[100]);
    Assert.Throws<InvalidDataException>(() => _ = new ZfsReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_NoLabel_Throws() {
    using var ms = new MemoryStream(new byte[2 * 1024 * 1024]);
    Assert.Throws<InvalidDataException>(() => _ = new ZfsReader(ms));
  }

  // ---------- checksums ----------

  [Test, Category("Spec")]
  public void Fletcher4_KnownVector() {
    // Two words 1 and 2: a = 3, b = 1 + 3 = 4, c = 1 + 4 = 5, d = 1 + 5 = 6.
    Span<byte> sum = stackalloc byte[32];
    ZfsChecksum.Fletcher4([1, 0, 0, 0, 2, 0, 0, 0], sum);
    Assert.That(Words(sum), Is.EqualTo(new ulong[] { 3, 4, 5, 6 }));
  }

  [Test, Category("Spec")]
  public void Fletcher2_KnownVector() {
    // Pairs (1, 2), (3, 4): a0 = 4, a1 = 6, b0 = 1 + 4 = 5, b1 = 2 + 6 = 8.
    var data = new byte[32];
    for (var i = 0; i < 4; ++i) BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(i * 8), (ulong)(i + 1));
    Span<byte> sum = stackalloc byte[32];
    ZfsChecksum.Fletcher2(data, sum);
    Assert.That(Words(sum), Is.EqualTo(new ulong[] { 4, 6, 5, 8 }));
  }

  [Test, Category("Spec")]
  public void Sha256_IsStoredAsBigEndianWords() {
    // SHA-256("abc") = ba7816bf 8f01cfea ...
    Span<byte> sum = stackalloc byte[32];
    ZfsChecksum.Sha256Words("abc"u8, sum);
    Assert.That(Words(sum)[0], Is.EqualTo(0xBA7816BF8F01CFEAUL));
  }

  [Test, Category("Spec")]
  public void Sha512_IsSha512Over256StoredAsDigestBytes() {
    var expected = Convert.FromHexString("53048e2681941ef99b2e29b76b4c7dabe4c2d0c634fc6d46e0e2f13107e7af23");
    Assert.That(ZfsChecksum.Verify(ZfsConstants.ChecksumSha512, "abc"u8, expected), Is.EqualTo(ZfsChecksumResult.Match));
  }

  [Test, Category("Spec")]
  public void Verify_SaltedAlgorithmIsReportedNotVerified()
    => Assert.That(ZfsChecksum.Verify(ZfsConstants.ChecksumBlake3, new byte[8], new byte[32]),
      Is.EqualTo(ZfsChecksumResult.NotVerified));

  [Test, Category("ErrorHandling")]
  public void Verify_Mismatch()
    => Assert.That(ZfsChecksum.Verify(ZfsConstants.ChecksumFletcher4, new byte[8] { 1, 0, 0, 0, 0, 0, 0, 0 }, new byte[32]),
      Is.EqualTo(ZfsChecksumResult.Mismatch));

  private static ulong[] Words(ReadOnlySpan<byte> sum) {
    var words = new ulong[4];
    for (var i = 0; i < 4; ++i) words[i] = BinaryPrimitives.ReadUInt64LittleEndian(sum[(i * 8)..]);
    return words;
  }

  // ---------- block pointers ----------

  [Test, Category("Spec")]
  public void EmbeddedPointer_PayloadSkipsPropertiesAndBirthWords() {
    var raw = new byte[128];
    for (var i = 0; i < 128; ++i) raw[i] = (byte)i;
    // embedded, uncompressed (2), physical size 112, logical size 112
    var prop = (1UL << 39) | ((ulong)ZfsConstants.CompressOff << 32) | (111UL << 25) | 111UL | (1UL << 63);
    BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(0x30), prop);
    var bp = ZfsBlockPointer.Read(raw);
    Assert.That(bp.IsEmbedded, Is.True);
    Assert.That(bp.PhysicalSize, Is.EqualTo(112));
    var payload = bp.EmbeddedPayload();
    var expected = Enumerable.Range(0, 48).Concat(Enumerable.Range(0x38, 24)).Concat(Enumerable.Range(0x58, 40))
      .Select(i => (byte)i).ToArray();
    Assert.That(payload, Is.EqualTo(expected));
  }

  [Test, Category("Spec")]
  public void BlockPointer_DecodesDvaAndSizes() {
    var raw = new byte[128];
    BinaryPrimitives.WriteUInt64LittleEndian(raw, (3UL << 32) | 8);          // vdev 3, asize 8 sectors
    BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(8), (1UL << 63) | 100); // gang, offset 100 sectors
    var prop = 255UL | (7UL << 16) | ((ulong)ZfsConstants.CompressLz4 << 32) | ((ulong)ZfsConstants.ChecksumFletcher4 << 40)
               | (19UL << 48) | (1UL << 56) | (1UL << 63);
    BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(0x30), prop);
    var bp = ZfsBlockPointer.Read(raw);
    Assert.Multiple(() => {
      Assert.That(bp.Dvas[0], Is.EqualTo(new ZfsDva(3, 100 * 512, 8, true, false)));
      Assert.That(bp.LogicalSize, Is.EqualTo(256 * 512));
      Assert.That(bp.PhysicalSize, Is.EqualTo(8 * 512));
      Assert.That(bp.Compression, Is.EqualTo(ZfsConstants.CompressLz4));
      Assert.That(bp.Level, Is.EqualTo(1));
      Assert.That(bp.IsHole, Is.False);
    });
  }

  [Test, Category("Boundary")]
  public void BlockPointer_AllZeroIsAHole() => Assert.That(ZfsBlockPointer.Read(new byte[128]).IsHole, Is.True);

  // ---------- RAID-Z mapping ----------

  [Test, Category("Spec")]
  public void RaidzMap_FullStripe() {
    // 3 children, 1 parity, ashift 9, 4 sectors at sector 0: q = 2, r = 0
    var map = ZfsRaidzVdev.Map(0, 2048, 3, 1, 9);
    Assert.That(map, Has.Length.EqualTo(3));
    Assert.That(map.Select(c => (c.Child, c.Offset, c.Size, c.IsData)), Is.EqualTo(new[] {
      (0, 0L, 1024, false), (1, 0L, 1024, true), (2, 0L, 1024, true),
    }));
  }

  [Test, Category("Spec")]
  public void RaidzMap_ShortBlockWrapsAndShortensColumns() {
    // 3 children, 1 parity, 3 sectors at sector 4 (f = 1): q = 1, r = 1, bc = 2
    var map = ZfsRaidzVdev.Map(4 * 512, 3 * 512, 3, 1, 9);
    Assert.That(map.Select(c => (c.Child, c.Offset, c.Size)), Is.EqualTo(new[] {
      (1, 512L, 1024), (2, 512L, 1024), (0, 1024L, 512),
    }));
  }

  [Test, Category("Spec")]
  public void RaidzMap_SingleSectorUsesParityPlusOneColumn() {
    var map = ZfsRaidzVdev.Map(0, 512, 4, 2, 9);
    Assert.That(map, Has.Length.EqualTo(3));
    Assert.That(map.Count(c => c.IsData), Is.EqualTo(1));
  }

  [Test, Category("Spec")]
  public void RaidzMap_Raidz1SwapsParityAtOneMebibyteBit() {
    var offset = 1L << 20;
    var map = ZfsRaidzVdev.Map(offset, 2048, 3, 1, 9);
    var plain = ZfsRaidzVdev.Map(offset + (3L << 21), 2048, 3, 1, 9);
    Assert.That(map[0].Child, Is.EqualTo((int)((offset >> 9) % 3 + 1) % 3));
    Assert.That(map[0].Size, Is.EqualTo(plain[0].Size));
  }

  // ---------- nvlist ----------

  [Test, Category("Spec")]
  public void NvList_DecodesNestedArraysAndSkipsUnknownTypes() {
    var nv = new NvBuilder()
      .U64("version", 5000).Str("name", "tank").Bool("flag").Unknown("other")
      .List("vdev_tree", new NvBuilder().Str("type", "raidz").U64("nparity", 1)
        .Lists("children", new NvBuilder().U64("guid", 1), new NvBuilder().U64("guid", 2)))
      .Build();
    var list = XdrNvList.Decode(nv);
    Assert.That(list.UInt64("version"), Is.EqualTo(5000UL));
    Assert.That(list.String("name"), Is.EqualTo("tank"));
    Assert.That(list.Pairs.ContainsKey("flag"), Is.True);
    Assert.That(list.Pairs.ContainsKey("other"), Is.False);
    var tree = list.List("vdev_tree")!;
    Assert.That(tree.String("type"), Is.EqualTo("raidz"));
    Assert.That(tree.Lists("children").Select(c => c.UInt64("guid")), Is.EqualTo(new ulong?[] { 1, 2 }));
  }

  [Test, Category("ErrorHandling")]
  public void NvList_NotXdr_Throws()
    => Assert.Throws<InvalidDataException>(() => XdrNvList.Decode(new byte[16]));

  /// <summary>Builds XDR nvlists the way libnvpair encodes them, for the decoder tests.</summary>
  private sealed class NvBuilder {
    private readonly MemoryStream _pairs = new();

    public NvBuilder U64(string name, ulong value) => this.Pair(name, 8, 1, w => w.U64(value));
    public NvBuilder Str(string name, string value) => this.Pair(name, 9, 1, w => w.Str(value));
    public NvBuilder Bool(string name) => this.Pair(name, 1, 0, _ => { });
    public NvBuilder Unknown(string name) => this.Pair(name, 6, 1, w => w.U32(7));
    public NvBuilder List(string name, NvBuilder inner) => this.Pair(name, 19, 1, w => w.Raw(inner.Body()));
    public NvBuilder Lists(string name, params NvBuilder[] inner)
      => this.Pair(name, 20, inner.Length, w => { foreach (var i in inner) w.Raw(i.Body()); });

    public byte[] Build() => [1, 1, 0, 0, .. this.Body()];

    private byte[] Body() {
      var w = new Writer();
      w.U32(0);
      w.U32(1);
      w.Raw(this._pairs.ToArray());
      w.U32(0);
      w.U32(0);
      return w.ToArray();
    }

    private NvBuilder Pair(string name, int type, int count, Action<Writer> value) {
      var body = new Writer();
      body.Str(name);
      body.U32((uint)type);
      body.U32((uint)count);
      value(body);
      var bytes = body.ToArray();
      var w = new Writer();
      w.U32((uint)(bytes.Length + 8));
      w.U32((uint)(bytes.Length + 8));
      w.Raw(bytes);
      this._pairs.Write(w.ToArray());
      return this;
    }

    private sealed class Writer {
      private readonly MemoryStream _ms = new();
      public void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); this._ms.Write(b); }
      public void U64(ulong v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, v); this._ms.Write(b); }
      public void Str(string s) {
        var bytes = Encoding.UTF8.GetBytes(s);
        this.U32((uint)bytes.Length);
        this._ms.Write(bytes);
        this._ms.Write(new byte[(4 - bytes.Length % 4) % 4]);
      }
      public void Raw(byte[] bytes) => this._ms.Write(bytes);
      public byte[] ToArray() => this._ms.ToArray();
    }
  }
}
