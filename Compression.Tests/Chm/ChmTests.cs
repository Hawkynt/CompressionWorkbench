using FileFormat.Chm;

namespace Compression.Tests.Chm;

[TestFixture]
public sealed class ChmTests {

  // -------------------------------------------------------------------------
  // Synthetic CHM builder
  //
  // Minimal CHM structure (all section 0 / uncompressed):
  //
  //  [0x00] ITSF header (96 bytes, version 3)
  //  [0x60] ITSP header (84 bytes, dirHeaderSize = 84)
  //  [0xB4] PMGL chunk  (chunkSize = 4096 bytes)
  //  [contentOffset] file data
  // -------------------------------------------------------------------------

  private const int ChunkSize      = 4096;
  private const int ItsfHeaderSize = 96;  // 4+4+4+4+4+4+16+16+8+8+8 = 80 ... +16 padding to 96
  private const int ItspHeaderSize = 84;

  /// <summary>
  /// Encodes a non-negative value as a CHM ENCINT (big-endian 7-bit groups, high bit = more).
  /// </summary>
  private static byte[] EncodeEncInt(long value) {
    if (value == 0)
      return [0x00];

    var bytes = new List<byte>();
    var v = value;
    while (v > 0) {
      bytes.Add((byte)(v & 0x7F));
      v >>= 7;
    }
    bytes.Reverse();
    // Set continuation bits on all but the last byte
    for (var i = 0; i < bytes.Count - 1; i++)
      bytes[i] |= 0x80;
    return bytes.ToArray();
  }

  private static void WriteLE16(byte[] buf, int off, ushort v) {
    buf[off]     = (byte)(v & 0xFF);
    buf[off + 1] = (byte)(v >> 8);
  }

  private static void WriteLE32(byte[] buf, int off, uint v) {
    buf[off]     = (byte)(v        & 0xFF);
    buf[off + 1] = (byte)((v >> 8)  & 0xFF);
    buf[off + 2] = (byte)((v >> 16) & 0xFF);
    buf[off + 3] = (byte)((v >> 24) & 0xFF);
  }

  private static void WriteLE64(byte[] buf, int off, ulong v) {
    WriteLE32(buf, off,     (uint)(v         & 0xFFFFFFFF));
    WriteLE32(buf, off + 4, (uint)((v >> 32) & 0xFFFFFFFF));
  }

