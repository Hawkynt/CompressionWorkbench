namespace Compression.Tests.Fat;

[TestFixture]
public class FatWriterTests {

  [Test, Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var data = "Hello FAT writer!"u8.ToArray();
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("TEST.TXT", data);
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Fat.FatReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("TEST.TXT"));
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_MultipleFiles() {
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("A.TXT", "First"u8.ToArray());
    w.AddFile("B.TXT", "Second"u8.ToArray());
    w.AddFile("C.BIN", new byte[200]);
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Fat.FatReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(3));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo("First"u8.ToArray()));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo("Second"u8.ToArray()));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_LargeFile() {
    var data = new byte[10000];
    new Random(42).NextBytes(data);
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("BIG.DAT", data);
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Fat.FatReader(ms);
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void FAT12_DefaultType() {
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("TEST.TXT", new byte[10]);
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Fat.FatReader(ms);
    Assert.That(r.FatType, Is.EqualTo(12));
  }

  [Test, Category("RoundTrip")]
  public void FAT32_RoundTrip_SmallImage() {
    // ~75 MB → forces cluster count over 65525, triggering FAT32.
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("HELLO.TXT", "hello fat32"u8.ToArray());
    var payload = new byte[4096];
    new Random(7).NextBytes(payload);
    w.AddFile("RAND.BIN", payload);
    var totalSectors = 200_000;
    var disk = w.Build(totalSectors);

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Fat.FatReader(ms);
    Assert.That(r.FatType, Is.EqualTo(32), "image should land in FAT32 range");
    Assert.That(r.Entries, Has.Count.EqualTo(2));
    var nameSet = r.Entries.Select(e => e.Name).ToHashSet();
    Assert.That(nameSet.Contains("HELLO.TXT"), Is.True);
    Assert.That(nameSet.Contains("RAND.BIN"), Is.True);
    var hello = r.Entries.First(e => e.Name == "HELLO.TXT");
    var rand = r.Entries.First(e => e.Name == "RAND.BIN");
    Assert.That(r.Extract(hello), Is.EqualTo("hello fat32"u8.ToArray()));
    Assert.That(r.Extract(rand), Is.EqualTo(payload));
  }

  [Test, Category("Spec")]
  public void FAT32_HasFsInfoSectorAndBackupBoot() {
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("A.TXT", "x"u8.ToArray());
    var disk = w.Build(200_000);

    // FSInfo at sector 1.
    var fsInfo = disk.AsSpan(512);
    var leadSig = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(fsInfo);
    var strucSig = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(fsInfo[484..]);
    var trailSig = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(fsInfo[508..]);
    Assert.That(leadSig, Is.EqualTo(0x41615252u), "FSI_LeadSig");
    Assert.That(strucSig, Is.EqualTo(0x61417272u), "FSI_StrucSig");
    Assert.That(trailSig, Is.EqualTo(0xAA550000u), "FSI_TrailSig");

    // Backup boot sector at sector 6 must duplicate the primary boot sector.
    var primary = disk.AsSpan(0, 512);
    var backup = disk.AsSpan(6 * 512, 512);
    Assert.That(backup.SequenceEqual(primary), Is.True, "backup boot sector must mirror primary");

    // BPB_RootClus at offset 44 must be 2.
    Assert.That(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(44)), Is.EqualTo(2u));
    // BPB_FSInfo at offset 48 must be 1.
    Assert.That(System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(disk.AsSpan(48)), Is.EqualTo((ushort)1));
    // Filesystem type string "FAT32   " at offset 82.
    Assert.That(System.Text.Encoding.ASCII.GetString(disk.AsSpan(82, 8)), Is.EqualTo("FAT32   "));
  }

  [Test, Category("RoundTrip")]
  public void EmptyDisk() {
    var w = new FileSystem.Fat.FatWriter();
    var disk = w.Build();
    Assert.That(disk.Length, Is.EqualTo(2880 * 512));

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Fat.FatReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(0));
  }

  [Test, Category("RoundTrip")]
  public void LFN_LongName_RoundtripsViaReader() {
    // Multi-fragment long names exercise the LFN ord-reversal, the
    // 5/6/2 split, and the trailing-NUL-then-FFFF padding rule.
    var w = new FileSystem.Fat.FatWriter();
    var longName = "Hello World With Long Name.TXT";
    w.AddFile(longName, "lfn"u8.ToArray());
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Fat.FatReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo(longName), "Reader should reconstruct long name");
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo("lfn"u8.ToArray()));
  }

  [Test, Category("RoundTrip")]
  public void LFN_MixedCase_PreservesCase() {
    // Mixed-case filename triggers LFN even though it'd fit in 8.3 chars,
    // because pure 8.3 entries can't carry lowercase.
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("ReadMe.md", "x"u8.ToArray());
    var disk = w.Build();
    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Fat.FatReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("ReadMe.md"));
  }

  [Test, Category("Spec")]
  public void LFN_ChecksumMatchesShortNameInDirent() {
    // The FATGEN103 checksum is sum_i RotR(prev) + short[i] over the 11
    // raw bytes of the 8.3 entry. Both the LFN slot and the 8.3 entry must
    // store the same value or fsck.fat reports a CHAIN error.
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("MixedCase.txt", new byte[5]);
    var disk = w.Build();

    // First user dirent on a FAT12 floppy starts at offset
    // (1 + 2*9) * 512 = 9728. Walk LFN slots (attr=0x0F) until we hit
    // the 8.3 entry — checksum is replicated in every LFN slot.
    const int rootStart = 9728;
    var off = rootStart;
    var lfnChecksum = disk[off + 13];
    Assert.That(disk[off + 11], Is.EqualTo(0x0F), "First slot must be LFN (attribute 0x0F)");
    while (disk[off + 11] == 0x0F) off += 32;
    var shortStart = off;

    byte recomputed = 0;
    for (var i = 0; i < 11; i++)
      recomputed = (byte)((((recomputed & 1) != 0 ? 0x80 : 0) + (recomputed >> 1) + disk[shortStart + i]) & 0xFF);
    Assert.That(lfnChecksum, Is.EqualTo(recomputed),
      "LFN slot checksum must equal RotR-add over the 11 raw bytes of the 8.3 entry");
  }

  [Test, Category("RoundTrip")]
  public void LFN_DoesNotEmitForPlain83Names() {
    // Plain 8.3 names should NOT emit any LFN slot — DOS readers must see
    // the file at the very first dirent. Verify by counting attribute=0x0F
    // entries: there should be zero in the root dir.
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("HELLO.TXT", "x"u8.ToArray());
    var disk = w.Build();

    const int rootStart = 9728;
    var lfnSlots = 0;
    for (var off = rootStart; off < rootStart + 32 * 16; off += 32) {
      if (disk[off] == 0x00) break;
      if (disk[off + 11] == 0x0F) lfnSlots++;
    }
    Assert.That(lfnSlots, Is.EqualTo(0), "Plain 8.3 names must not emit LFN slots");
  }

  [Test, Category("RoundTrip")]
  public void LFN_AndPlain83_Coexist() {
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("PLAIN.TXT", "p"u8.ToArray());
    w.AddFile("Mixed Case Filename.dat", "m"u8.ToArray());
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Fat.FatReader(ms);
    var names = r.Entries.Select(e => e.Name).ToHashSet();
    Assert.That(names.Contains("PLAIN.TXT"), Is.True);
    Assert.That(names.Contains("Mixed Case Filename.dat"), Is.True);
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_Create_ViaInterface() {
    var tmpFile = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmpFile, new byte[10]);
      var desc = new FileSystem.Fat.FatFormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveCreatable)desc).Create(ms, [new Compression.Registry.ArchiveInputInfo(tmpFile, "TEST.TXT", false)], new Compression.Registry.FormatCreateOptions());
      ms.Position = 0;
      var entries = desc.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(1));
    } finally {
      File.Delete(tmpFile);
    }
  }
}
