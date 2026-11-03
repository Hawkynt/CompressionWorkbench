using System.Text;
using FileFormat.Tar;

namespace Compression.Tests.Tar;

/// <summary>
/// GNU sparse members in all four encodings GNU tar writes: the old-style 'S'
/// header (with and without extension blocks) and PAX sparse 0.0, 0.1 and 1.0.
/// The archives are assembled byte by byte from the layouts in the GNU tar manual
/// ("Storing Sparse Files", appendix "Sparse Formats").
/// </summary>
[TestFixture]
public class TarSparseTests {

  // Logical file: 3000 bytes; data "AAAA…" at 0..99, "BBBB…" at 1024..1223, hole to 3000.
  private const long RealSize = 3000;
  private static readonly (long Offset, byte[] Data)[] Segments = [
    (0, Enumerable.Repeat((byte)'A', 100).ToArray()),
    (1024, Enumerable.Repeat((byte)'B', 200).ToArray()),
  ];

  private static byte[] Expected() {
    var file = new byte[RealSize];
    foreach (var (offset, data) in Segments)
      data.CopyTo(file, offset);
    return file;
  }

  private static byte[] StoredData() => [.. Segments.SelectMany(s => s.Data)];

  // ── raw archive builders ────────────────────────────────────────────

  private static void Octal(byte[] block, int offset, int length, long value) {
    var text = Convert.ToString(value, 8).PadLeft(length - 1, '0');
    Encoding.ASCII.GetBytes(text, block.AsSpan(offset, length - 1));
    block[offset + length - 1] = 0;
  }

  private static byte[] Header(string name, long size, char type, bool gnu, Action<byte[]>? patch = null) {
    var h = new byte[512];
    Encoding.ASCII.GetBytes(name, h.AsSpan(0, name.Length));
    Octal(h, 100, 8, 420);
    Octal(h, 108, 8, 0);
    Octal(h, 116, 8, 0);
    Octal(h, 124, 12, size);
    Octal(h, 136, 12, 0);
    h[156] = (byte)type;
    if (gnu) "ustar  \0"u8.CopyTo(h.AsSpan(257));
    else { "ustar\0"u8.CopyTo(h.AsSpan(257)); "00"u8.CopyTo(h.AsSpan(263)); }
    patch?.Invoke(h);
    "        "u8.CopyTo(h.AsSpan(148));
    var sum = h.Sum(b => b);
    Octal(h, 148, 7, sum);
    h[155] = (byte)' ';
    return h;
  }

  private static void Pad(MemoryStream ms) {
    var pad = (512 - ms.Length % 512) % 512;
    ms.Write(new byte[pad]);
  }

