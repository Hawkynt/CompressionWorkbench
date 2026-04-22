using System.Text;
using FileSystem.Adf;

namespace Compression.Tests.Adf;

[TestFixture]
public class AdfTests {
  // ── Helpers ────────────────────────────────────────────────────────────

  private const int SectorSize = 512;
  private const int RootSector = 880;
  private const int TotalSectors = 1760;
  private const int DiskSize = TotalSectors * SectorSize;

  /// <summary>
  /// Builds a minimal ADF disk image with OFS file system containing the given files.
  /// Each file is stored in a single OFS data block (max 488 bytes each).
  /// </summary>
  private static byte[] BuildAdf(params (string Name, byte[] Data)[] files) {
    var disk = new byte[DiskSize];

    // Boot block: "DOS\0" at sector 0
    disk[0] = (byte)'D'; disk[1] = (byte)'O'; disk[2] = (byte)'S'; disk[3] = 0x00; // OFS

    // Root block at sector 880
    var rootOffset = RootSector * SectorSize;
    WriteUInt32BE(disk, rootOffset, 2); // type = T_HEADER
    // Hash table: 72 entries at offset 24 (initially all zero)
    WriteUInt32BE(disk, rootOffset + 508, 1); // secondary type = ST_ROOT

    // Place files into consecutive sectors starting at 881
    var nextSector = RootSector + 1;
    for (var i = 0; i < files.Length; i++) {
      var (name, data) = files[i];
      var headerSector = nextSector++;
      var dataSector = nextSector++;

      // Write file header block
      var headerOffset = headerSector * SectorSize;
      WriteUInt32BE(disk, headerOffset, 2); // type = T_HEADER
      WriteUInt32BE(disk, headerOffset + 4, (uint)headerSector); // header_key
      WriteUInt32BE(disk, headerOffset + 16, (uint)dataSector); // first data block (OFS)
      WriteUInt32BE(disk, headerOffset + 324, (uint)data.Length); // file size
      WriteUInt32BE(disk, headerOffset + 504, (uint)RootSector); // parent

      // Write filename (Pascal string at offset 432)
      var nameBytes = Encoding.ASCII.GetBytes(name);
      disk[headerOffset + 432] = (byte)Math.Min(nameBytes.Length, 30);
      Buffer.BlockCopy(nameBytes, 0, disk, headerOffset + 433, Math.Min(nameBytes.Length, 30));

      WriteUInt32BE(disk, headerOffset + 508, 0xFFFFFFFD); // secondary type = ST_FILE (-3)

      // Write OFS data block
      var dataOffset = dataSector * SectorSize;
      WriteUInt32BE(disk, dataOffset, 8); // type = T_DATA
      WriteUInt32BE(disk, dataOffset + 4, (uint)headerSector); // header_key
      WriteUInt32BE(disk, dataOffset + 8, 1); // sequence number
      WriteUInt32BE(disk, dataOffset + 12, (uint)data.Length); // data size
      // next data block at offset 16 = 0 (only one block)
      Buffer.BlockCopy(data, 0, disk, dataOffset + 24, Math.Min(data.Length, 488));

      // Add to root hash table
      var hash = AmigaHash(name) % 72;
      var hashOffset = rootOffset + 24 + hash * 4;
      var existingEntry = ReadUInt32BE(disk, hashOffset);
      if (existingEntry == 0) {
        WriteUInt32BE(disk, hashOffset, (uint)headerSector);
      } else {
        // Chain: set hash chain pointer in file header
        WriteUInt32BE(disk, headerOffset + 496, existingEntry);
        WriteUInt32BE(disk, hashOffset, (uint)headerSector);
      }
    }

    return disk;
  }

  private static int AmigaHash(string name) {
    var hash = (int)name.Length;
    foreach (var c in name)
      hash = (hash * 13 + char.ToUpper(c)) & 0x7FF;
    return hash % 72;
  }

  private static void WriteUInt32BE(byte[] data, int offset, uint value) {
    data[offset]     = (byte)(value >> 24);
    data[offset + 1] = (byte)(value >> 16);
    data[offset + 2] = (byte)(value >> 8);
    data[offset + 3] = (byte)value;
  }

  private static uint ReadUInt32BE(byte[] data, int offset) =>
    (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);

  // ── Tests ──────────────────────────────────────────────────────────────

  [Category("HappyPath")]
  [Test]
  public void Magic_IsDos() {
    var disk = BuildAdf(("test.txt", "Hello"u8.ToArray()));
    Assert.That(Encoding.ASCII.GetString(disk, 0, 3), Is.EqualTo("DOS"));
  }

  [Category("HappyPath")]
  [Test]
  public void Reader_DetectsOfs() {
    var disk = BuildAdf(("test.txt", [1, 2, 3]));
    using var ms = new MemoryStream(disk);
    var reader = new AdfReader(ms);
    Assert.That(reader.IsFfs, Is.False);
  }

  [Category("HappyPath")]
  [Test]
  public void Reader_ListsFiles() {
    var disk = BuildAdf(
      ("file1.txt", "Hello"u8.ToArray()),
      ("file2.bin", new byte[] { 1, 2, 3 }));

    using var ms = new MemoryStream(disk);
    var reader = new AdfReader(ms);

    Assert.That(reader.Entries, Has.Count.EqualTo(2));
    var names = reader.Entries.Select(e => e.Name).OrderBy(n => n).ToArray();
    Assert.That(names, Does.Contain("file1.txt"));
    Assert.That(names, Does.Contain("file2.bin"));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Reader_ExtractsOfsFileData() {
    var content = "Hello, Amiga!"u8.ToArray();
    var disk = BuildAdf(("test.txt", content));

    using var ms = new MemoryStream(disk);
    var reader = new AdfReader(ms);

    var entry = reader.Entries.First(e => e.Name == "test.txt");
    var extracted = reader.Extract(entry);
    Assert.That(extracted, Is.EqualTo(content));
  }

  [Category("HappyPath")]
  [Test]
  public void Reader_FileSizes_AreCorrect() {
    var content = new byte[200];
    new Random(42).NextBytes(content);
    var disk = BuildAdf(("data.bin", content));

    using var ms = new MemoryStream(disk);
    var reader = new AdfReader(ms);

    Assert.That(reader.Entries[0].Size, Is.EqualTo(200));
  }

  // ── Descriptor ────────────────────────────────────────────────────────

  [Category("HappyPath")]
  [Test]
  public void Descriptor_Properties() {
    var desc = new AdfFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("Adf"));
    Assert.That(desc.DefaultExtension, Is.EqualTo(".adf"));
  }

  [Category("HappyPath")]
  [Test]
  public void Descriptor_List_ReturnsEntries() {
    var disk = BuildAdf(("a.txt", [1, 2]), ("b.bin", [3, 4, 5]));
    using var ms = new MemoryStream(disk);
    var desc = new AdfFormatDescriptor();
    var entries = desc.List(ms, null);

    Assert.That(entries, Has.Count.EqualTo(2));
  }

  [Category("HappyPath")]
  [Test]
  public void Descriptor_Extract_WritesFiles() {
    var content = "ADF extract test"u8.ToArray();
    var disk = BuildAdf(("test.txt", content));

    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(disk);
      var desc = new AdfFormatDescriptor();
      desc.Extract(ms, tmp, null, null);

      var extracted = File.ReadAllBytes(Path.Combine(tmp, "test.txt"));
      Assert.That(extracted, Is.EqualTo(content));
    } finally {
      Directory.Delete(tmp, true);
    }
  }
}
