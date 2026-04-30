using System.Text;

namespace Compression.Tests.Vpp;

[TestFixture]
public class VppTests {

  // 0xCE0A8951 little-endian on disk = 51 89 0A CE.
  private static readonly byte[] MagicBytes = [0x51, 0x89, 0x0A, 0xCE];
  private const int Alignment      = 2048;
  private const int HeaderSize     = 2048;
  private const int IndexEntrySize = 64;
  private const int NameFieldSize  = 60;

  // ──────────────────────────── Synthetic-bytes helpers ────────────────────────────

  /// <summary>Builds a minimal v1 .vpp byte sequence for use in reader tests.</summary>
  private static byte[] BuildSyntheticVpp(IReadOnlyList<(string Name, byte[] Data)> entries) {
    var indexBlockSize = AlignUp(entries.Count * IndexEntrySize, Alignment);

    long payloadTotal = 0;
    foreach (var (_, data) in entries)
      payloadTotal += AlignUp(data.Length, Alignment);

    var totalSize = HeaderSize + indexBlockSize + payloadTotal;

    var buffer = new byte[totalSize];

    // Header.
    BitConverter.GetBytes(0xCE0A8951u).CopyTo(buffer, 0);
    BitConverter.GetBytes(1u).CopyTo(buffer, 4);
    BitConverter.GetBytes((uint)entries.Count).CopyTo(buffer, 8);
    BitConverter.GetBytes((uint)totalSize).CopyTo(buffer, 12);

    // Index entries.
    var indexCursor = HeaderSize;
    foreach (var (name, data) in entries) {
      var nameBytes = Encoding.ASCII.GetBytes(name);
      Array.Copy(nameBytes, 0, buffer, indexCursor, Math.Min(nameBytes.Length, NameFieldSize - 1));
      BitConverter.GetBytes((uint)data.Length).CopyTo(buffer, indexCursor + NameFieldSize);
      indexCursor += IndexEntrySize;
    }

    // Payloads, each padded to alignment.
    var dataCursor = HeaderSize + indexBlockSize;
    foreach (var (_, data) in entries) {
      Array.Copy(data, 0, buffer, dataCursor, data.Length);
      dataCursor += AlignUp(data.Length, Alignment);
    }

    return buffer;
  }

  private static int AlignUp(int value, int alignment) {
    var remainder = value % alignment;
    return remainder == 0 ? value : value + (alignment - remainder);
  }

  private static long AlignUp(long value, long alignment) {
    var remainder = value % alignment;
    return remainder == 0 ? value : value + (alignment - remainder);
  }

  // ──────────────────────────── Reader: Synthetic ────────────────────────────

  [Test, Category("HappyPath")]
  public void Reader_ParsesSyntheticV1() {
    var data = "Red Faction asset"u8.ToArray();
    var bytes = BuildSyntheticVpp([("level1.tbl", data)]);

    using var ms = new MemoryStream(bytes);
    var r = new FileFormat.Vpp.VppReader(ms);

    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("level1.tbl"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(data.Length));
    // First payload always lands one alignment block past the header (single index entry < 2048).
    Assert.That(r.Entries[0].Offset, Is.EqualTo(HeaderSize + Alignment));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
    Assert.That(r.DeclaredTotalSize, Is.EqualTo(bytes.Length));
  }

