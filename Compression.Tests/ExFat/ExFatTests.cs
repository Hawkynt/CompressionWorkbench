namespace Compression.Tests.ExFat;

[TestFixture]
public class ExFatTests {

  [Test, Category("HappyPath")]
  public void RoundTrip_SingleFile() {
    var content = "Hello exFAT!"u8.ToArray();
    var w = new FileSystem.ExFat.ExFatWriter();
    w.AddFile("TEST.TXT", content);
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.ExFat.ExFatReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("TEST.TXT"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(content.Length));

    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(content));
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_MultipleFiles() {
    var data1 = "First file content"u8.ToArray();
    var data2 = "Second file"u8.ToArray();
    var data3 = new byte[100];
    new Random(42).NextBytes(data3);

    var w = new FileSystem.ExFat.ExFatWriter();
    w.AddFile("FILE1.TXT", data1);
    w.AddFile("FILE2.TXT", data2);
    w.AddFile("DATA.BIN", data3);
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.ExFat.ExFatReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(3));

    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data1));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(data2));
    Assert.That(r.Extract(r.Entries[2]), Is.EqualTo(data3));
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_LargeFile() {
    var data = new byte[16384]; // 4 clusters worth
    new Random(123).NextBytes(data);

    var w = new FileSystem.ExFat.ExFatWriter();
    w.AddFile("LARGE.BIN", data);
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.ExFat.ExFatReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Size, Is.EqualTo(data.Length));

    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new FileSystem.ExFat.ExFatFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("ExFat"));
    Assert.That(desc.DisplayName, Is.EqualTo("exFAT"));
    Assert.That(desc.DefaultExtension, Is.EqualTo(".img"));
    Assert.That(desc.Extensions, Does.Contain(".img"));
    Assert.That(desc.Extensions, Does.Contain(".exfat"));
    Assert.That(desc.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
    Assert.That(desc.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Archive));
    Assert.That(desc.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(desc.MagicSignatures[0].Offset, Is.EqualTo(3));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_ViaInterface() {
    var w = new FileSystem.ExFat.ExFatWriter();
    w.AddFile("TEST.DAT", new byte[50]);
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var desc = new FileSystem.ExFat.ExFatFormatDescriptor();
    var entries = desc.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("TEST.DAT"));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Create_ViaInterface() {
    var tmpFile = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmpFile, new byte[10]);
      var desc = new FileSystem.ExFat.ExFatFormatDescriptor();
      using var ms = new MemoryStream();
      desc.Create(ms, [new Compression.Registry.ArchiveInputInfo(tmpFile, "TEST.TXT", false)], new Compression.Registry.FormatCreateOptions());
      ms.Position = 0;
      var entries = desc.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(1));
    } finally { File.Delete(tmpFile); }
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[100]);
    Assert.Throws<InvalidDataException>(() => _ = new FileSystem.ExFat.ExFatReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_BadMagic_Throws() {
    var data = new byte[1024];
    data[0] = 0xEB; data[1] = 0x76; data[2] = 0x90;
    // Write something other than "EXFAT   " at offset 3
    System.Text.Encoding.ASCII.GetBytes("NOTEXFAT").CopyTo(data, 3);
    data[510] = 0x55; data[511] = 0xAA;
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new FileSystem.ExFat.ExFatReader(ms));
  }

  [Test, Category("EdgeCase")]
  public void EmptyDisk_NoEntries() {
    var w = new FileSystem.ExFat.ExFatWriter();
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.ExFat.ExFatReader(ms);
    // Should have no file entries (only system entries like bitmap/upcase)
    Assert.That(r.Entries, Has.Count.EqualTo(0));
  }

  // Windows silently skips files whose SetChecksum is wrong; verify we compute it.
  [Test, Category("RealWorld")]
  public void FileEntrySet_HasValidSetChecksum() {
    var w = new FileSystem.ExFat.ExFatWriter();
    w.AddFile("MYFILE.TXT", new byte[10]);
    var disk = w.Build();

    // With default layout: 512 B/sector, 8 sectors/cluster, FAT at sector 24, FAT
    // length ≈ 17 sectors → cluster heap starts ~sector 41; cluster 2 (root) begins
    // there. The first three 32-byte entries are VolumeLabel (0x83) + Bitmap (0x81)
    // + UpCase (0x82). The File entry set starts at offset +96.
    var clusterHeapOffsetSectors = System.Buffers.Binary.BinaryPrimitives
      .ReadUInt32LittleEndian(disk.AsSpan(88));
    var rootDirOffset = (int)clusterHeapOffsetSectors * 512;
    var fileEntryOffset = rootDirOffset + 3 * 32;

    // First byte must be 0x85 (File entry).
    Assert.That(disk[fileEntryOffset], Is.EqualTo((byte)0x85));
    var secondaryCount = disk[fileEntryOffset + 1];
    var setBytes = 32 * (1 + secondaryCount);

    // Recompute the checksum the same way Windows does; it must match what we wrote.
    ushort expected = 0;
    for (var i = 0; i < setBytes; ++i) {
      if (i == 2 || i == 3) continue;
      expected = (ushort)((((expected & 1) != 0 ? 0x8000 : 0) + (expected >> 1)
        + disk[fileEntryOffset + i]) & 0xFFFF);
    }
    var written = System.Buffers.Binary.BinaryPrimitives
      .ReadUInt16LittleEndian(disk.AsSpan(fileEntryOffset + 2));
    Assert.That(written, Is.EqualTo(expected));
  }

  [Test, Category("RealWorld")]
  public void Vbr_AdvertisesFileSystemRevisionAndVolumeSerial() {
    var w = new FileSystem.ExFat.ExFatWriter();
    var disk = w.Build();

    var volSerial = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(100));
    var fsRev = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(disk.AsSpan(104));
    Assert.That(fsRev, Is.EqualTo(0x0100), "FileSystemRevision must be 1.0 — Windows rejects unknown revs.");
    Assert.That(volSerial, Is.Not.Zero, "VolumeSerialNumber must be populated for enumeration.");
  }

  [Test, Category("RealWorld")]
  public void UpCaseTable_HasValidChecksum() {
    var w = new FileSystem.ExFat.ExFatWriter();
    var disk = w.Build();

    var clusterHeapOffsetSectors = System.Buffers.Binary.BinaryPrimitives
      .ReadUInt32LittleEndian(disk.AsSpan(88));
    var rootDirOffset = (int)clusterHeapOffsetSectors * 512;
    // Entry order: Volume(0x83), Bitmap(0x81), UpCase(0x82).
    var upcaseEntryOffset = rootDirOffset + 2 * 32;
    Assert.That(disk[upcaseEntryOffset], Is.EqualTo((byte)0x82));

    var storedChecksum = System.Buffers.Binary.BinaryPrimitives
      .ReadUInt32LittleEndian(disk.AsSpan(upcaseEntryOffset + 4));
    var upcaseCluster = System.Buffers.Binary.BinaryPrimitives
      .ReadUInt32LittleEndian(disk.AsSpan(upcaseEntryOffset + 20));
    var upcaseLen = (int)System.Buffers.Binary.BinaryPrimitives
      .ReadInt64LittleEndian(disk.AsSpan(upcaseEntryOffset + 24));
    var upcaseOffset = rootDirOffset + (int)(upcaseCluster - 2) * 4096;

    uint expected = 0;
    for (var i = 0; i < upcaseLen; ++i)
      expected = ((expected & 1) != 0 ? 0x80000000u : 0) + (expected >> 1) + disk[upcaseOffset + i];
    Assert.That(storedChecksum, Is.EqualTo(expected));
  }
}
