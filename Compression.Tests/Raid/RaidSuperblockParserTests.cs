using System.Buffers.Binary;
using System.Text;
using Compression.Core.DiskImage.Raid;

namespace Compression.Tests.Raid;

/// <summary>
/// Field-offset proofs for the md 0.90 / 1.x superblock parsers and the IMSM container
/// parser, built from synthetic superblocks laid out per the kernel/mdadm structs. These
/// pin the on-disk offsets independently; the mdadm end-to-end fixture proves the same
/// parsers against superblocks written by the real tool.
/// </summary>
[TestFixture]
public class RaidSuperblockParserTests {

  // ── md 1.x ────────────────────────────────────────────────────────────
  [Test, Category("HappyPath")]
  public void Md1x_V12_ParsesLevelChunkRoleAndGeometry() {
    const long deviceLen = 32L * 1024 * 1024;
    var dev = new byte[deviceLen];
    const int sbOff = 4096; // 1.2

    void U32(int rel, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(dev.AsSpan(sbOff + rel), v);
    void U64(int rel, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(dev.AsSpan(sbOff + rel), v);

    U32(0x00, 0xA92B4EFC);          // magic
    U32(0x04, 1);                   // major_version
    for (var i = 0; i < 16; i++) dev[sbOff + 0x10 + i] = (byte)(0xA0 + i); // set_uuid
    Encoding.ASCII.GetBytes("host:array0").CopyTo(dev, sbOff + 0x20);       // set_name
    U32(0x48, 5);                   // level
    U32(0x4C, 2);                   // layout (left-symmetric)
    U64(0x50, 20480);               // size (component sectors) = 10 MiB
    U32(0x58, 128);                 // chunk sectors = 64 KiB
    U32(0x5C, 3);                   // raid_disks
    U64(0x80, 2048);                // data_offset sectors = 1 MiB
    U64(0x90, sbOff / 512);         // super_offset sectors (self-describing)
    U32(0xA0, 1);                   // dev_number = 1
    U32(0xDC, 3);                   // max_dev
    // dev_roles[dev_number=1] = role 2
    BinaryPrimitives.WriteUInt16LittleEndian(dev.AsSpan(sbOff + 0x100 + 1 * 2), 2);

    using var ms = new MemoryStream(dev, writable: false);
    var meta = Md1SuperblockParser.TryParse(ms);

    Assert.That(meta, Is.Not.Null);
    Assert.That(meta!.Format, Is.EqualTo(RaidMetadataFormat.Mdraid1x));
    Assert.That(meta.Level, Is.EqualTo(RaidLevel.Raid5));
    Assert.That(meta.RaidDisks, Is.EqualTo(3));
    Assert.That(meta.ChunkSizeBytes, Is.EqualTo(64 * 1024));
    Assert.That(meta.Layout, Is.EqualTo(2));
    Assert.That(meta.Role, Is.EqualTo(2));
    Assert.That(meta.DataOffsetBytes, Is.EqualTo(2048L * 512));
    Assert.That(meta.DataSizeBytes, Is.EqualTo(20480L * 512));
    Assert.That(meta.ArrayName, Is.EqualTo("host:array0"));
  }

  [Test, Category("EdgeCase")]
  public void Md1x_WrongSuperOffset_IsRejected() {
    var dev = new byte[8L * 1024 * 1024];
    BinaryPrimitives.WriteUInt32LittleEndian(dev.AsSpan(4096), 0xA92B4EFC);
    BinaryPrimitives.WriteUInt64LittleEndian(dev.AsSpan(4096 + 0x90), 99); // bogus super_offset
    using var ms = new MemoryStream(dev, writable: false);
    Assert.That(Md1SuperblockParser.TryParse(ms), Is.Null);
  }

  // ── md 0.90 ───────────────────────────────────────────────────────────
  [Test, Category("HappyPath")]
  public void Md090_ParsesLevelChunkRoleAndGeometry() {
    const long deviceLen = 16L * 1024 * 1024;
    var dev = new byte[deviceLen];
    var sbOff = (int)Md09SuperblockParser.SuperblockOffset(deviceLen);

    void U32(int rel, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(dev.AsSpan(sbOff + rel), v);

    U32(0, 0xA92B4EFC);   // magic
    U32(4, 0);            // major_version 0
    U32(28, 1);           // level = RAID1
    U32(32, 4096);        // size KiB per disk
    U32(40, 2);           // raid_disks
    U32(20, 0x11223344);  // set_uuid0
    U32(52, 0x55667788);  // set_uuid1
    U32(56, 0x99AABBCC);  // set_uuid2
    U32(60, 0xDDEEFF00);  // set_uuid3
    U32(256, 0);          // layout
    U32(260, 65536);      // chunk_size bytes
    U32(3968 + 12, 1);    // this_disk.raid_disk = role 1

    using var ms = new MemoryStream(dev, writable: false);
    var meta = Md09SuperblockParser.TryParse(ms);

    Assert.That(meta, Is.Not.Null);
    Assert.That(meta!.Format, Is.EqualTo(RaidMetadataFormat.Mdraid090));
    Assert.That(meta.Level, Is.EqualTo(RaidLevel.Raid1));
    Assert.That(meta.RaidDisks, Is.EqualTo(2));
    Assert.That(meta.Role, Is.EqualTo(1));
    Assert.That(meta.ChunkSizeBytes, Is.EqualTo(65536));
    Assert.That(meta.DataOffsetBytes, Is.Zero);
    Assert.That(meta.DataSizeBytes, Is.EqualTo(4096L * 1024));
    Assert.That(meta.ArrayUuid, Is.EqualTo("112233445566778899aabbccddeeff00"));
  }

  // ── IMSM (struct-parity) ───────────────────────────────────────────────
  [Test, Category("HappyPath")]
  public void Imsm_HandCraftedContainer_ParsesDisksAndVolumeGeometry() {
    var mpb = BuildImsm(numDisks: 3, rawLevel: 5, numMembers: 3, blocksPerStrip: 256,
      familyNum: 0xCAFEBABE, generation: 7, volumeName: "Volume0");

    // Embed near the end of a device image and parse via the tail scanner.
    var dev = new byte[2 * 1024 * 1024];
    mpb.CopyTo(dev, dev.Length - 8192);
    using var ms = new MemoryStream(dev, writable: false);

    var c = ImsmMetadataParser.TryParse(ms);
    Assert.That(c, Is.Not.Null);
    Assert.That(c!.FamilyNum, Is.EqualTo(0xCAFEBABE));
    Assert.That(c.GenerationNum, Is.EqualTo(7u));
    Assert.That(c.Disks.Count, Is.EqualTo(3));
    Assert.That(c.Disks[0].Serial, Is.EqualTo("SERIAL0"));
    Assert.That(c.Disks[2].TotalBlocks, Is.EqualTo(40960));
    Assert.That(c.Volumes.Count, Is.EqualTo(1));

    var vol = c.Volumes[0];
    Assert.That(vol.Name, Is.EqualTo("Volume0"));
    Assert.That(vol.Level, Is.EqualTo(RaidLevel.Raid5));
    Assert.That(vol.RawLevel, Is.EqualTo(5));
    Assert.That(vol.NumMembers, Is.EqualTo(3));
    Assert.That(vol.ChunkSizeBytes, Is.EqualTo(256L * 512));
    Assert.That(vol.DiskOrder, Is.EqualTo(new[] { 0, 1, 2 }));
  }

  [Test, Category("HappyPath")]
  public void Imsm_Raid1_TwoMembers_MapsToMirror() {
    var mpb = BuildImsm(numDisks: 2, rawLevel: 1, numMembers: 2, blocksPerStrip: 0,
      familyNum: 1, generation: 1, volumeName: "Mirror");
    var c = ImsmMetadataParser.Parse(mpb);
    Assert.That(c, Is.Not.Null);
    Assert.That(c!.Volumes[0].Level, Is.EqualTo(RaidLevel.Raid1));
  }

  /// <summary>
  /// Builds a minimal, self-consistent IMSM superblock matching
  /// <see cref="ImsmMetadataParser"/>'s classic single-map layout.
  /// </summary>
  private static byte[] BuildImsm(int numDisks, int rawLevel, int numMembers, ushort blocksPerStrip,
      uint familyNum, uint generation, string volumeName) {
    const int diskArray = 0xD8;
    const int diskSize = 48;
    const int devHeader = 0x44;
    const int volHeader = 0x20;
    const int mapOrdTable = 0x30;

    var devOffset = diskArray + numDisks * diskSize;
    var mapOffset = devOffset + devHeader + volHeader;
    var total = mapOffset + mapOrdTable + numMembers * 4 + 64;
    var b = new byte[total];

    ImsmMetadataParser.SignaturePrefix.CopyTo(b);
    Encoding.ASCII.GetBytes("1.2.02").CopyTo(b, ImsmMetadataParser.SignaturePrefix.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x28), familyNum);
    BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x2C), generation);
    b[0x38] = (byte)numDisks;
    b[0x39] = 1; // num_raid_devs

    for (var i = 0; i < numDisks; i++) {
      var d = diskArray + i * diskSize;
      Encoding.ASCII.GetBytes($"SERIAL{i}").CopyTo(b, d);
      BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(d + 0x10), 40960); // total_blocks_lo
    }

    Encoding.ASCII.GetBytes(volumeName).CopyTo(b, devOffset);
    var map = mapOffset;
    BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(map + 0x0C), blocksPerStrip);
    b[map + 0x0F] = (byte)rawLevel;
    b[map + 0x10] = (byte)numMembers;
    for (var m = 0; m < numMembers; m++)
      BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(map + mapOrdTable + m * 4), (uint)m);

    return b;
  }
}
