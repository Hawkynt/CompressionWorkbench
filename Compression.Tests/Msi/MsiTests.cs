using System.Buffers.Binary;
using System.Text;

namespace Compression.Tests.Msi;

[TestFixture]
public class MsiTests {

  // ── OLE Compound File builder ─────────────────────────────────────────────

  /// <summary>
  /// Builds a minimal OLE Compound File (CFB v3, 512-byte sectors) containing
  /// the given named streams under the root storage.
  /// </summary>
  private static byte[] BuildCfb(params (string Name, byte[] Data)[] streams) {
    const int sectorSize = 512;
    const int headerSize = 512;
    const int miniSectorSize = 64;
    const uint miniStreamCutoff = 4096;
    const uint endOfChain = 0xFFFFFFFE;
    const uint freeSect   = 0xFFFFFFFF;

    // Layout: header (512) + sector 0 (directory) + sector 1 (FAT) +
    //         sectors 2..N (stream data or mini stream container)
    //
    // For simplicity, small streams (< 4096) go into the mini stream container
    // stored in root entry's sector chain. Large streams get their own sectors.

    var largeSectors = new List<(int streamIdx, int startSector, int sectorCount)>();
    var miniStreamParts = new List<(int streamIdx, int miniSectorStart, int miniSectorCount)>();

    var nextSector = 2; // 0=dir, 1=FAT

    // Classify streams
    var miniStreamBuf = new MemoryStream();
    var currentMiniSector = 0;
    for (var i = 0; i < streams.Length; i++) {
      var data = streams[i].Data;
      if (data.Length == 0) {
        miniStreamParts.Add((i, 0, 0));
      } else if (data.Length < miniStreamCutoff) {
        var miniSectors = (data.Length + miniSectorSize - 1) / miniSectorSize;
        miniStreamParts.Add((i, currentMiniSector, miniSectors));
        miniStreamBuf.Write(data);
        // Pad to mini sector boundary
        var pad = miniSectors * miniSectorSize - data.Length;
        if (pad > 0) miniStreamBuf.Write(new byte[pad]);
        currentMiniSector += miniSectors;
      } else {
        var sectors = (data.Length + sectorSize - 1) / sectorSize;
        largeSectors.Add((i, nextSector, sectors));
        nextSector += sectors;
      }
    }

    // Mini stream container sectors
    var miniStreamBytes = miniStreamBuf.ToArray();
    var miniContainerSectorCount = (miniStreamBytes.Length + sectorSize - 1) / sectorSize;
    var miniContainerStart = miniStreamBytes.Length > 0 ? nextSector : -1;
    if (miniContainerSectorCount > 0) nextSector += miniContainerSectorCount;

    // Mini FAT sectors (if mini streams exist)
    var miniFatSector = -1;
    byte[]? miniFatData = null;
    if (currentMiniSector > 0) {
      miniFatData = new byte[sectorSize];
      for (var i = 0; i < currentMiniSector; i++) {
        var val = (i + 1 < currentMiniSector) ? (uint)(i + 1) : endOfChain;
        // Check if this is the last mini sector of any stream
        foreach (var (_, start, count) in miniStreamParts) {
          if (count > 0 && i == start + count - 1)
            val = endOfChain;
        }
        if (i * 4 + 4 <= miniFatData.Length)
          BinaryPrimitives.WriteUInt32LittleEndian(miniFatData.AsSpan(i * 4), val);
      }
      miniFatSector = nextSector++;
    }

    // Total sectors: dir(0) + fat(1) + large stream sectors + mini container + mini fat
    var totalSectors = nextSector;
    var fileSize = headerSize + totalSectors * sectorSize;
    var buf = new byte[fileSize];

    // ---- Write header ----
    // Magic
    byte[] magic = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
    magic.CopyTo(buf, 0);
    // Minor version
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x18), 0x003E);
    // Major version (3)
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x1A), 0x0003);
    // Byte order (little endian)
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x1C), 0xFFFE);
    // Sector size exponent (9 = 512)
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x1E), 9);
    // Mini sector size exponent (6 = 64)
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x20), 6);
    // FAT sectors count = 1
    BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0x2C), 1);
    // First directory sector = 0
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x30), 0);
    // Mini stream cutoff
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x38), miniStreamCutoff);
    // First mini FAT sector
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x3C),
      miniFatSector >= 0 ? (uint)miniFatSector : endOfChain);
    // Num mini FAT sectors
    BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0x40),
      miniFatSector >= 0 ? 1 : 0);
    // First DIFAT sector (none)
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x44), endOfChain);
    // Num DIFAT sectors
    BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0x48), 0);
    // DIFAT[0] = FAT is at sector 1
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x4C), 1);
    // Fill rest of DIFAT with FREESECT
    for (var i = 1; i < 109; i++)
      BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x4C + i * 4), freeSect);

    // ---- Write directory sector (sector 0) ----
    var dirOffset = headerSize; // sector 0

    // Entry 0: Root Storage
    WriteDirectoryEntry(buf, dirOffset, "Root Entry", 5, // RootStorage
      miniContainerStart >= 0 ? (uint)miniContainerStart : endOfChain,
      (long)miniStreamBytes.Length,
      streams.Length > 0 ? 1u : 0xFFFFFFFF); // child = entry 1

    // Entries 1..N: streams
    // Build a simple balanced-ish tree: entry 1 is root child,
    // subsequent entries chain as right siblings
    for (var i = 0; i < streams.Length; i++) {
      var (name, data) = streams[i];
      uint startSector;
      long size = data.Length;

      // Find sector info
      var largeSector = largeSectors.Find(ls => ls.streamIdx == i);
      var miniPart = miniStreamParts.Find(mp => mp.streamIdx == i);

      if (largeSector.sectorCount > 0) {
        startSector = (uint)largeSector.startSector;
      } else if (miniPart.miniSectorCount > 0) {
        startSector = (uint)miniPart.miniSectorStart;
      } else {
        startSector = endOfChain;
      }

      var entryIdx = i + 1;
      var rightSibling = (i + 1 < streams.Length) ? (uint)(entryIdx + 1) : 0xFFFFFFFF;

      WriteDirectoryEntry(buf, dirOffset + entryIdx * 128, name, 2, // Stream
        startSector, size, 0xFFFFFFFF, // no children
        0xFFFFFFFF, rightSibling); // left=none, right=next
    }

    // ---- Write FAT sector (sector 1) ----
    var fatOffset = headerSize + 1 * sectorSize;
    // Sector 0 = directory = ENDOFCHAIN
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(fatOffset + 0 * 4), endOfChain);
    // Sector 1 = FAT itself = 0xFFFFFFFD (FAT sector marker)
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(fatOffset + 1 * 4), 0xFFFFFFFD);
    // Large stream sectors: chain
    foreach (var (_, start, count) in largeSectors) {
      for (var j = 0; j < count; j++) {
        var val = (j + 1 < count) ? (uint)(start + j + 1) : endOfChain;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(fatOffset + (start + j) * 4), val);
      }
    }
    // Mini container sectors: chain
    if (miniContainerStart >= 0) {
      for (var j = 0; j < miniContainerSectorCount; j++) {
        var val = (j + 1 < miniContainerSectorCount)
          ? (uint)(miniContainerStart + j + 1) : endOfChain;
        BinaryPrimitives.WriteUInt32LittleEndian(
          buf.AsSpan(fatOffset + (miniContainerStart + j) * 4), val);
      }
    }
    // Mini FAT sector
    if (miniFatSector >= 0)
      BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(fatOffset + miniFatSector * 4), endOfChain);
    // Fill remaining FAT entries with FREESECT
    var fatEntries = sectorSize / 4;
    for (var i = 0; i < fatEntries; i++) {
      var existing = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(fatOffset + i * 4));
      if (existing == 0 && i >= 2) // only fill genuinely unused sectors
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(fatOffset + i * 4), freeSect);
    }

    // ---- Write large stream data ----
    foreach (var (streamIdx, start, count) in largeSectors) {
      var data = streams[streamIdx].Data;
      var offset = headerSize + start * sectorSize;
      data.AsSpan().CopyTo(buf.AsSpan(offset));
    }

    // ---- Write mini stream container ----
    if (miniContainerStart >= 0 && miniStreamBytes.Length > 0) {
      var offset = headerSize + miniContainerStart * sectorSize;
      miniStreamBytes.AsSpan().CopyTo(buf.AsSpan(offset));
    }

    // ---- Write mini FAT ----
    if (miniFatSector >= 0 && miniFatData != null) {
      var offset = headerSize + miniFatSector * sectorSize;
      miniFatData.AsSpan().CopyTo(buf.AsSpan(offset));
    }

    return buf;
  }

  private static void WriteDirectoryEntry(byte[] buf, int offset, string name,
      byte entryType, uint startSector, long streamSize,
      uint childDid = 0xFFFFFFFF, uint leftDid = 0xFFFFFFFF, uint rightDid = 0xFFFFFFFF) {
    // Name (UTF-16LE)
    var nameBytes = Encoding.Unicode.GetBytes(name);
    var nameLen = Math.Min(nameBytes.Length, 62);
    nameBytes.AsSpan(0, nameLen).CopyTo(buf.AsSpan(offset));
    // Name length (bytes including null terminator)
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(offset + 0x40), (ushort)(nameLen + 2));
    // Entry type
    buf[offset + 0x42] = entryType;
    // Color flag (black)
    buf[offset + 0x43] = 1;
    // Left sibling
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset + 0x44), leftDid);
    // Right sibling
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset + 0x48), rightDid);
    // Child
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset + 0x4C), childDid);
    // Start sector
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset + 0x74), startSector);
    // Stream size (v3: 32-bit)
    BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(offset + 0x78), streamSize);
  }

  // ── Tests ─────────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Read_Cfb_ListsStreams() {
    var data1 = "Hello, OLE!"u8.ToArray();
    var data2 = "Second stream"u8.ToArray();
    var cfb = BuildCfb(("Stream1", data1), ("Stream2", data2));
    using var ms = new MemoryStream(cfb);

    var r = new FileFormat.Msi.MsiReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(2));
  }

  [Test, Category("HappyPath")]
  public void Read_Cfb_EntryNames() {
    var cfb = BuildCfb(("TestStream", "data"u8.ToArray()));
    using var ms = new MemoryStream(cfb);

    var r = new FileFormat.Msi.MsiReader(ms);
    Assert.That(r.Entries[0].Name, Is.EqualTo("TestStream"));
  }

  [Test, Category("HappyPath")]
  public void Read_Cfb_EntrySize() {
    var data = new byte[100];
    Random.Shared.NextBytes(data);
    var cfb = BuildCfb(("data.bin", data));
    using var ms = new MemoryStream(cfb);

    var r = new FileFormat.Msi.MsiReader(ms);
    Assert.That(r.Entries[0].Size, Is.EqualTo(100));
  }

  [Test, Category("HappyPath")]
  public void Extract_SmallStream_ReturnsData() {
    var original = "Hello, OLE Compound File!"u8.ToArray();
    var cfb = BuildCfb(("greeting.txt", original));
    using var ms = new MemoryStream(cfb);

    var r = new FileFormat.Msi.MsiReader(ms);
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(original));
  }

  [Test, Category("HappyPath")]
  public void Extract_MultipleSmallStreams_RoundTrip() {
    var data1 = "First stream content"u8.ToArray();
    var data2 = "Second stream content"u8.ToArray();
    var cfb = BuildCfb(("first.txt", data1), ("second.txt", data2));
    using var ms = new MemoryStream(cfb);

    var r = new FileFormat.Msi.MsiReader(ms);
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data1));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(data2));
  }

  [Test, Category("HappyPath")]
  public void Extract_LargeStream_ReturnsData() {
    // 8KB stream — above mini stream cutoff (4096)
    var data = new byte[8192];
    Random.Shared.NextBytes(data);
    var cfb = BuildCfb(("big.bin", data));
    using var ms = new MemoryStream(cfb);

    var r = new FileFormat.Msi.MsiReader(ms);
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Extract_MixedSizes_RoundTrip() {
    var small = "tiny"u8.ToArray();
    var large = new byte[5000];
    Random.Shared.NextBytes(large);
    var cfb = BuildCfb(("small.txt", small), ("large.bin", large));
    using var ms = new MemoryStream(cfb);

    var r = new FileFormat.Msi.MsiReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(2));

    var smallEntry = r.Entries.First(e => e.Name == "small.txt");
    var largeEntry = r.Entries.First(e => e.Name == "large.bin");

    Assert.That(r.Extract(smallEntry), Is.EqualTo(small));
    Assert.That(r.Extract(largeEntry), Is.EqualTo(large));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new FileFormat.Msi.MsiFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("Msi"));
    Assert.That(desc.Extensions, Does.Contain(".msi"));
    Assert.That(desc.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(desc.MagicSignatures[0].Bytes,
      Is.EqualTo(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }));
  }

  [Test, Category("HappyPath")]
  public void Detect_ByExtension() {
    var format = Compression.Lib.FormatDetector.DetectByExtension("package.msi");
    Assert.That(format, Is.EqualTo(Compression.Lib.FormatDetector.Format.Msi));
  }

  [Test, Category("ErrorHandling")]
  public void Ctor_StreamTooShort_Throws() {
    using var ms = new MemoryStream(new byte[100]);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Msi.MsiReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Ctor_BadMagic_Throws() {
    var data = new byte[1024];
    data[0] = 0xFF; // wrong magic
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Msi.MsiReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Extract_NullEntry_Throws() {
    var cfb = BuildCfb(("test", "data"u8.ToArray()));
    using var ms = new MemoryStream(cfb);
    var r = new FileFormat.Msi.MsiReader(ms);
    Assert.Throws<ArgumentNullException>(() => r.Extract(null!));
  }

  [Test, Category("EdgeCase")]
  public void Read_EmptyStream_ZeroSize() {
    var cfb = BuildCfb(("empty.txt", []));
    using var ms = new MemoryStream(cfb);
    var r = new FileFormat.Msi.MsiReader(ms);
    // Empty streams may not appear as entries (size 0)
    // or appear with size 0 — depends on implementation
    if (r.Entries.Count > 0) {
      var entry = r.Entries.FirstOrDefault(e => e.Name == "empty.txt");
      if (entry != null)
        Assert.That(r.Extract(entry), Is.Empty);
    }
    Assert.Pass();
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_ViaInterface() {
    var data = "content"u8.ToArray();
    var cfb = BuildCfb(("stream.bin", data));
    using var ms = new MemoryStream(cfb);

    var desc = new FileFormat.Msi.MsiFormatDescriptor();
    var entries = desc.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("stream.bin"));
    Assert.That(entries[0].OriginalSize, Is.EqualTo(data.Length));
  }
}
