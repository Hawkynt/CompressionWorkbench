using System.Text;
using FileFormat.Nds;

namespace Compression.Tests.Nds;

[TestFixture]
public class NdsTests {
  // ── Helpers ────────────────────────────────────────────────────────────

  /// <summary>
  /// Builds a minimal NDS ROM image with a NitroFS file system containing the given files.
  /// </summary>
  private static byte[] BuildNdsRom(params (string Name, byte[] Data)[] files) {
    // Allocate sectors: 0x1000 header + FAT + FNT + file data
    const int headerSize = 0x1000;
    var fntSize = 8 + 1; // root dir main entry (8 bytes) + end byte
    foreach (var f in files)
      fntSize += 1 + f.Name.Length; // length byte + name

    // Pad FNT to 4-byte boundary
    fntSize = (fntSize + 3) & ~3;

    var fatOffset = headerSize;
    var fatSize = files.Length * 8;
    var fntOffset = fatOffset + fatSize;
    var dataOffset = fntOffset + fntSize;

    // Calculate total size
    var totalData = 0;
    foreach (var f in files)
      totalData += f.Data.Length;
    var romSize = dataOffset + totalData;

    var rom = new byte[romSize];

    // Write header
    Encoding.ASCII.GetBytes("TESTGAME\0\0\0\0").CopyTo(rom, 0x00); // game title (12 bytes)
    Encoding.ASCII.GetBytes("TTTT").CopyTo(rom, 0x0C); // game code
    Encoding.ASCII.GetBytes("01").CopyTo(rom, 0x10); // maker code
    rom[0x12] = 0; // unit code

    // NDS logo area (0xC0): write the known magic for detection
    rom[0xC0] = 0x24; rom[0xC1] = 0xFF; rom[0xC2] = 0xAE; rom[0xC3] = 0x51;
    rom[0xC4] = 0x69; rom[0xC5] = 0x9A; rom[0xC6] = 0xA2; rom[0xC7] = 0x21;

    // FNT offset/size, FAT offset/size
    WriteUInt32LE(rom, 0x40, (uint)fntOffset);
    WriteUInt32LE(rom, 0x44, (uint)fntSize);
    WriteUInt32LE(rom, 0x48, (uint)fatOffset);
    WriteUInt32LE(rom, 0x4C, (uint)(fatSize));

    // ROM size
    WriteUInt32LE(rom, 0x80, (uint)romSize);

    // Write FAT
    var currentDataOffset = dataOffset;
    for (var i = 0; i < files.Length; i++) {
      WriteUInt32LE(rom, fatOffset + i * 8, (uint)currentDataOffset);
      WriteUInt32LE(rom, fatOffset + i * 8 + 4, (uint)(currentDataOffset + files[i].Data.Length));
      Buffer.BlockCopy(files[i].Data, 0, rom, currentDataOffset, files[i].Data.Length);
      currentDataOffset += files[i].Data.Length;
    }

    // Write FNT
    // Root directory main entry: sub-table offset (uint32), first file ID (uint16), total dirs (uint16)
    WriteUInt32LE(rom, fntOffset, 8); // sub-table starts at offset 8 within FNT
    WriteUInt16LE(rom, fntOffset + 4, 0); // first file ID = 0
    WriteUInt16LE(rom, fntOffset + 6, 1); // total directories = 1 (root only)

    // Sub-table entries for root directory
    var pos = fntOffset + 8;
    for (var i = 0; i < files.Length; i++) {
      var nameBytes = Encoding.ASCII.GetBytes(files[i].Name);
      rom[pos++] = (byte)nameBytes.Length; // length byte (no 0x80 bit = file)
      Buffer.BlockCopy(nameBytes, 0, rom, pos, nameBytes.Length);
      pos += nameBytes.Length;
    }
    rom[pos] = 0x00; // end of sub-table

    return rom;
  }

