using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;
using FileFormat.PackIt;

namespace Compression.Tests.PackIt;

[TestFixture]
public class PackItTests {

  // ── Helpers ───────────────────────────────────────────────────────────────────

  private static byte[] Write(params (string Name, byte[] Data)[] files) {
    using var ms = new MemoryStream();
    using (var w = new PackItWriter(ms, leaveOpen: true))
      foreach (var (name, data) in files)
        w.AddFile(name, data);
    return ms.ToArray();
  }

  private static PackItReader Open(byte[] archive) => new(new MemoryStream(archive));

  private static readonly DateTime SampleTime = new(1990, 6, 15, 12, 30, 0, DateTimeKind.Utc);

  // ── Layout ────────────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Writer_Layout_MagicHeaderForksCrcAndEndMarker() {
    var data = "Hello, PackIt!"u8.ToArray();
    var archive = Write(("hello.txt", data));

    Assert.That(archive.AsSpan(0, 4).SequenceEqual("PMag"u8), Is.True);
    var header = archive.AsSpan(4, 94);
    Assert.That(header[0], Is.EqualTo(9));
    Assert.That(Encoding.Latin1.GetString(header.Slice(1, 9)), Is.EqualTo("hello.txt"));
    Assert.That(Encoding.Latin1.GetString(header.Slice(64, 4)), Is.EqualTo("TEXT"));
    Assert.That(Encoding.Latin1.GetString(header.Slice(68, 4)), Is.EqualTo("CWIE"));
    Assert.That(BinaryPrimitives.ReadUInt32BigEndian(header[76..]), Is.EqualTo((uint)data.Length));
    Assert.That(BinaryPrimitives.ReadUInt32BigEndian(header[80..]), Is.Zero);
    Assert.That(BinaryPrimitives.ReadUInt16BigEndian(header[92..]), Is.EqualTo(Crc16Ccitt.Compute(header[..92])),
      "header CRC is CRC-16/XMODEM over the 92 bytes before it");
    Assert.That(archive.AsSpan(98, data.Length).SequenceEqual(data), Is.True);
    Assert.That(BinaryPrimitives.ReadUInt16BigEndian(archive.AsSpan(98 + data.Length)), Is.EqualTo(Crc16Ccitt.Compute(data)));
    Assert.That(archive.AsSpan(archive.Length - 4).SequenceEqual("PEnd"u8), Is.True);
    Assert.That(archive, Has.Length.EqualTo(4 + 94 + data.Length + 2 + 4));
  }

  [Test, Category("HappyPath")]
  public void Writer_NoFiles_IsJustTheEndMarker() {
    var archive = Write();
    Assert.That(archive, Is.EqualTo("PEnd"u8.ToArray()));
    using var r = Open(archive);
    Assert.That(r.Entries, Is.Empty);
    Assert.That(r.IsIncomplete, Is.False);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_Reader_RoundTrip_MetadataAndForks() {
    var data = new byte[3000];
    new Random(5).NextBytes(data);
    var rsrc = "resource fork bytes"u8.ToArray();
    using var ms = new MemoryStream();
    using (var w = new PackItWriter(ms, leaveOpen: true))
      w.AddFile("doc", data, "APPL", "ttxt", rsrc, SampleTime);

    ms.Position = 0;
    using var r = new PackItReader(ms);
    var e = r.Entries.Single();
    Assert.That((e.Name, e.FileType, e.Creator), Is.EqualTo(("doc", "APPL", "ttxt")));
    Assert.That((e.DataForkSize, e.ResourceForkSize), Is.EqualTo((3000L, (long)rsrc.Length)));
    Assert.That(e.Modified, Is.EqualTo(SampleTime));
    Assert.That(e.IsCompressed, Is.False);
    Assert.That(r.Extract(e), Is.EqualTo(data));
    Assert.That(r.ExtractResourceFork(e), Is.EqualTo(rsrc));
  }

  [Test, Category("RoundTrip")]
  public void Writer_Reader_MultipleFiles_InOrder() {
    var archive = Write(("a.txt", "Hello"u8.ToArray()), ("b.txt", []), ("c.bin", new byte[700]));
    using var r = Open(archive);
    Assert.That(r.Entries.Select(e => e.Name), Is.EqualTo(new[] { "a.txt", "b.txt", "c.bin" }));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo("Hello"u8.ToArray()));
    Assert.That(r.Extract(r.Entries[1]), Is.Empty);
    Assert.That(r.Extract(r.Entries[2]), Is.EqualTo(new byte[700]));
  }

  [Test, Category("Boundary")]
  [TestCase(63, 63)]
  [TestCase(64, 63)]
  [TestCase(1, 1)]
  public void Writer_Name_IsCutAtSixtyThreeBytes(int length, int kept) {
    var name = new string('n', length);
    using var r = Open(Write((name, [1])));
    Assert.That(r.Entries[0].Name, Is.EqualTo(name[..kept]));
  }

  // ── Huffman ("PMa4") entries ─────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Compressed_Entry_DecodesBothForksAndFindsNextEntry() {
    var data = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("the quick brown fox ", 40)));
    var rsrc = new byte[] { 0, 0, 1, 2, 3, 0, 0, 0 };
    using var ms = new MemoryStream();
    PackItTestEncoder.WriteCompressedEntry(ms, "fox.txt", data, rsrc);
    PackItTestEncoder.WriteStoredEntry(ms, "after.txt", "tail"u8.ToArray(), []);
    ms.Write("PEnd"u8);

    ms.Position = 0;
    using var r = new PackItReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(2));
    Assert.That(r.Entries[0].IsCompressed, Is.True);
    Assert.That(r.Entries[0].EntryLength, Is.LessThan(4 + 94 + data.Length + rsrc.Length + 2),
      "the test encoder must actually compress, or the boundary walk is not exercised");
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
    Assert.That(r.ExtractResourceFork(r.Entries[0]), Is.EqualTo(rsrc));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo("tail"u8.ToArray()));
    Assert.That(r.IsIncomplete, Is.False);
  }

  [Test, Category("Exception")]
  public void Compressed_ForkCrcMismatch_Throws() {
    using var ms = new MemoryStream();
    PackItTestEncoder.WriteCompressedEntry(ms, "bad.txt", "payload"u8.ToArray(), [], corruptForkCrc: true);
    ms.Write("PEnd"u8);
    ms.Position = 0;
    using var r = new PackItReader(ms);
    Assert.Throws<InvalidDataException>(() => r.Extract(r.Entries[0]));
  }

  // ── Integrity ─────────────────────────────────────────────────────────────────

  [Test, Category("Exception")]
  public void Stored_ForkCrcMismatch_Throws() {
    var archive = Write(("x.txt", "abc"u8.ToArray()));
    archive[98] ^= 0x01; // first data byte
    using var r = Open(archive);
    Assert.Throws<InvalidDataException>(() => r.Extract(r.Entries[0]));
  }

  [Test, Category("Exception")]
  public void HeaderCrcMismatch_ListsButRefusesExtraction() {
    var archive = Write(("x.txt", "abc"u8.ToArray()));
    archive[4 + 72] ^= 0x01; // Finder flags: covered by the header CRC, changes nothing else
    using var r = Open(archive);
    Assert.That(r.Entries[0].HeaderCrcValid, Is.False);
    Assert.Throws<InvalidDataException>(() => r.Extract(r.Entries[0]));
  }

  [Test, Category("Exception")]
  public void EncryptedEntry_ListedButRefused() {
    using var ms = new MemoryStream();
    PackItTestEncoder.WriteStoredEntry(ms, "plain.txt", "ok"u8.ToArray(), []);
    ms.Write("PMa5"u8);
    ms.Write(new byte[40]);
    ms.Position = 0;
    using var r = new PackItReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(2));
    Assert.That(r.Entries[1].IsEncrypted, Is.True);
    Assert.That(r.IsIncomplete, Is.True);
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo("ok"u8.ToArray()));
    Assert.Throws<NotSupportedException>(() => r.Extract(r.Entries[1]));
  }

  // ── Damaged and foreign input ─────────────────────────────────────────────────

  [Test, Category("EdgeCase")]
  public void MissingEndMarker_IsTolerated() {
    var archive = Write(("a.txt", "x"u8.ToArray()));
    using var r = Open(archive[..^4]);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.IsIncomplete, Is.False);
  }

  [Test, Category("EdgeCase")]
  public void TruncatedSecondEntry_KeepsFirstAndFlagsIncomplete() {
    var archive = Write(("a.txt", "first"u8.ToArray()), ("b.txt", new byte[100]));
    using var r = Open(archive[..^50]);
    Assert.That(r.Entries.Select(e => e.Name), Is.EqualTo(new[] { "a.txt" }));
    Assert.That(r.IsIncomplete, Is.True);
  }

  [Test, Category("EdgeCase")]
  public void UnknownMagicAfterEntries_StopsAndFlagsIncomplete() {
    var archive = Write(("valid.txt", [0x41]));
    var damaged = archive[..^4].Concat("XXXX"u8.ToArray()).ToArray();
    using var r = Open(damaged);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.IsIncomplete, Is.True);
  }

  [Test, Category("Exception")]
  [TestCase(new byte[0])]
  [TestCase(new byte[] { (byte)'P', (byte)'K', 3, 4, 0, 0 })]
  public void NotAPackItArchive_Throws(byte[] bytes) =>
    Assert.Throws<InvalidDataException>(() => _ = Open(bytes));

  [Test, Category("Exception")]
  public void Extract_NullEntry_Throws() {
    using var r = Open(Write(("x.txt", [0x41])));
    Assert.Throws<ArgumentNullException>(() => r.Extract(null!));
  }

  [Test, Category("Exception")]
  public void Dispose_ThenExtract_Throws() {
    var r = Open(Write(("f.txt", [0x41])));
    var entry = r.Entries[0];
    r.Dispose();
    Assert.Throws<ObjectDisposedException>(() => r.Extract(entry));
  }

  [Test, Category("HappyPath")]
  public void Reader_LeaveOpen_True_StreamRemainsUsable() {
    using var ms = new MemoryStream(Write(("f.txt", [0x41])));
    using (var r = new PackItReader(ms, leaveOpen: true))
      _ = r.Entries;
    Assert.That(ms.CanRead, Is.True);
  }
}