  [Test, Category("HappyPath")]
  public void Reader_HandlesMultipleEntries() {
    var d1 = new byte[100];     // < alignment
    var d2 = new byte[Alignment]; // exactly aligned — must NOT add an extra pad block
    var d3 = new byte[Alignment + 1]; // forces two-block pad
    Array.Fill(d1, (byte)0x11);
    Array.Fill(d2, (byte)0x22);
    Array.Fill(d3, (byte)0x33);

    var bytes = BuildSyntheticVpp([("a.bin", d1), ("b.bin", d2), ("c.bin", d3)]);
    using var ms = new MemoryStream(bytes);
    var r = new FileFormat.Vpp.VppReader(ms);

    Assert.That(r.Entries, Has.Count.EqualTo(3));

    // 3 index entries × 64 = 192 bytes < 2048, so the index block is one alignment unit.
    var firstOffset = HeaderSize + Alignment;
    Assert.That(r.Entries[0].Offset, Is.EqualTo(firstOffset));
    Assert.That(r.Entries[1].Offset, Is.EqualTo(firstOffset + Alignment));            // d1 (100B) → 1 block
    Assert.That(r.Entries[2].Offset, Is.EqualTo(firstOffset + Alignment + Alignment)); // d2 exactly fills 1 block

    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(d1));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(d2));
    Assert.That(r.Extract(r.Entries[2]), Is.EqualTo(d3));
  }

  // ──────────────────────────── Reader: Error handling ────────────────────────────

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsBadMagic() {
    var buf = new byte[HeaderSize];
    Array.Fill(buf, (byte)0xFF);
    using var ms = new MemoryStream(buf);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Vpp.VppReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsNonV1() {
    var buf = new byte[HeaderSize];
    MagicBytes.CopyTo(buf, 0);
    BitConverter.GetBytes(2u).CopyTo(buf, 4); // version 2 — not supported
    BitConverter.GetBytes(0u).CopyTo(buf, 8);
    BitConverter.GetBytes((uint)HeaderSize).CopyTo(buf, 12);
    using var ms = new MemoryStream(buf);
    Assert.Throws<NotSupportedException>(() => _ = new FileFormat.Vpp.VppReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsTruncatedHeader() {
    using var ms = new MemoryStream(new byte[16]);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Vpp.VppReader(ms));
  }

  // ──────────────────────────── Descriptor ────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileFormat.Vpp.VppFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Vpp"));
    Assert.That(d.DisplayName, Is.EqualTo("Volition Package (RF1)"));
    Assert.That(d.Extensions, Contains.Item(".vpp"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".vpp"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(MagicBytes));
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
    Assert.That(d.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Archive));
    Assert.That(d.Methods, Has.Count.EqualTo(1));
    Assert.That(d.Methods[0].Name, Is.EqualTo("vpp1"));
  }

  // ──────────────────────────── Writer / Round-trip ────────────────────────────

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var data = "single payload"u8.ToArray();
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Vpp.VppWriter(ms, leaveOpen: true))
      w.AddEntry("readme.txt", data);
    ms.Position = 0;

    var r = new FileFormat.Vpp.VppReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("readme.txt"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(data.Length));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
    // The on-stream length must be a multiple of the alignment block.
    Assert.That(ms.Length % Alignment, Is.EqualTo(0));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_MultipleFiles() {
    // Mix of < block, == block, > block to exercise the alignment edge.
    var d1 = Enumerable.Range(0, 13).Select(i => (byte)i).ToArray();
    var d2 = new byte[Alignment];
    var d3 = new byte[Alignment * 2 + 7];
    Array.Fill(d2, (byte)0xA5);
    for (var i = 0; i < d3.Length; ++i)
      d3[i] = (byte)(i * 31);

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Vpp.VppWriter(ms, leaveOpen: true)) {
      w.AddEntry("first.dat",  d1);
      w.AddEntry("second.bin", d2);
      w.AddEntry("third.lvl",  d3);
    }
    ms.Position = 0;

    var r = new FileFormat.Vpp.VppReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(3));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(d1));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(d2));
    Assert.That(r.Extract(r.Entries[2]), Is.EqualTo(d3));

    // Header total_file_size field must equal the actual stream length (writer backpatches).
    Assert.That(r.DeclaredTotalSize, Is.EqualTo(ms.Length));

    // Every entry must start on an alignment boundary — that's the silent corruption guard.
    foreach (var e in r.Entries)
      Assert.That(e.Offset % Alignment, Is.EqualTo(0), $"Entry '{e.Name}' offset {e.Offset} not aligned");
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_EmptyFile() {
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Vpp.VppWriter(ms, leaveOpen: true))
      w.AddEntry("zero.bin", []);
    ms.Position = 0;

    var r = new FileFormat.Vpp.VppReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Size, Is.EqualTo(0));
    Assert.That(r.Extract(r.Entries[0]), Is.Empty);
  }

  [Test, Category("ErrorHandling")]
  public void Writer_RejectsLongName() {
    var tooLong = new string('x', 60); // 60 bytes — one over the 59-byte limit
    using var ms = new MemoryStream();
    using var w  = new FileFormat.Vpp.VppWriter(ms, leaveOpen: true);
    Assert.Throws<ArgumentException>(() => w.AddEntry(tooLong, [1, 2, 3]));
  }

  [Test, Category("HappyPath")]
  public void Writer_AcceptsMaxLengthName() {
    var maxLength = new string('y', 59); // exactly the 59-byte ceiling
    var data = "ok"u8.ToArray();

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Vpp.VppWriter(ms, leaveOpen: true))
      w.AddEntry(maxLength, data);
    ms.Position = 0;

    var r = new FileFormat.Vpp.VppReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo(maxLength));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }
}