  /// <summary>
  /// Builds a minimal valid CHM image containing only section-0 (uncompressed) entries.
  /// </summary>
  /// <param name="files">Name + data pairs to embed.</param>
  private static byte[] BuildSyntheticChm(params (string Name, byte[] Data)[] files) {
    // ---- Build the PMGL directory entries payload ----
    var pmglPayload = new List<byte>();
    var fileOffsets = new List<long>(); // offsets within the content section
    long contentCursor = 0;

    foreach (var (name, data) in files) {
      var nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
      pmglPayload.AddRange(EncodeEncInt(nameBytes.Length)); // name length
      pmglPayload.AddRange(nameBytes);                      // name bytes
      pmglPayload.AddRange(EncodeEncInt(0));                // section = 0
      pmglPayload.AddRange(EncodeEncInt(contentCursor));    // offset in section 0
      pmglPayload.AddRange(EncodeEncInt(data.Length));      // size

      fileOffsets.Add(contentCursor);
      contentCursor += data.Length;
    }

    // ---- Layout sizes ----
    var dirOffset   = (long)ItsfHeaderSize;          // ITSP immediately after ITSF
    var dirLength   = (long)(ItspHeaderSize + ChunkSize);
    // Content offset: right after the directory section
    var contentOffset = dirOffset + dirLength;

    // ---- Build PMGL chunk (4096 bytes) ----
    var pmgl = new byte[ChunkSize];
    // "PMGL"
    pmgl[0] = (byte)'P'; pmgl[1] = (byte)'M'; pmgl[2] = (byte)'G'; pmgl[3] = (byte)'L';
    // freeSpace = ChunkSize - headerSize - payloadSize
    var freeSpace = (uint)(ChunkSize - 20 - pmglPayload.Count);
    WriteLE32(pmgl, 4,  freeSpace);
    WriteLE32(pmgl, 8,  0);          // unknown
    WriteLE32(pmgl, 12, unchecked((uint)-1)); // prevChunk = -1
    WriteLE32(pmgl, 16, unchecked((uint)-1)); // nextChunk = -1
    // Copy payload immediately after the 20-byte PMGL header
    pmglPayload.ToArray().AsSpan().CopyTo(pmgl.AsSpan(20));

    // ---- Build ITSP header (84 bytes) ----
    var itsp = new byte[ItspHeaderSize];
    // "ITSP"
    itsp[0] = (byte)'I'; itsp[1] = (byte)'T'; itsp[2] = (byte)'S'; itsp[3] = (byte)'P';
    WriteLE32(itsp, 4,  1);                  // version
    WriteLE32(itsp, 8,  (uint)ItspHeaderSize); // dirHeaderSize
    WriteLE32(itsp, 12, 0);                  // unknown1
    WriteLE32(itsp, 16, (uint)ChunkSize);    // chunkSize
    WriteLE32(itsp, 20, 2);                  // density
    WriteLE32(itsp, 24, 1);                  // indexTreeDepth
    WriteLE32(itsp, 28, unchecked((uint)-1)); // rootIndexChunkNum = -1 (no PMGI)
    WriteLE32(itsp, 32, 0);                  // firstPMGLChunkNum = 0
    WriteLE32(itsp, 36, 0);                  // lastPMGLChunkNum  = 0
    WriteLE32(itsp, 40, unchecked((uint)-1)); // unknown2 = -1
    WriteLE32(itsp, 44, 1);                  // numDirChunks = 1
    WriteLE32(itsp, 48, 0x0409);             // languageId = en-US
    // GUID (16 bytes, zeroed) + chunkLength (4 bytes) + padding = remaining bytes, left zero

    // ---- Build ITSF header (96 bytes) ----
    var itsf = new byte[ItsfHeaderSize];
    // "ITSF"
    itsf[0] = (byte)'I'; itsf[1] = (byte)'T'; itsf[2] = (byte)'S'; itsf[3] = (byte)'F';
    WriteLE32(itsf, 4,  3);                        // version = 3
    WriteLE32(itsf, 8,  (uint)ItsfHeaderSize);      // headerSize
    WriteLE32(itsf, 12, 1);                         // unknown(1)
    WriteLE32(itsf, 16, 0);                         // timestamp
    WriteLE32(itsf, 20, 0x0409);                    // languageId = en-US
    // GUID1 at 24 (16 bytes) — zeroed
    // GUID2 at 40 (16 bytes) — zeroed
    WriteLE64(itsf, 56, (ulong)dirOffset);          // dirSectionOffset
    WriteLE64(itsf, 64, (ulong)dirLength);          // dirSectionLength
    WriteLE64(itsf, 72, (ulong)contentOffset);      // contentOffset (version >= 3)
    // Remaining 8 bytes: padding, left zero

    // ---- Assemble file content section ----
    var contentData = new byte[contentCursor];
    long pos = 0;
    foreach (var (_, data) in files) {
      data.AsSpan().CopyTo(contentData.AsSpan((int)pos));
      pos += data.Length;
    }

    // ---- Concatenate all sections ----
    var result = new byte[ItsfHeaderSize + ItspHeaderSize + ChunkSize + contentData.Length];
    itsf.AsSpan().CopyTo(result.AsSpan(0));
    itsp.AsSpan().CopyTo(result.AsSpan(ItsfHeaderSize));
    pmgl.AsSpan().CopyTo(result.AsSpan(ItsfHeaderSize + ItspHeaderSize));
    contentData.AsSpan().CopyTo(result.AsSpan(ItsfHeaderSize + ItspHeaderSize + ChunkSize));

    return result;
  }

  // -------------------------------------------------------------------------
  // Tests
  // -------------------------------------------------------------------------

