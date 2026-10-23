#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;

namespace Compression.Tests.DiskImage;

/// <summary>
/// Partition schemes beyond the classic 512-byte MBR/GPT: 4Kn GPT, GPT recovery from the backup
/// header, protective/hybrid MBRs, EBR chains, and the checksummed labels of SGI, Sun, BSD and
/// Amiga. The labels here are laid out field by field from the scheme's specification; the
/// external fixture checks the same readers against labels <c>parted</c> and <c>sfdisk</c> write.
/// </summary>
[TestFixture]
public class PartitionSchemeDetectionTests {

  [SetUp]
  public void EnsureRegistered() => Compression.Lib.FormatRegistration.EnsureInitialized();

  /// <summary>A zeroed, writable stream whose buffer the test may patch through GetBuffer.</summary>
  private static MemoryStream Buffered(long length) {
    var bytes = new byte[length];
    return new MemoryStream(bytes, 0, bytes.Length, writable: true, publiclyVisible: true);
  }

  private static MemoryStream GptDisk(int sectorSize, long sectors, params (long Start, long Length)[] parts) {
    var ms = Buffered(sectors * sectorSize);
    var editor = PartitionEditor.CreateEmpty(ms, PartitionScheme.Gpt, sectorSize);
    foreach (var (start, length) in parts)
      editor.AddPartition(start * sectorSize, length * sectorSize, PartitionType.Linux, "p");
    return ms;
  }

  // ── GPT ────────────────────────────────────────────────────────────

  [TestCase(512)]
  [TestCase(4096)]
  [Category("HappyPath")]
  public void Given_GptAtBlockSize_When_Detected_Then_SectorSizeAndOffsetsFollowIt(int sectorSize) {
    using var disk = GptDisk(sectorSize, 4096, (256, 512));
    var head = disk.ToArray().AsSpan(0, GptParser.ProbeBytes);

    Assert.That(GptParser.DetectSectorSize(head), Is.EqualTo(sectorSize));
    var detection = PartitionTableDetector.Detect(disk);
    Assert.That(detection.Scheme, Is.EqualTo("GPT"));
    Assert.That(detection.SectorSize, Is.EqualTo(sectorSize));
    Assert.That(detection.Partitions.Single().StartOffset, Is.EqualTo(256L * sectorSize));
    Assert.That(detection.Partitions.Single().Size, Is.EqualTo(512L * sectorSize));
  }

  [Test, Category("HappyPath")]
  public void Given_4KnGpt_When_Written_Then_EntryArrayTakesFourBlocksAndUsableRangeFollows() {
    using var disk = GptDisk(4096, 1024);
    var info = GptParser.ReadHeader(disk)!;
    Assert.That(info.SectorSize, Is.EqualTo(4096));
    Assert.That(info.FirstUsableLba, Is.EqualTo(6UL), "LBA0 MBR + LBA1 header + 4 blocks of 128x128-byte entries");
    Assert.That(info.LastUsableLba, Is.EqualTo(1024UL - 6), "backup array (4) + backup header (1) at the end");
    Assert.That(info.AlternateLba, Is.EqualTo(1023UL));
  }

  [Test, Category("Boundary")]
  public void Given_GptPartitionEndingAtLastUsableLba_When_Added_Then_AcceptedButOneMoreSectorRefused() {
    using var disk = GptDisk(512, 2048);
    var editor = new PartitionEditor(disk);
    var lastUsable = 2048 - 34; // 32 backup-array sectors + backup header, then the LBA before
    Assert.That(() => editor.AddPartition(1024L * 512, (lastUsable - 1024 + 2) * 512L, PartitionType.Linux, null),
      Throws.InvalidOperationException, "would run into the backup entry array");
    editor.AddPartition(1024L * 512, (lastUsable - 1024 + 1) * 512L, PartitionType.Linux, null);
    Assert.That(editor.Verify().IsValid, Is.True, string.Join("; ", editor.Verify().Issues));
    Assert.That(() => editor.AddPartition(33L * 512, 8 * 512, PartitionType.Linux, null),
      Throws.InvalidOperationException, "LBA 33 is the primary entry array");
  }