  private static byte[] PaxRecords(params (string Key, string Value)[] records) {
    var sb = new StringBuilder();
    foreach (var (k, v) in records) {
      var body = $" {k}={v}\n";
      var len = body.Length + 1;
      while ((len.ToString().Length + body.Length) != len) len = len.ToString().Length + body.Length;
      sb.Append(len).Append(body);
    }
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static byte[] PaxArchive(string headerName, (string, string)[] records, byte[] data) {
    using var ms = new MemoryStream();
    var pax = PaxRecords(records);
    ms.Write(Header("PaxHeaders/x", pax.Length, 'x', gnu: false));
    ms.Write(pax);
    Pad(ms);
    ms.Write(Header(headerName, data.Length, '0', gnu: false));
    ms.Write(data);
    Pad(ms);
    ms.Write(new byte[1024]);
    return ms.ToArray();
  }

  private static byte[] GnuOldArchive((long Offset, long Length)[] map, byte[] data, long realSize) {
    using var ms = new MemoryStream();
    ms.Write(Header("sparse.bin", data.Length, 'S', gnu: true, h => {
      for (var i = 0; i < Math.Min(4, map.Length); ++i) {
        Octal(h, 386 + i * 24, 12, map[i].Offset);
        Octal(h, 386 + i * 24 + 12, 12, map[i].Length);
      }
      h[482] = (byte)(map.Length > 4 ? 1 : 0);
      Octal(h, 483, 12, realSize);
    }));
    for (var start = 4; start < map.Length; start += 21) {
      var ext = new byte[512];
      for (var i = 0; i < 21 && start + i < map.Length; ++i) {
        Octal(ext, i * 24, 12, map[start + i].Offset);
        Octal(ext, i * 24 + 12, 12, map[start + i].Length);
      }
      ext[504] = (byte)(start + 21 < map.Length ? 1 : 0);
      ms.Write(ext);
    }
    ms.Write(data);
    Pad(ms);
    ms.Write(new byte[1024]);
    return ms.ToArray();
  }

  private static (TarEntry Entry, byte[] Data) ReadSingle(byte[] archive) {
    using var reader = new TarReader(new MemoryStream(archive));
    var entry = reader.GetNextEntry() ?? throw new AssertionException("no entry");
    using var s = reader.GetEntryStream();
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    Assert.That(reader.GetNextEntry(), Is.Null, "exactly one member, and the reader must land on the terminator");
    return (entry, ms.ToArray());
  }

  private static (long, long)[] Map() => [.. Segments.Select(s => (s.Offset, (long)s.Data.Length))];

  // ── tests ───────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void GnuOldSparse_HolesAreReconstructed() {
    var (entry, data) = ReadSingle(GnuOldArchive(Map(), StoredData(), RealSize));
    Assert.Multiple(() => {
      Assert.That(entry.Name, Is.EqualTo("sparse.bin"));
      Assert.That(entry.IsFile, Is.True);
      Assert.That(entry.IsSparse, Is.True);
      Assert.That(entry.Size, Is.EqualTo(RealSize));
      Assert.That(entry.StoredSize, Is.EqualTo(300));
      Assert.That(data, Is.EqualTo(Expected()));
    });
  }

  [Test, Category("Boundary")]
  public void GnuOldSparse_MapSpillingIntoTwoExtensionBlocks_IsFollowed() {
    // 4 in the header + 21 in the first extension block + 5 in the second = 30 segments.
    var map = Enumerable.Range(0, 30).Select(i => ((long)i * 1000, 10L)).ToArray();
    var stored = new byte[300];
    for (var i = 0; i < stored.Length; ++i) stored[i] = (byte)(i / 10 + 1);
    var (entry, data) = ReadSingle(GnuOldArchive(map, stored, 30_000));
    var expected = new byte[30_000];
    for (var i = 0; i < 30; ++i) stored.AsSpan(i * 10, 10).CopyTo(expected.AsSpan(i * 1000));
    Assert.Multiple(() => {
      Assert.That(entry.SparseMap, Has.Count.EqualTo(30));
      Assert.That(data, Is.EqualTo(expected));
    });
  }

  [Test, Category("Boundary")]
  public void GnuOldSparse_EmptyFileOfOnlyHole() {
    var (entry, data) = ReadSingle(GnuOldArchive([(5000, 0)], [], 5000));
    Assert.Multiple(() => {
      Assert.That(entry.Size, Is.EqualTo(5000));
      Assert.That(data, Is.EqualTo(new byte[5000]));
    });
  }

  [Test, Category("HappyPath")]
  public void PaxSparse00_RepeatedOffsetNumbytesRecords() {
    var archive = PaxArchive("sparse.bin", [
      ("GNU.sparse.size", RealSize.ToString()),
      ("GNU.sparse.numblocks", "2"),
      ("GNU.sparse.offset", "0"), ("GNU.sparse.numbytes", "100"),
      ("GNU.sparse.offset", "1024"), ("GNU.sparse.numbytes", "200"),
    ], StoredData());
    var (entry, data) = ReadSingle(archive);
    Assert.Multiple(() => {
      Assert.That(entry.Name, Is.EqualTo("sparse.bin"));
      Assert.That(data, Is.EqualTo(Expected()));
    });
  }

  [Test, Category("HappyPath")]
  public void PaxSparse01_CommaSeparatedMap_UsesRealName() {
    var archive = PaxArchive("./GNUSparseFile.1/sparse.bin", [
      ("GNU.sparse.size", RealSize.ToString()),
      ("GNU.sparse.numblocks", "2"),
      ("GNU.sparse.name", "sparse.bin"),
      ("GNU.sparse.map", "0,100,1024,200"),
    ], StoredData());
    var (entry, data) = ReadSingle(archive);
    Assert.Multiple(() => {
      Assert.That(entry.Name, Is.EqualTo("sparse.bin"));
      Assert.That(data, Is.EqualTo(Expected()));
    });
  }

