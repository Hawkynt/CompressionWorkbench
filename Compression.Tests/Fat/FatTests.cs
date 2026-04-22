using System.Buffers.Binary;
using System.Text;

namespace Compression.Tests.Fat;

[TestFixture]
public class FatTests {

  /// <summary>
  /// Builds a minimal FAT12 floppy disk image (1.44MB) with the given files.
  /// </summary>
  private static byte[] BuildFat12(params (string Name, byte[] Data)[] files) {
    const int bytesPerSector = 512;
    const int sectorsPerCluster = 1;
    const int reservedSectors = 1;
    const int fatCount = 2;
    const int rootEntryCount = 224;
    const int totalSectors = 2880; // 1.44MB
    const int fatSize = 9; // sectors per FAT for FAT12

    var disk = new byte[totalSectors * bytesPerSector];

    // Boot sector
    disk[0] = 0xEB; disk[1] = 0x3C; disk[2] = 0x90; // jump
    Encoding.ASCII.GetBytes("MSDOS5.0").CopyTo(disk, 3); // OEM name
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(11), bytesPerSector);
    disk[13] = sectorsPerCluster;
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(14), reservedSectors);
    disk[16] = fatCount;
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(17), rootEntryCount);
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(19), totalSectors);
    disk[21] = 0xF0; // media type (floppy)
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(22), fatSize);
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(24), 18); // sectors per track
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(26), 2); // heads
    disk[510] = 0x55; disk[511] = 0xAA; // boot signature

    // FAT starts at sector 1
    var fatOffset = reservedSectors * bytesPerSector;
    // FAT12 media byte + 0xFFF
    disk[fatOffset] = 0xF0;
    disk[fatOffset + 1] = 0xFF;
    disk[fatOffset + 2] = 0xFF;

    // Root directory starts after both FATs
    var rootDirOffset = (reservedSectors + fatCount * fatSize) * bytesPerSector;
    // Data area starts after root directory
    var rootDirSectors = (rootEntryCount * 32 + bytesPerSector - 1) / bytesPerSector;
    var dataOffset = rootDirOffset + rootDirSectors * bytesPerSector;

    var nextCluster = 2; // first data cluster
    var dirEntryPos = rootDirOffset;

    foreach (var (name, data) in files) {
      // Write directory entry (short name only)
      var dotIdx = name.LastIndexOf('.');
      var baseName = dotIdx >= 0 ? name[..dotIdx] : name;
      var ext = dotIdx >= 0 ? name[(dotIdx + 1)..] : "";
      var shortBase = baseName.ToUpperInvariant().PadRight(8)[..8];
      var shortExt = ext.ToUpperInvariant().PadRight(3)[..3];

      Encoding.ASCII.GetBytes(shortBase).CopyTo(disk, dirEntryPos);
      Encoding.ASCII.GetBytes(shortExt).CopyTo(disk, dirEntryPos + 8);
      disk[dirEntryPos + 11] = 0x20; // Archive attribute
      BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(dirEntryPos + 26), (ushort)nextCluster);
      BinaryPrimitives.WriteInt32LittleEndian(disk.AsSpan(dirEntryPos + 28), data.Length);
      dirEntryPos += 32;

      // Write file data to clusters
      var clustersNeeded = (data.Length + bytesPerSector - 1) / bytesPerSector;
      if (clustersNeeded == 0) clustersNeeded = 1;

      var clusterOffset = dataOffset + (nextCluster - 2) * bytesPerSector;
      data.CopyTo(disk, clusterOffset);

      // Write FAT chain
      for (var c = 0; c < clustersNeeded; c++) {
        var cluster = nextCluster + c;
        var nextVal = (c + 1 < clustersNeeded) ? cluster + 1 : 0xFFF; // end of chain
        WriteFat12Entry(disk, fatOffset, cluster, nextVal);
      }

      nextCluster += clustersNeeded;
    }

    // Copy FAT1 to FAT2
    Array.Copy(disk, fatOffset, disk, fatOffset + fatSize * bytesPerSector, fatSize * bytesPerSector);

    return disk;
  }

  private static void WriteFat12Entry(byte[] disk, int fatOffset, int cluster, int value) {
    var bytePos = fatOffset + cluster * 3 / 2;
    if ((cluster & 1) == 0) {
      disk[bytePos] = (byte)(value & 0xFF);
      disk[bytePos + 1] = (byte)((disk[bytePos + 1] & 0xF0) | ((value >> 8) & 0x0F));
    } else {
      disk[bytePos] = (byte)((disk[bytePos] & 0x0F) | ((value << 4) & 0xF0));
      disk[bytePos + 1] = (byte)((value >> 4) & 0xFF);
    }
  }

  [Test, Category("HappyPath")]
  public void Read_SingleFile() {
    var content = "Hello FAT!"u8.ToArray();
    var disk = BuildFat12(("TEST.TXT", content));
    using var ms = new MemoryStream(disk);

    var r = new FileSystem.Fat.FatReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.FatType, Is.EqualTo(12));
    Assert.That(r.Entries[0].Name, Is.EqualTo("TEST.TXT"));
  }

  [Test, Category("HappyPath")]
  public void Extract_ReturnsCorrectData() {
    var content = "Hello FAT filesystem!"u8.ToArray();
    var disk = BuildFat12(("HELLO.TXT", content));
    using var ms = new MemoryStream(disk);

    var r = new FileSystem.Fat.FatReader(ms);
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(content));
  }

  [Test, Category("HappyPath")]
  public void Read_MultipleFiles() {
    var disk = BuildFat12(
      ("FILE1.TXT", "First"u8.ToArray()),
      ("FILE2.TXT", "Second"u8.ToArray()),
      ("DATA.BIN", new byte[100])
    );
    using var ms = new MemoryStream(disk);

    var r = new FileSystem.Fat.FatReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(3));
  }

  [Test, Category("HappyPath")]
  public void Extract_MultipleFiles_RoundTrip() {
    var data1 = "First file"u8.ToArray();
    var data2 = "Second file"u8.ToArray();
    var disk = BuildFat12(("A.TXT", data1), ("B.TXT", data2));
    using var ms = new MemoryStream(disk);

    var r = new FileSystem.Fat.FatReader(ms);
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data1));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(data2));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new FileSystem.Fat.FatFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("Fat"));
    Assert.That(desc.Extensions, Does.Contain(".img"));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_ViaInterface() {
    var disk = BuildFat12(("TEST.DAT", new byte[50]));
    using var ms = new MemoryStream(disk);
    var desc = new FileSystem.Fat.FatFormatDescriptor();
    var entries = desc.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[100]);
    Assert.Throws<InvalidDataException>(() => _ = new FileSystem.Fat.FatReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_BadBootJump_Throws() {
    var data = new byte[1024];
    data[0] = 0x90; // not a valid boot jump
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new FileSystem.Fat.FatReader(ms));
  }

  [Test, Category("EdgeCase")]
  public void EmptyDisk_NoEntries() {
    var disk = BuildFat12();
    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Fat.FatReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(0));
  }
}