  [Test, Category("Boundary")]
  public void Given_GptWith128Entries_When_129thAdded_Then_Refused() {
    using var disk = GptDisk(512, 34 + 128 + 34 + 8);
    var editor = new PartitionEditor(disk);
    for (var i = 0; i < 128; ++i)
      editor.AddPartition((34L + i) * 512, 512, PartitionType.Linux, null);
    Assert.That(PartitionTableDetector.Detect(disk).Partitions, Has.Count.EqualTo(128));
    Assert.That(() => editor.AddPartition((34L + 128) * 512, 512, PartitionType.Linux, null), Throws.InvalidOperationException);
  }

  [Test, Category("ErrorHandling")]
  public void Given_PrimaryHeaderCrcBroken_When_Parsed_Then_TheBackupHeaderIsUsed() {
    using var disk = GptDisk(512, 4096, (2048, 1024));
    disk.Position = 512 + 40; // FirstUsableLBA inside the primary header: breaks its CRC
    disk.WriteByte(0x77);

    var detection = PartitionTableDetector.Detect(disk);
    Assert.That(detection.Scheme, Is.EqualTo("GPT"));
    Assert.That(detection.Partitions.Single().StartOffset, Is.EqualTo(2048L * 512));
    Assert.That(GptParser.ReadHeader(disk)!.FromBackup, Is.True);
  }

  [Test, Category("ErrorHandling")]
  public void Given_PrimaryEntryArrayCorrupted_When_Parsed_Then_TheBackupArrayIsUsed() {
    using var disk = GptDisk(512, 4096, (2048, 1024));
    disk.Position = 2 * 512 + 32; // first entry's FirstLBA
    disk.Write(new byte[8]);

    var part = PartitionTableDetector.Detect(disk).Partitions.Single();
    Assert.That(part.StartOffset, Is.EqualTo(2048L * 512));
  }

  [Test, Category("ErrorHandling")]
  public void Given_BothGptHeadersBroken_When_Detected_Then_NoTableAndNoProtectiveEntryListed() {
    using var disk = GptDisk(512, 4096, (2048, 1024));
    disk.Position = 512 + 40; disk.WriteByte(0x77);
    disk.Position = 4095L * 512 + 40; disk.WriteByte(0x77);
    disk.Position = 512; disk.Write(new byte[8]); // and no signature for the lenient reading either

    var detection = PartitionTableDetector.Detect(disk);
    Assert.That(detection.Partitions, Is.Empty, "a protective 0xEE entry describes the GPT, not data");
  }