  [Test, Category("HappyPath")]
  public void PaxSparse10_MapInDataBlock() {
    var map = Encoding.ASCII.GetBytes("2\n0\n100\n1024\n200\n");
    var block = new byte[512];
    map.CopyTo(block, 0);
    var archive = PaxArchive("./GNUSparseFile.1/sparse.bin", [
      ("GNU.sparse.major", "1"), ("GNU.sparse.minor", "0"),
      ("GNU.sparse.name", "sparse.bin"),
      ("GNU.sparse.realsize", RealSize.ToString()),
    ], [.. block, .. StoredData()]);
    var (entry, data) = ReadSingle(archive);
    Assert.Multiple(() => {
      Assert.That(entry.Name, Is.EqualTo("sparse.bin"));
      Assert.That(entry.StoredSize, Is.EqualTo(300));
      Assert.That(data, Is.EqualTo(Expected()));
    });
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Extract_WritesTheReconstructedFile() {
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_tarsparse_{Guid.NewGuid():N}");
    try {
      new TarFormatDescriptor().Extract(new MemoryStream(GnuOldArchive(Map(), StoredData(), RealSize)), dir, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(dir, "sparse.bin")), Is.EqualTo(Expected()));
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_ReportsTheLogicalSize() {
    var entries = new TarFormatDescriptor().List(new MemoryStream(GnuOldArchive(Map(), StoredData(), RealSize)), null);
    Assert.That(entries.Single().OriginalSize, Is.EqualTo(RealSize));
  }

  [Test, Category("Exception")]
  public void Sparse_MapDescribingMoreThanStored_IsRejected() {
    var archive = GnuOldArchive([(0, 100), (1024, 999)], StoredData(), RealSize);
    using var reader = new TarReader(new MemoryStream(archive));
    Assert.Throws<InvalidDataException>(() => reader.GetNextEntry());
  }

  [Test, Category("Exception")]
  public void Sparse_OverlappingSegments_AreRejected() {
    var archive = GnuOldArchive([(0, 100), (50, 200)], StoredData(), RealSize);
    using var reader = new TarReader(new MemoryStream(archive));
    Assert.Throws<InvalidDataException>(() => reader.GetNextEntry());
  }

  [Test, Category("Exception")]
  public void Sparse_MapPastRealSize_IsRejected() {
    var archive = GnuOldArchive(Map(), StoredData(), 1100);
    using var reader = new TarReader(new MemoryStream(archive));
    Assert.Throws<InvalidDataException>(() => reader.GetNextEntry());
  }

  [Test, Category("Exception")]
  public void PaxSparse01_OddMapLength_IsRejected() {
    var archive = PaxArchive("x", [("GNU.sparse.size", "3000"), ("GNU.sparse.map", "0,100,1024")], StoredData());
    using var reader = new TarReader(new MemoryStream(archive));
    Assert.Throws<InvalidDataException>(() => reader.GetNextEntry());
  }

  [Test, Category("Exception")]
  public void PaxSparse10_GarbageMap_IsRejected() {
    var block = new byte[512];
    "2\nzz\n"u8.CopyTo(block);
    var archive = PaxArchive("x", [("GNU.sparse.major", "1"), ("GNU.sparse.realsize", "3000")], [.. block, .. StoredData()]);
    using var reader = new TarReader(new MemoryStream(archive));
    Assert.Throws<InvalidDataException>(() => reader.GetNextEntry());
  }

  [Test, Category("Boundary")]
  public void Base256SizeField_IsDecoded() {
    using var ms = new MemoryStream();
    ms.Write(Header("big.bin", 0, '0', gnu: true, h => {
      Array.Clear(h, 124, 12);
      h[124] = 0x80;
      h[135] = 5; // size 5, binary
    }));
    ms.Write("hello"u8);
    Pad(ms);
    ms.Write(new byte[1024]);
    var (entry, data) = ReadSingle(ms.ToArray());
    Assert.Multiple(() => {
      Assert.That(entry.Size, Is.EqualTo(5));
      Assert.That(data, Is.EqualTo("hello"u8.ToArray()));
    });
  }
}