/// <summary>
/// Writes PackIt entries for tests, including "PMa4" Huffman entries, which the product writer does not
/// produce. The Huffman tree is built from byte frequencies; any prefix tree is valid PackIt.
/// </summary>
internal static class PackItTestEncoder {
  public static void WriteStoredEntry(Stream output, string name, byte[] data, byte[] rsrc) {
    output.Write("PMag"u8);
    output.Write(Body(name, data, rsrc, corruptForkCrc: false));
  }

  public static void WriteCompressedEntry(Stream output, string name, byte[] data, byte[] rsrc, bool corruptForkCrc = false) {
    var body = Body(name, data, rsrc, corruptForkCrc);
    output.Write("PMa4"u8);
    output.Write(Huffman(body));
  }

  private static byte[] Body(string name, byte[] data, byte[] rsrc, bool corruptForkCrc) {
    var header = new byte[94];
    var nameBytes = Encoding.Latin1.GetBytes(name);
    header[0] = (byte)nameBytes.Length;
    nameBytes.CopyTo(header, 1);
    "TEXT"u8.CopyTo(header.AsSpan(64));
    "ttxt"u8.CopyTo(header.AsSpan(68));
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(76), (uint)data.Length);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(80), (uint)rsrc.Length);
    BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(92), Crc16Ccitt.Compute(header.AsSpan(0, 92)));
    var forks = data.Concat(rsrc).ToArray();
    var crc = Crc16Ccitt.Compute(forks);
    if (corruptForkCrc) crc ^= 0x5555;
    var trailer = new byte[2];
    BinaryPrimitives.WriteUInt16BigEndian(trailer, crc);
    return [.. header, .. forks, .. trailer];
  }

  private sealed record Node(int Weight, int Symbol, Node? Zero, Node? One);

  /// <summary>Huffman-codes <paramref name="body"/> as PackIt does: pre-order tree, then MSB-first codes, byte-padded.</summary>
  public static byte[] Huffman(byte[] body) {
    var counts = new int[256];
    foreach (var b in body) ++counts[b];
    var queue = new PriorityQueue<Node, (int, int)>();
    var order = 0;
    for (var s = 0; s < 256; ++s)
      if (counts[s] > 0)
        queue.Enqueue(new Node(counts[s], s, null, null), (counts[s], order++));
    if (queue.Count == 1) {
      var only = queue.Dequeue();
      queue.Enqueue(new Node(only.Weight, -1, only, new Node(0, (only.Symbol + 1) & 0xFF, null, null)), (only.Weight, order++));
    }
    while (queue.Count > 1) {
      var a = queue.Dequeue();
      var b = queue.Dequeue();
      queue.Enqueue(new Node(a.Weight + b.Weight, -1, a, b), (a.Weight + b.Weight, order++));
    }
    var root = queue.Dequeue();

    var codes = new (uint Bits, int Length)[256];
    var bits = new List<bool>();
    void Emit(Node n, uint code, int length) {
      if (n.Zero is null) {
        bits.Add(true);
        for (var i = 7; i >= 0; --i) bits.Add(((n.Symbol >> i) & 1) != 0);
        codes[n.Symbol] = (code, length);
        return;
      }
      bits.Add(false);
      Emit(n.Zero, code << 1, length + 1);
      Emit(n.One!, (code << 1) | 1, length + 1);
    }
    Emit(root, 0, 0);
    foreach (var b in body) {
      var (code, length) = codes[b];
      for (var i = length - 1; i >= 0; --i) bits.Add(((code >> i) & 1) != 0);
    }

    var result = new byte[(bits.Count + 7) / 8];
    for (var i = 0; i < bits.Count; ++i)
      if (bits[i]) result[i >> 3] |= (byte)(0x80 >> (i & 7));
    return result;
  }
}