  private static void WriteUInt32LE(byte[] data, int offset, uint value) {
    data[offset] = (byte)value;
    data[offset + 1] = (byte)(value >> 8);
    data[offset + 2] = (byte)(value >> 16);
    data[offset + 3] = (byte)(value >> 24);
  }

  private static void WriteUInt16LE(byte[] data, int offset, ushort value) {
    data[offset] = (byte)value;
    data[offset + 1] = (byte)(value >> 8);
  }

  // ── Tests ──────────────────────────────────────────────────────────────

  [Category("HappyPath")]
  [Test]
  public void Reader_ParsesHeader() {
    var rom = BuildNdsRom(("test.txt"u8.ToArray().Length > 0 ? "test.txt" : "test.txt", "Hello"u8.ToArray()));
    using var ms = new MemoryStream(rom);
    var reader = new NdsReader(ms);

    Assert.That(reader.GameTitle, Does.StartWith("TESTGAME"));
    Assert.That(reader.GameCode, Is.EqualTo("TTTT"));
  }

  [Category("HappyPath")]
  [Test]
  public void Reader_ListsFiles() {
    var rom = BuildNdsRom(
      ("file1.txt", "Hello"u8.ToArray()),
      ("file2.bin", new byte[] { 1, 2, 3, 4 }));

    using var ms = new MemoryStream(rom);
    var reader = new NdsReader(ms);

    Assert.That(reader.Entries, Has.Count.EqualTo(2));
    Assert.That(reader.Entries[0].Name, Is.EqualTo("file1.txt"));
    Assert.That(reader.Entries[1].Name, Is.EqualTo("file2.bin"));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Reader_ExtractsFileData() {
    var data1 = "Hello, NDS!"u8.ToArray();
    var data2 = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
    var rom = BuildNdsRom(("hello.txt", data1), ("dead.bin", data2));

    using var ms = new MemoryStream(rom);
    var reader = new NdsReader(ms);

    Assert.That(reader.Extract(reader.Entries[0]), Is.EqualTo(data1));
    Assert.That(reader.Extract(reader.Entries[1]), Is.EqualTo(data2));
  }

  [Category("HappyPath")]
  [Test]
  public void Reader_FileSizes_AreCorrect() {
    var data = new byte[1024];
    new Random(42).NextBytes(data);
    var rom = BuildNdsRom(("big.bin", data));

    using var ms = new MemoryStream(rom);
    var reader = new NdsReader(ms);

    Assert.That(reader.Entries[0].Size, Is.EqualTo(1024));
  }

  // ── Descriptor ────────────────────────────────────────────────────────

  [Category("HappyPath")]
  [Test]
  public void Descriptor_Properties() {
    var desc = new NdsFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("Nds"));
    Assert.That(desc.DefaultExtension, Is.EqualTo(".nds"));
    Assert.That(desc.MagicSignatures.Count, Is.EqualTo(1));
    Assert.That(desc.MagicSignatures[0].Offset, Is.EqualTo(0xC0));
  }

  [Category("HappyPath")]
  [Test]
  public void Descriptor_List_ReturnsEntries() {
    var rom = BuildNdsRom(("a.txt", [1, 2, 3]), ("b.txt", [4, 5]));
    using var ms = new MemoryStream(rom);
    var desc = new NdsFormatDescriptor();
    var entries = desc.List(ms, null);

    Assert.That(entries, Has.Count.EqualTo(2));
    Assert.That(entries[0].Name, Is.EqualTo("a.txt"));
    Assert.That(entries[1].Name, Is.EqualTo("b.txt"));
  }

  [Category("HappyPath")]
  [Test]
  public void Descriptor_Extract_WritesFiles() {
    var data = "NDS extract test"u8.ToArray();
    var rom = BuildNdsRom(("test.txt", data));

    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(rom);
      var desc = new NdsFormatDescriptor();
      desc.Extract(ms, tmp, null, null);

      var extracted = File.ReadAllBytes(Path.Combine(tmp, "test.txt"));
      Assert.That(extracted, Is.EqualTo(data));
    } finally {
      Directory.Delete(tmp, true);
    }
  }
}
