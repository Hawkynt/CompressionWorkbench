using System.Reflection;
using System.Text;
using Compression.Core.Checksums;
using Compression.Registry;
using FileFormat.FreeArc;

namespace Compression.Tests.FreeArc;

/// <summary>
/// FreeArc against archives FreeArc 0.67 wrote (see <c>ReferenceVectors/README.md</c>) and against
/// our own writer. The live oracle runs are in <see cref="FreeArcOracleTests"/>.
/// </summary>
[TestFixture]
public class FreeArcTests {
  // The fixture inputs were touched to 2024-01-02 03:04:05 UTC, but FreeArc 0.67 (run on Windows
  // while daylight saving was in effect) recorded them one hour later; the archives hold this value.
  internal static readonly DateTime FixtureTime = new(2024, 1, 2, 4, 4, 5, DateTimeKind.Utc);

  internal static byte[] Hello => "Hello, FreeArc!\n"u8.ToArray();

  internal static byte[] Lines() {
    var sb = new StringBuilder();
    for (var i = 1; i <= 40; ++i)
      sb.Append("line ").Append(i).Append(" the quick brown fox jumps over the lazy dog\n");
    return Encoding.ASCII.GetBytes(sb.ToString());
  }

  internal static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"FreeArcVectors.{name}.arc")
      ?? throw new InvalidOperationException($"Missing FreeArc reference vector {name}.arc");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  private static FreeArcReader Open(byte[] archive) => new(new MemoryStream(archive));

  private static FreeArcEntry Entry(FreeArcReader r, string name) => r.Entries.Single(e => e.Name == name);

  // ── Real archives: supported methods ───────────────────────────────────────

  [TestCase("storing", "storing")]
  [TestCase("lzma", "lzma:32kb:normal:32")]
  [TestCase("lzma-lc4", "lzma:32kb:normal:32:pb1:lc4:lp1")]
  [Category("RoundTrip")]
  public void RealArchive_ThreeFiles_ExtractByteIdentical(string vector, string method) {
    using var r = Open(Vector(vector));

    Assert.That(r.Entries.Select(e => e.Name), Is.EquivalentTo(new[] { "hello.txt", "empty.bin", "sub/lines.txt" }));
    Assert.Multiple(() => {
      Assert.That(r.Extract(Entry(r, "hello.txt")), Is.EqualTo(Hello));
      Assert.That(r.Extract(Entry(r, "sub/lines.txt")), Is.EqualTo(Lines()));
      Assert.That(r.Extract(Entry(r, "empty.bin")), Is.Empty);
      Assert.That(r.Entries.Select(e => e.Method).Distinct(), Is.EqualTo(new[] { method }));
      Assert.That(r.Entries.All(e => !e.IsDirectory));
      Assert.That(r.Entries.All(e => e.LastModified == FixtureTime));
      Assert.That(Entry(r, "hello.txt").Crc, Is.EqualTo(0x2B27C831u));
      Assert.That(r.Entries.Sum(e => e.CompressedSize), Is.LessThanOrEqualTo(Vector(vector).Length));
      Assert.That(r.SfxSize, Is.Zero);
    });
  }

  [Test, Category("RoundTrip")]
  public void RealArchive_DirectoriesUnicodeAndSeveralSolidBlocks() {
    using var r = Open(Vector("dirs"));

    Assert.Multiple(() => {
      Assert.That(Entry(r, "emptydir").IsDirectory, Is.True);
      Assert.That(Entry(r, "dир").IsDirectory, Is.True);
      Assert.That(Entry(r, "dир/файл.txt").IsDirectory, Is.False);
      Assert.That(r.Extract(Entry(r, "dир/файл.txt")), Is.EqualTo("abc"u8.ToArray()));
      Assert.That(r.Extract(Entry(r, "hello.txt")), Is.EqualTo(Hello));
      Assert.That(r.Extract(Entry(r, "emptydir")), Is.Empty);
    });
  }

  [Test]
  public void RealArchive_Comment() {
    using var r = Open(Vector("comment"));
    Assert.That(r.Comment, Is.EqualTo("A FreeArc comment"));
    Assert.That(r.Extract(Entry(r, "sub/lines.txt")), Is.EqualTo(Lines()));
  }

  // ── Real archives: methods we refuse ───────────────────────────────────────

  [TestCase("ppmd", "ppmd:10:48mb")]
  [TestCase("tornado", "rep:3kb:256+4x4:tor:3:3kb:h16kb")]
  public void RealArchive_UnsupportedMethod_ListsButRefusesExtraction(string vector, string method) {
    using var r = Open(Vector(vector));

    Assert.That(r.Entries.Select(e => e.Name), Is.EquivalentTo(new[] { "hello.txt", "sub/lines.txt" }));
    Assert.That(r.Entries[0].Method, Is.EqualTo(method));
    Assert.That(() => r.Extract(Entry(r, "hello.txt")), Throws.TypeOf<NotSupportedException>());
  }

  [Test]
  public void RealArchive_EncryptedData_ListsButRefusesExtraction() {
    using var r = Open(Vector("encrypted"));
    var entry = Entry(r, "hello.txt");
    Assert.That(entry.Method, Does.Contain("+aes-256/ctr:"));
    Assert.That(() => r.Extract(entry), Throws.TypeOf<NotSupportedException>().With.Message.Contains("encrypted"));
  }

  [Test]
  public void RealArchive_EncryptedHeaders_ReaderRefuses_DescriptorFallsBack() {
    var archive = Vector("hdrenc");
    Assert.That(() => Open(archive), Throws.TypeOf<NotSupportedException>());

    var listed = new FreeArcFormatDescriptor().List(new MemoryStream(archive), null);
    Assert.That(listed.Select(e => e.Name), Is.EqualTo(new[] { "FULL.arc", "metadata.ini" }));
    Assert.That(listed[0].OriginalSize, Is.EqualTo(archive.Length));
  }

  [Test]
  public void RealArchive_SfxPrefix_IsSkipped() {
    var archive = Vector("lzma");
    var prefixed = new byte[1000 + archive.Length];
    archive.CopyTo(prefixed, 1000);
    using var r = Open(prefixed);
    Assert.That(r.SfxSize, Is.EqualTo(1000));
    Assert.That(r.Extract(Entry(r, "sub/lines.txt")), Is.EqualTo(Lines()));
  }

  // ── Corruption and boundaries ──────────────────────────────────────────────

  [Test]
  public void Corrupt_FooterDescriptorCrc_Throws() {
    var archive = Vector("storing");
    archive[^1] ^= 0x55;
    Assert.That(() => Open(archive), Throws.TypeOf<InvalidDataException>());
  }

  [Test]
  public void Corrupt_Truncated_Throws() {
    var archive = Vector("lzma");
    Assert.That(() => Open(archive[..^3]), Throws.TypeOf<InvalidDataException>());
  }

  [Test]
  public void Corrupt_DirectoryBlockByte_FailsCrc() {
    var archive = Vector("storing");
    // Data block is 2087 bytes at 31; the stored directory follows it.
    archive[31 + 2087 + 5] ^= 0x01;
    Assert.That(() => Open(archive), Throws.TypeOf<InvalidDataException>());
  }

  [Test]
  public void Corrupt_FileDataByte_FailsCrcOnExtract() {
    var archive = Vector("storing");
    archive[31 + 2087 - 1] ^= 0x01; // last byte of sub/lines.txt
    using var r = Open(archive);
    Assert.That(r.Extract(Entry(r, "hello.txt")), Is.EqualTo(Hello));
    Assert.That(() => r.Extract(Entry(r, "sub/lines.txt")), Throws.TypeOf<InvalidDataException>().With.Message.Contains("CRC"));
  }

  [TestCase(0)]
  [TestCase(2)]
  [TestCase(15)]
  public void TooSmall_Throws(int length) {
    var data = new byte[length];
    "ArC\x01"u8[..Math.Min(4, length)].CopyTo(data);
    Assert.That(() => Open(data), Throws.TypeOf<InvalidDataException>());
  }

  [Test]
  public void SignatureOnly_NoFooter_Throws() {
    var data = new byte[64];
    "ArC\x01"u8.CopyTo(data);
    Assert.That(() => Open(data), Throws.TypeOf<InvalidDataException>());
  }

  [Test]
  public void LegacyArcBytes_Throw() {
    var data = new byte[64];
    data[0] = 0x1A;
    data[1] = 0x08;
    Assert.That(() => Open(data), Throws.TypeOf<InvalidDataException>());
  }

  [TestCase(0UL, 1)]
  [TestCase(127UL, 1)]
  [TestCase(128UL, 2)]
  [TestCase((1UL << 14) - 1, 2)]
  [TestCase(1UL << 14, 3)]
  [TestCase((1UL << 28) - 1, 4)]
  [TestCase(1UL << 28, 5)]
  [TestCase((1UL << 56) - 1, 8)]
  [TestCase(1UL << 56, 9)]
  [TestCase(ulong.MaxValue, 9)]
  public void VarInt_RoundTrip_AtLengthBoundaries(ulong value, int expectedLength) {
    using var ms = new MemoryStream();
    FreeArcEncoding.WriteVarInt(ms, value);
    var bytes = ms.ToArray();
    var p = 0;
    Assert.That(bytes, Has.Length.EqualTo(expectedLength));
    Assert.That(FreeArcEncoding.ReadVarInt(bytes, ref p), Is.EqualTo(value));
    Assert.That(p, Is.EqualTo(expectedLength));
  }

  [Test]
  public void VarInt_KnownEncodings_FromFreeArcOutput() {
    // 10508 was written by FreeArc as 31 A4, 85 as AA, 8 as 10.
    var p = 0;
    Assert.That(FreeArcEncoding.ReadVarInt([0x31, 0xA4], ref p), Is.EqualTo(10508));
    p = 0;
    Assert.That(FreeArcEncoding.ReadVarInt([0xAA], ref p), Is.EqualTo(85));
    p = 0;
    Assert.That(FreeArcEncoding.ReadVarInt([0x10], ref p), Is.EqualTo(8));
  }

  [Test]
  public void VarInt_Truncated_Throws() {
    var p = 0;
    Assert.That(() => FreeArcEncoding.ReadVarInt([0x01], ref p), Throws.TypeOf<InvalidDataException>());
  }

  [TestCase("storing", true)]
  [TestCase("lzma:1mb:normal:bt4:32", true)]
  [TestCase("ppmd:10:48mb", false)]
  [TestCase("delta+lzma:32kb", false)]
  [TestCase("4x4:tor:3", false)]
  [TestCase("lzma+aes-256/ctr:n1000", false)]
  public void Methods_IsSupported(string method, bool supported) =>
    Assert.That(FreeArcMethods.IsSupported(method) && !FreeArcMethods.IsEncrypted(method), Is.EqualTo(supported));

  // ── Writer ─────────────────────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void Writer_Storing_ReproducesFreeArcByteForByte() {
    // FreeArc 0.67 wrote storing.arc with -m0 -dmstoring; given the same files in the same order
    // and timestamps our writer must produce the identical archive.
    var w = new FreeArcWriter();
    w.AddFile("empty.bin", [], FixtureTime);
    w.AddFile("hello.txt", Hello, FixtureTime);
    w.AddFile("sub/lines.txt", Lines(), FixtureTime);
    Assert.That(w.Build(), Is.EqualTo(Vector("storing")));
  }

  [TestCase("storing")]
  [TestCase("lzma")]
  [TestCase("lzma:1mb")]
  [TestCase("lzma:64kb:lc4:lp1:pb1")]
  [Category("RoundTrip")]
  public void Writer_RoundTrip(string method) {
    var big = new byte[200_000];
    for (var i = 0; i < big.Length; ++i)
      big[i] = (byte)(i * 31 % 251 ^ (i >> 9));

    var w = new FreeArcWriter(method);
    w.AddDirectory("emptydir", FixtureTime);
    w.AddDirectory("dир", FixtureTime);
    w.AddFile("dир/файл.txt", "abc"u8.ToArray(), FixtureTime);
    w.AddFile("hello.txt", Hello, FixtureTime);
    w.AddFile("a/b/empty.bin", [], FixtureTime);
    w.AddFile("big.bin", big, FixtureTime);

    using var r = Open(w.Build());
    Assert.Multiple(() => {
      Assert.That(r.Entries, Has.Count.EqualTo(6));
      Assert.That(Entry(r, "emptydir").IsDirectory, Is.True);
      Assert.That(Entry(r, "dир").IsDirectory, Is.True);
      Assert.That(r.Extract(Entry(r, "dир/файл.txt")), Is.EqualTo("abc"u8.ToArray()));
      Assert.That(r.Extract(Entry(r, "hello.txt")), Is.EqualTo(Hello));
      Assert.That(r.Extract(Entry(r, "a/b/empty.bin")), Is.Empty);
      Assert.That(r.Extract(Entry(r, "big.bin")), Is.EqualTo(big));
      Assert.That(Entry(r, "big.bin").Crc, Is.EqualTo(Crc32.Compute(big)));
      Assert.That(r.Entries.All(e => e.LastModified == FixtureTime));
    });
  }

  [Test]
  public void Writer_NoEntries_IsReadableAndEmpty() {
    using var r = Open(new FreeArcWriter().Build());
    Assert.That(r.Entries, Is.Empty);
  }

  [TestCase("ppmd")]
  [TestCase("tor:3")]
  [TestCase("delta+lzma")]
  public void Writer_UnsupportedMethod_Throws(string method) =>
    Assert.That(() => new FreeArcWriter(method), Throws.TypeOf<NotSupportedException>());

  // ── Descriptor ─────────────────────────────────────────────────────────────

  [Test]
  public void Descriptor_Capabilities_AndIdentity() {
    var d = new FreeArcFormatDescriptor();
    Assert.Multiple(() => {
      Assert.That(d.Id, Is.EqualTo("FreeArc"));
      Assert.That(d.DefaultExtension, Is.EqualTo(".arc"));
      Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo("ArC\x01"u8.ToArray()));
      Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
      Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
      Assert.That(d.Capabilities.HasFlag(FormatCapabilities.SupportsDirectories), Is.True);
    });
  }

  [Test]
  public void Descriptor_ListAndExtract_RealArchive() {
    var d = new FreeArcFormatDescriptor();
    var listed = d.List(new MemoryStream(Vector("dirs")), null);
    Assert.That(listed.Single(e => e.Name == "emptydir").IsDirectory, Is.True);

    var dir = Path.Combine(Path.GetTempPath(), $"cwb_freearc_{Guid.NewGuid():N}");
    try {
      d.Extract(new MemoryStream(Vector("dirs")), dir, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(dir, "dир", "файл.txt")), Is.EqualTo("abc"u8.ToArray()));
      Assert.That(Directory.Exists(Path.Combine(dir, "emptydir")), Is.True);
    } finally {
      try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
  }

  [Test]
  public void Descriptor_Create_WritesRealLayout() {
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, Hello);
      using var ms = new MemoryStream();
      ((IArchiveCreatable)new FreeArcFormatDescriptor()).Create(ms,
        [new ArchiveInputInfo(tmp, "x/hello.txt", false), new ArchiveInputInfo("", "x", true)],
        new FormatCreateOptions());
      ms.Position = 0;
      using var r = new FreeArcReader(ms);
      Assert.That(r.Extract(Entry(r, "x/hello.txt")), Is.EqualTo(Hello));
      Assert.That(Entry(r, "x").IsDirectory, Is.True);
      Assert.That(ms.ToArray()[..4], Is.EqualTo("ArC\x01"u8.ToArray()));
    } finally {
      File.Delete(tmp);
    }
  }
}