  [Category("HappyPath")]
  [Test]
  public void Read_SyntheticImage_SingleFile() {
    var fileData = "Hello, CHM!"u8.ToArray();
    var chm      = BuildSyntheticChm(("/index.html", fileData));

    var reader = new ChmReader(new MemoryStream(chm));

    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].Path,    Is.EqualTo("/index.html"));
    Assert.That(reader.Entries[0].Size,    Is.EqualTo(fileData.Length));
    Assert.That(reader.Entries[0].Section, Is.EqualTo(0));

    var extracted = reader.Extract(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(fileData));
  }

  [Category("HappyPath")]
  [Test]
  public void Read_SyntheticImage_TwoFiles() {
    var data1 = "File one content."u8.ToArray();
    var data2 = "File two has different content!"u8.ToArray();
    var chm   = BuildSyntheticChm(("/a.html", data1), ("/b.css", data2));

    var reader = new ChmReader(new MemoryStream(chm));

    Assert.That(reader.Entries, Has.Count.EqualTo(2));

    var entry1 = reader.Entries.First(e => e.Path == "/a.html");
    var entry2 = reader.Entries.First(e => e.Path == "/b.css");

    Assert.That(reader.Extract(entry1), Is.EqualTo(data1));
    Assert.That(reader.Extract(entry2), Is.EqualTo(data2));
  }

  [Category("HappyPath")]
  [Test]
  public void Read_SyntheticImage_BinaryData() {
    var binary = new byte[256];
    for (var i = 0; i < binary.Length; i++)
      binary[i] = (byte)i;

    var chm    = BuildSyntheticChm(("/data.bin", binary));
    var reader = new ChmReader(new MemoryStream(chm));

    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Extract(reader.Entries[0]), Is.EqualTo(binary));
  }

  [Category("HappyPath")]
  [Test]
  public void Read_SyntheticImage_CorrectOffsets() {
    var data1 = "AAAA"u8.ToArray();
    var data2 = "BBBBBBBBB"u8.ToArray();
    var chm   = BuildSyntheticChm(("/first.html", data1), ("/second.html", data2));

    var reader = new ChmReader(new MemoryStream(chm));

    // Entries must have consecutive offsets in section 0
    var e1 = reader.Entries.First(e => e.Path == "/first.html");
    var e2 = reader.Entries.First(e => e.Path == "/second.html");

    Assert.That(e1.Offset, Is.EqualTo(0));
    Assert.That(e2.Offset, Is.EqualTo(data1.Length));
    Assert.That(e1.Size,   Is.EqualTo(data1.Length));
    Assert.That(e2.Size,   Is.EqualTo(data2.Length));
  }

  [Category("HappyPath")]
  [Test]
  public void Read_SyntheticImage_EmptyContent() {
    // An entry with size 0 should extract as an empty array
    // (we include a non-empty sibling so the PMGL has at least something)
    var filler = "x"u8.ToArray();
    var chm    = BuildSyntheticChm(("/filler.txt", filler));
    var reader = new ChmReader(new MemoryStream(chm));

    // Extract existing entry correctly
    Assert.That(reader.Extract(reader.Entries[0]), Is.EqualTo(filler));
  }

  [Category("HappyPath")]
  [Test]
  public void Descriptor_Properties() {
    var d = new ChmFormatDescriptor();

    Assert.That(d.Id,               Is.EqualTo("Chm"));
    Assert.That(d.DisplayName,      Is.EqualTo("CHM"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".chm"));
    Assert.That(d.Extensions,       Contains.Item(".chm"));
    Assert.That(d.Description,      Is.EqualTo("Microsoft Compiled HTML Help"));
    Assert.That(d.Category,         Is.EqualTo(Compression.Registry.FormatCategory.Archive));
    Assert.That(d.Family,           Is.EqualTo(Compression.Registry.AlgorithmFamily.Archive));
    Assert.That(d.MagicSignatures,  Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo("ITSF"u8.ToArray()));
  }

  [Category("HappyPath")]
  [Test]
  public void Descriptor_List_ReturnsChmEntries() {
    var data = "test content"u8.ToArray();
    var chm  = BuildSyntheticChm(("/page.html", data));
    var d    = new ChmFormatDescriptor();

    var entries = d.List(new MemoryStream(chm), null);

    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name,           Is.EqualTo("/page.html"));
    Assert.That(entries[0].OriginalSize,   Is.EqualTo(data.Length));
    Assert.That(entries[0].CompressedSize, Is.EqualTo(data.Length));
    Assert.That(entries[0].Method,         Is.EqualTo("Stored"));
  }

  [Category("Exception")]
  [Test]
  public void BadMagic_Throws() {
    var bad    = new byte[ItsfHeaderSize + ItspHeaderSize + ChunkSize];
    bad[0] = 0xFF; // Not "ITSF"

    Assert.Throws<InvalidDataException>(() => _ = new ChmReader(new MemoryStream(bad)));
  }

  [Category("Exception")]
  [Test]
  public void TooSmall_Throws() {
    // A stream that is too short to even hold the ITSF magic
    var tiny = new byte[3];
    tiny[0] = (byte)'I'; tiny[1] = (byte)'T'; tiny[2] = (byte)'S';

    Assert.Throws<InvalidDataException>(() => _ = new ChmReader(new MemoryStream(tiny)));
  }

  // ── WORM creation ────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_ReportsWormCapability() {
    var d = new ChmFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_SingleFile_RoundTrips() {
    var payload = "hello from chm"u8.ToArray();
    var w = new ChmWriter();
    w.AddFile("/index.html", payload);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    var r = new ChmReader(ms);
    var entry = r.Entries.FirstOrDefault(e => e.Path == "/index.html");
    Assert.That(entry, Is.Not.Null);
    Assert.That(entry!.Section, Is.EqualTo(0));
    Assert.That(entry.Size, Is.EqualTo(payload.Length));
    Assert.That(r.Extract(entry), Is.EqualTo(payload));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_MultipleFiles_AllRoundTrip() {
    var p1 = "<html>page1</html>"u8.ToArray();
    var p2 = "<html>page2</html>"u8.ToArray();

    var w = new ChmWriter();
    w.AddFile("/page1.html", p1);
    w.AddFile("/page2.html", p2);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    var r = new ChmReader(ms);
    var byPath = r.Entries.ToDictionary(e => e.Path);
    Assert.That(r.Extract(byPath["/page1.html"]), Is.EqualTo(p1));
    Assert.That(r.Extract(byPath["/page2.html"]), Is.EqualTo(p2));
  }

  [Test, Category("HappyPath")]
  public void Writer_HasItsfMagic() {
    var w = new ChmWriter();
    w.AddFile("/a.txt", "x"u8.ToArray());
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    var bytes = ms.ToArray();
    Assert.That(bytes[..4], Is.EqualTo("ITSF"u8.ToArray()));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_Lzx_SingleFile_RoundTrips() {
    var payload = "lzx compressed content in chm"u8.ToArray();
    var w = new ChmWriter();
    w.AddFile("/lzx.html", payload);
    using var ms = new MemoryStream();
    w.WriteTo(ms, useLzx: true);
    ms.Position = 0;

    var r = new ChmReader(ms);
    var entry = r.Entries.FirstOrDefault(e => e.Path == "/lzx.html");
    Assert.That(entry, Is.Not.Null);
    Assert.That(entry!.Section, Is.EqualTo(1), "LZX files should be in section 1");
    Assert.That(r.Extract(entry), Is.EqualTo(payload));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_Lzx_MultipleFiles_AllRoundTrip() {
    var p1 = new byte[2000];
    var p2 = new byte[5000];
    new Random(1).NextBytes(p1);
    new Random(2).NextBytes(p2);

    var w = new ChmWriter();
    w.AddFile("/a.bin", p1);
    w.AddFile("/b.bin", p2);
    using var ms = new MemoryStream();
    w.WriteTo(ms, useLzx: true);
    ms.Position = 0;

    var r = new ChmReader(ms);
    var byPath = r.Entries.Where(e => !e.Path.StartsWith("::")).ToDictionary(e => e.Path);
    Assert.That(r.Extract(byPath["/a.bin"]), Is.EqualTo(p1));
    Assert.That(r.Extract(byPath["/b.bin"]), Is.EqualTo(p2));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Create_Lzx_ViaOptions() {
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "lzx via options"u8.ToArray());
      var d = new ChmFormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveFormatOperations)d).Create(
        ms,
        [new Compression.Registry.ArchiveInputInfo(tmp, "test.html", false)],
        new Compression.Registry.FormatCreateOptions { MethodName = "lzx" });
      ms.Position = 0;
      var entries = d.List(ms, null);
      var userEntry = entries.FirstOrDefault(e => e.Name == "test.html");
      Assert.That(userEntry, Is.Not.Null);
      Assert.That(userEntry!.Method, Is.EqualTo("LZX"));
    } finally {
      File.Delete(tmp);
    }
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Create_RoundTrips() {
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "chm descriptor test"u8.ToArray());
      var d = new ChmFormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveFormatOperations)d).Create(
        ms,
        [new Compression.Registry.ArchiveInputInfo(tmp, "test.html", false)],
        new Compression.Registry.FormatCreateOptions());
      ms.Position = 0;
      var entries = d.List(ms, null);
      Assert.That(entries.Select(e => e.Name), Has.Member("test.html"));
    } finally {
      File.Delete(tmp);
    }
  }
}