  [Test, Category("EdgeCase")]
  public void Given_HybridMbr_When_Detected_Then_GptWins() {
    using var disk = GptDisk(512, 4096, (2048, 1024));
    // Hybrid: a real MBR entry mirroring the GPT partition beside the 0xEE entry.
    var entry = disk.GetBuffer().AsSpan(0x1BE + 16, 16);
    entry[4] = 0x83;
    BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], 2048);
    BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], 1024);

    var detection = PartitionTableDetector.Detect(disk);
    Assert.That(detection.Scheme, Is.EqualTo("GPT"));
    Assert.That(detection.Partitions.Single().Source, Is.EqualTo("GPT"));
  }

  [Test, Category("EdgeCase")]
  public void Given_GptEntryWithInvertedRange_When_Parsed_Then_Skipped() {
    using var disk = GptDisk(512, 4096, (2048, 1024), (3072, 16));
    // Invert the second entry and recompute both CRCs through a re-read of the editor's own layout.
    var bytes = disk.GetBuffer();
    var slot = bytes.AsSpan(2 * 512 + 128, 128);
    BinaryPrimitives.WriteUInt64LittleEndian(slot[40..], 100); // LastLBA < FirstLBA
    FixGptCrcs(bytes, 512, 4096);

    Assert.That(PartitionTableDetector.Detect(disk).Partitions, Has.Count.EqualTo(1));
  }

  private static void FixGptCrcs(byte[] disk, int sectorSize, long sectors) {
    foreach (var headerLba in new[] { 1L, sectors - 1 }) {
      var header = disk.AsSpan((int)(headerLba * sectorSize), 92);
      var entriesLba = BinaryPrimitives.ReadUInt64LittleEndian(header[72..]);
      var count = BinaryPrimitives.ReadUInt32LittleEndian(header[80..]);
      var size = BinaryPrimitives.ReadUInt32LittleEndian(header[84..]);
      if (headerLba != 1) // keep the backup array identical to the primary
        disk.AsSpan(2 * sectorSize, (int)(count * size)).CopyTo(disk.AsSpan((int)((long)entriesLba * sectorSize)));
      var crc = Compression.Core.Checksums.Crc32.Compute(disk.AsSpan((int)((long)entriesLba * sectorSize), (int)(count * size)));
      BinaryPrimitives.WriteUInt32LittleEndian(header[88..], crc);
      header.Slice(16, 4).Clear();
      BinaryPrimitives.WriteUInt32LittleEndian(header[16..], Compression.Core.Checksums.Crc32.Compute(header));
    }
  }

  // ── MBR / EBR ──────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Given_ExtendedPartitionWithTwoLogicals_When_Detected_Then_BothLogicalsListedAtTheirAbsoluteOffsets() {
    using var disk = Buffered(8192 * 512);
    var editor = PartitionEditor.CreateEmpty(disk, PartitionScheme.Mbr);
    editor.AddPartition(2048L * 512, 6000L * 512, PartitionType.ExtendedLba, null);
    editor.AddLogicalPartition(2049L * 512, 1000L * 512, PartitionType.Linux, null);
    editor.AddLogicalPartition(4000L * 512, 1000L * 512, PartitionType.Fat32Lba, null);

    var parts = PartitionTableDetector.Detect(disk).Partitions;
    Assert.That(parts.Select(p => (p.Source, p.StartOffset)),
      Is.EqualTo(new[] { ("EBR", 2049L * 512), ("EBR", 4000L * 512) }));
  }

  [Test, Category("ErrorHandling")]
  public void Given_EbrChainThatLoops_When_Detected_Then_TheWalkStops() {
    using var disk = Buffered(8192 * 512);
    var editor = PartitionEditor.CreateEmpty(disk, PartitionScheme.Mbr);
    editor.AddPartition(2048L * 512, 6000L * 512, PartitionType.ExtendedLba, null);
    editor.AddLogicalPartition(2049L * 512, 1000L * 512, PartitionType.Linux, null);
    // Point the first EBR's link back at itself.
    var link = disk.GetBuffer().AsSpan(2048 * 512 + 0x1BE + 16, 16);
    link[4] = 0x05;
    BinaryPrimitives.WriteUInt32LittleEndian(link[8..], 0);
    BinaryPrimitives.WriteUInt32LittleEndian(link[12..], 1);

    Assert.That(PartitionTableDetector.Detect(disk).Partitions, Has.Count.EqualTo(1));
  }

  // ── SGI / Sun / BSD / Amiga ────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Given_SgiVolumeHeader_When_Detected_Then_PartitionsListedWithoutTheWholeVolume() {
    var disk = new byte[8 * 1024 * 1024];
    var s = disk.AsSpan(0, 512);
    BinaryPrimitives.WriteUInt32BigEndian(s, 0x0BE5A941);
    void Part(int slot, uint blocks, uint first, uint type) {
      var r = disk.AsSpan(312 + slot * 12);
      BinaryPrimitives.WriteUInt32BigEndian(r, blocks);
      BinaryPrimitives.WriteUInt32BigEndian(r[4..], first);
      BinaryPrimitives.WriteUInt32BigEndian(r[8..], type);
    }
    Part(0, 4096, 2048, 0x83);
    Part(8, 2048, 0, 0);          // volume header
    Part(10, 16384, 0, 6);        // whole volume
    uint sum = 0;
    for (var i = 0; i < 512; i += 4) sum += BinaryPrimitives.ReadUInt32BigEndian(s[i..]);
    BinaryPrimitives.WriteUInt32BigEndian(s[504..], unchecked(0u - sum));

    var detection = PartitionTableDetector.Detect(disk);
    Assert.That(detection.Scheme, Is.EqualTo("SGI"));
    Assert.That(detection.Partitions.Select(p => (p.StartOffset, p.Size)),
      Is.EqualTo(new[] { (2048L * 512, 4096L * 512), (0L, 2048L * 512) }));
    using var ms = new MemoryStream(disk);
    Assert.That(PartitionTableDetector.HasSelfValidatingLabel(ms), Is.True);

    disk[400] ^= 1; // checksum now wrong
    Assert.That(PartitionTableDetector.Detect(disk).Scheme, Is.EqualTo("None"));
  }

  [Test, Category("HappyPath")]
  public void Given_SunLabel_When_Detected_Then_SlicesPlacedByCylinderGeometry() {
    var disk = new byte[16 * 1024 * 1024];
    var s = disk.AsSpan(0, 512);
    BinaryPrimitives.WriteUInt32BigEndian(s[128..], 1);            // vtoc version
    BinaryPrimitives.WriteUInt16BigEndian(s[140..], 8);            // nparts
    BinaryPrimitives.WriteUInt16BigEndian(s[142..], 0x83); // slice a: Linux native
    BinaryPrimitives.WriteUInt16BigEndian(s[150..], 5);    // slice c: whole disk
    BinaryPrimitives.WriteUInt32BigEndian(s[188..], 0x600DDEEE);   // vtoc sanity
    BinaryPrimitives.WriteUInt16BigEndian(s[436..], 16);           // ntrks
    BinaryPrimitives.WriteUInt16BigEndian(s[438..], 63);           // nsect
    BinaryPrimitives.WriteUInt32BigEndian(s[444..], 2);    // a: start cylinder 2
    BinaryPrimitives.WriteUInt32BigEndian(s[448..], 1008 * 5);
    BinaryPrimitives.WriteUInt32BigEndian(s[464..], 1008 * 32);
    BinaryPrimitives.WriteUInt16BigEndian(s[508..], 0xDABE);
    ushort xor = 0;
    for (var i = 0; i < 510; i += 2) xor ^= BinaryPrimitives.ReadUInt16BigEndian(s[i..]);
    BinaryPrimitives.WriteUInt16BigEndian(s[510..], xor);

    var detection = PartitionTableDetector.Detect(disk);
    Assert.That(detection.Scheme, Is.EqualTo("Sun"));
    var part = detection.Partitions.Single();
    Assert.That(part.StartOffset, Is.EqualTo(2L * 16 * 63 * 512));
    Assert.That(part.Size, Is.EqualTo(1008L * 5 * 512));
  }

  [Test, Category("HappyPath")]
  public void Given_StandaloneBsdDisklabel_When_Detected_Then_SlicesListedFromTheWholeDisk() {
    var disk = new byte[8 * 1024 * 1024];
    var l = disk.AsSpan(512, 148 + 8 * 16);
    BinaryPrimitives.WriteUInt32LittleEndian(l, 0x82564557);
    BinaryPrimitives.WriteUInt32LittleEndian(l[40..], 512);
    BinaryPrimitives.WriteUInt32LittleEndian(l[132..], 0x82564557);
    BinaryPrimitives.WriteUInt16LittleEndian(l[138..], 8);
    void Part(int slot, uint size, uint offset, byte fstype) {
      var r = disk.AsSpan(512 + 148 + slot * 16);
      BinaryPrimitives.WriteUInt32LittleEndian(r, size);
      BinaryPrimitives.WriteUInt32LittleEndian(r[4..], offset);
      r[12] = fstype;
    }
    Part(0, 4096, 64, 7);     // a: 4.2BSD
    Part(1, 2048, 4160, 1);   // b: swap
    Part(2, 16384, 0, 0);     // c: whole disk
    ushort xor = 0;
    for (var i = 0; i < l.Length; i += 2) xor ^= BinaryPrimitives.ReadUInt16LittleEndian(l[i..]);
    BinaryPrimitives.WriteUInt16LittleEndian(l[136..], xor);

    var detection = PartitionTableDetector.Detect(disk);
    Assert.That(detection.Scheme, Is.EqualTo("BSD"));
    Assert.That(detection.Partitions.Select(p => (p.Name, p.StartOffset)),
      Is.EqualTo(new[] { ("a", 64L * 512), ("b", 4160L * 512) }));

    disk[512 + 132] ^= 1;
    Assert.That(PartitionTableDetector.Detect(disk).Scheme, Is.EqualTo("None"), "without d_magic2 the label proves nothing");
  }

  [Test, Category("HappyPath")]
  public void Given_AmigaRigidDiskBlock_When_Detected_Then_PartitionsPlacedByDosEnvecGeometry() {
    var disk = new byte[16 * 1024 * 1024];
    void Checksum(Span<byte> block) {
      var longs = BinaryPrimitives.ReadUInt32BigEndian(block[4..]);
      block.Slice(8, 4).Clear();
      uint sum = 0;
      for (var i = 0; i < longs; ++i) sum += BinaryPrimitives.ReadUInt32BigEndian(block[(i * 4)..]);
      BinaryPrimitives.WriteUInt32BigEndian(block[8..], unchecked(0u - sum));
    }
    var rdb = disk.AsSpan(1 * 512, 512); // RDSK in block 1
    BinaryPrimitives.WriteUInt32BigEndian(rdb, 0x5244534B);
    BinaryPrimitives.WriteUInt32BigEndian(rdb[4..], 64);
    BinaryPrimitives.WriteUInt32BigEndian(rdb[16..], 512);
    BinaryPrimitives.WriteUInt32BigEndian(rdb[24..], 0xFFFFFFFF);
    BinaryPrimitives.WriteUInt32BigEndian(rdb[28..], 3);          // partition list → block 3
    BinaryPrimitives.WriteUInt32BigEndian(rdb[32..], 0xFFFFFFFF);
    Checksum(rdb);

    var part = disk.AsSpan(3 * 512, 512);
    BinaryPrimitives.WriteUInt32BigEndian(part, 0x50415254);
    BinaryPrimitives.WriteUInt32BigEndian(part[4..], 64);
    BinaryPrimitives.WriteUInt32BigEndian(part[16..], 0xFFFFFFFF); // no next
    part[36] = 3; "DH0"u8.CopyTo(part[37..]);
    void Env(int i, uint v) => BinaryPrimitives.WriteUInt32BigEndian(disk.AsSpan(3 * 512 + 128 + i * 4), v);
    Env(0, 16); Env(1, 128); Env(3, 2); Env(5, 32); Env(9, 4); Env(10, 99); Env(16, 0x444F5301);
    Checksum(part);

    var detection = PartitionTableDetector.Detect(disk);
    Assert.That(detection.Scheme, Is.EqualTo("RDB"));
    var p = detection.Partitions.Single();
    Assert.That(p.Name, Is.EqualTo("DH0"));
    Assert.That(p.TypeName, Is.EqualTo("DOS1"));
    Assert.That(p.StartOffset, Is.EqualTo(4L * 2 * 32 * 512));
    Assert.That(p.Size, Is.EqualTo(96L * 2 * 32 * 512));
  }

  [Test, Category("EdgeCase")]
  public void Given_ZeroedDisk_When_Detected_Then_NoSchemeClaimsIt() {
    Assert.That(PartitionTableDetector.Detect(new byte[1024 * 1024]).Scheme, Is.EqualTo("None"));
    using var ms = new MemoryStream(new byte[1024 * 1024]);
    Assert.That(PartitionTableDetector.HasSelfValidatingLabel(ms), Is.False);
  }
}
