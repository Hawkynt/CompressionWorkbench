using FileSystem.Gpfs;

namespace Compression.Tests.Gpfs;

/// <summary>
/// Pins the semantic GPFS model to output published by IBM's own diagnostic
/// tools. These are clean-room oracle tests: they deliberately test what IBM
/// reports, not a guessed binary structure.
/// </summary>
[TestFixture]
public class GpfsOracleModelTests {

  [Test, Category("HappyPath")]
  public void Mmfsckx_ParsesPublishedFourteenDiskGeometryAndReservedFiles() {
    // IBM Storage Scale 6.0.1 mmfsckx documentation, real 14-disk example.
    const string text = """
      File system info
        format version is 39.00 (6.0.1.0)
        features are FastEA; StripedLogs; 4KAligned; IExpandV2
        allocation map layout is vertical  type is scatter
        replicas  metadata is 1 of 2  data+perf is 1+0 of 2  log is 0 of 2
        subblocksPerFullBlock is 512
        Sizes
          sector is 512 B
          metadata subblock is 8 KB (16 sectors)
          metadata fullblock is 4 MB (8192 sectors)
          data subblock is 8 KB (16 sectors)
          data fullblock is 4 MB (8192 sectors)
          inode is 4 KB (8 sectors)
          indirect block is 32 KB (64 sectors)
          ea overflow block is 64 KB (128 sectors)
          directory block is 256 KB (512 sectors)
      Reserved files info
        snapshots  global are 1  fileset are 0
        inode0 file
          inodeNum is 0  subblocksPerFullBlock is 512  recordsPerBlock is 1024
          inodeSpaceMask is 0x0  inodeBlockMask is 0xFF
        block alloc map file of system pool
          inodeNum is 1  subblocksPerFullBlock is 512  recordsPerBlock is 512
          disks are 14  regions are 784  segments are 2  holdsData yes
        inode alloc map file
          inodeNum is 2  subblocksPerFullBlock is 32  recordsPerBlock is 32
          type is cluster  regions are 521  segments are 1
          iallocSpaceMask is 0x0  iallocSegmtMask is 0x0
        access control list file
          inodeNum is 4  subblocksPerFullBlock is 512  recordsPerBlock is 1
        extended attribute file
          inodeNum is 5  subblocksPerFullBlock is 2  recordsPerBlock is 1
        fileset metadata file
          inodeNum is 38  subblocksPerFullBlock is 512  recordsPerBlock is 1024
          independent are 1  afm are 0  dependent are 0
      """;

    var model = GpfsOracleParser.ParseMmfsckx(text);

    Assert.Multiple(() => {
      Assert.That(model.Geometry.FormatVersion, Is.EqualTo("39.00"));
      Assert.That(model.Geometry.AllocationMapLayout, Is.EqualTo("vertical"));
      Assert.That(model.Geometry.AllocationMapType, Is.EqualTo("scatter"));
      Assert.That(model.Geometry.SubblocksPerFullBlock, Is.EqualTo(512));
      Assert.That(model.Geometry.SectorBytes, Is.EqualTo(512));
      Assert.That(model.Geometry.MetadataSubblockBytes, Is.EqualTo(8 * 1024));
      Assert.That(model.Geometry.MetadataFullblockBytes, Is.EqualTo(4 * 1024 * 1024));
      Assert.That(model.Geometry.InodeBytes, Is.EqualTo(4 * 1024));
      Assert.That(model.Geometry.IndirectBlockBytes, Is.EqualTo(32 * 1024));
      Assert.That(model.Geometry.DirectoryBlockBytes, Is.EqualTo(256 * 1024));
      Assert.That(model.ReservedFiles, Has.Count.EqualTo(6));
    });

    var inodeFile = model.ReservedFiles.Single(x => x.Kind == GpfsReservedFileKind.InodeFile);
    var blockMap = model.ReservedFiles.Single(x => x.Kind == GpfsReservedFileKind.BlockAllocationMap);
    var inodeMap = model.ReservedFiles.Single(x => x.Kind == GpfsReservedFileKind.InodeAllocationMap);
    var filesets = model.ReservedFiles.Single(x => x.Kind == GpfsReservedFileKind.FilesetMetadata);

    Assert.Multiple(() => {
      Assert.That(inodeFile.InodeNumber, Is.Zero);
      Assert.That(inodeFile.RecordsPerBlock, Is.EqualTo(1024));
      Assert.That(inodeFile.InodeSpaceMask, Is.Zero);
      Assert.That(inodeFile.InodeBlockMask, Is.EqualTo(0xFF));

      Assert.That(blockMap.InodeNumber, Is.EqualTo(1));
      Assert.That(blockMap.DiskCount, Is.EqualTo(14));
      Assert.That(blockMap.RegionCount, Is.EqualTo(784));
      Assert.That(blockMap.SegmentCount, Is.EqualTo(2));
      Assert.That(blockMap.HoldsData, Is.True);

      Assert.That(inodeMap.InodeNumber, Is.EqualTo(2));
      Assert.That(inodeMap.RegionCount, Is.EqualTo(521));
      Assert.That(inodeMap.SegmentCount, Is.EqualTo(1));
      Assert.That(inodeMap.IallocSpaceMask, Is.Zero);
      Assert.That(inodeMap.IallocSegmentMask, Is.Zero);

      Assert.That(filesets.InodeNumber, Is.EqualTo(38));
    });
  }

  [Test, Category("HappyPath")]
  public void Mmfsckx_ParsesPublishedAllocationMapWordMismatch() {
    // IBM Storage Scale 6.0.0 mmfsckx corruption example.
    const string text = """
      File system info
        format version is 33.00 (5.1.9.0)
        allocation map layout is vertical  type is scatter
        subblocksPerFullBlock is 512
        Sizes
          sector is 512 B
          metadata subblock is 8 KB (16 sectors)
          metadata fullblock is 4 MB (8192 sectors)
          data subblock is 8 KB (16 sectors)
          data fullblock is 4 MB (8192 sectors)
          inode is 4 KB (8 sectors)
          indirect block is 32 KB (64 sectors)
          ea overflow block is 64 KB (128 sectors)
          directory block is 256 KB (512 sectors)
      !Block 3:188416 has map status:
          0x0000000000000000 0x0000000000000000 0x0000000000000000 0x0000000000000000 0x0000000000000000 0x0000000000000000 0x0000000000000000 0x0000000000000000
        expected:
          0x0001FFFFF8000000 0x0000000000000000 0x0000000000000000 0x0000000000000000 0x0000000000000000 0x0000000000000000 0x0000000000000000 0x0000000000000000
      """;

    var mismatch = GpfsOracleParser.ParseMmfsckx(text).AllocationMapMismatches.Single();

    Assert.Multiple(() => {
      Assert.That(mismatch.Address, Is.EqualTo(new GpfsDiskAddress(3, 188416)));
      Assert.That(mismatch.ActualWords, Has.Count.EqualTo(8));
      Assert.That(mismatch.ExpectedWords, Has.Count.EqualTo(8));
      Assert.That(mismatch.ActualWords.All(static word => word == 0), Is.True);
      Assert.That(mismatch.ExpectedWords[0], Is.EqualTo(0x0001FFFFF8000000UL));
    });
  }

  [Test, Category("HappyPath")]
  public void Tsdbfs_ParsesPublishedReplicatedInode() {
    // IBM support document 7246086, Storage Scale 6.2.3.2 procedure.
    const string text = """
      Inode 64 [64] snap 0 (index 64 in block 0):
        Inode address: 7:520143360 10:780184064 size 4096 nAddrs 330
        indirectionLevel=INDIRECT status=RESERVED
        objectVersion=0 generation=0x1 nlink=1
        owner uid=0 gid=0 mode=00: ?---------
        flags set: unbalanced
        blocksize code=7 (128 subblocks)
        lastBlockSubblocks=128
        checksum=0x609701E6 is Valid
        fileSize=4353687552 nFullBlocks=1038
        currentMetadataReplicas=2 maxMetadataReplicas=2
        currentDataReplicas=2 maxDataReplicas=2
        dataPoolIndex=7
      """;

    var inode = GpfsOracleParser.ParseTsdbfsInode(text);

    Assert.Multiple(() => {
      Assert.That(inode.InodeNumber, Is.EqualTo(64));
      Assert.That(inode.IndexInBlock, Is.EqualTo(64));
      Assert.That(inode.InodeBlock, Is.Zero);
      Assert.That(inode.PhysicalAddresses, Is.EqualTo(new[] {
        new GpfsDiskAddress(7, 520143360),
        new GpfsDiskAddress(10, 780184064),
      }));
      Assert.That(inode.InodeSize, Is.EqualTo(4096));
      Assert.That(inode.AddressSlots, Is.EqualTo(330));
      Assert.That(inode.IndirectionLevel, Is.EqualTo("INDIRECT"));
      Assert.That(inode.Status, Is.EqualTo("RESERVED"));
      Assert.That(inode.Generation, Is.EqualTo(1));
      Assert.That(inode.Checksum, Is.EqualTo(0x609701E6));
      Assert.That(inode.ChecksumValid, Is.True);
      Assert.That(inode.FileSize, Is.EqualTo(4353687552));
      Assert.That(inode.FullBlocks, Is.EqualTo(1038));
      Assert.That(inode.CurrentMetadataReplicas, Is.EqualTo(2));
      Assert.That(inode.CurrentDataReplicas, Is.EqualTo(2));
      Assert.That(inode.DataPoolIndex, Is.EqualTo(7));
    });
  }

  [Test, Category("HappyPath")]
  public void Mmfileid_MapsReservedAndUserSectors() {
    // IBM mmfileid command documentation. The command is invoked per NSD/range;
    // disk id 2 is supplied by the caller because the text output only echoes sector.
    const string text = """
      Address 2201958 is contained in the Block allocation map (inode 1)
      Address 2206688 is contained in the ACL Data file (inode 4, snapId 0)
      Address 2211038 is contained in the Log File (inode 7, snapId 0)
      14336 1076256 0 /gpfsB/tesDir/testFile.out
      14344 2922528 1 /gpfsB/x.img
      """;

    var owners = GpfsOracleParser.ParseMmfileid(text, diskId: 2);

    Assert.Multiple(() => {
      Assert.That(owners, Has.Count.EqualTo(5));
      Assert.That(owners[0].Address, Is.EqualTo(new GpfsDiskAddress(2, 2201958)));
      Assert.That(owners[0].InodeNumber, Is.EqualTo(1));
      Assert.That(owners[1].InodeNumber, Is.EqualTo(4));
      Assert.That(owners[2].InodeNumber, Is.EqualTo(7));
      Assert.That(owners[3].InodeNumber, Is.EqualTo(14336));
      Assert.That(owners[3].SnapshotId, Is.Zero);
      Assert.That(owners[3].Path, Is.EqualTo("/gpfsB/tesDir/testFile.out"));
      Assert.That(owners[4].SnapshotId, Is.EqualTo(1));
    });
  }

  [Test, Category("HappyPath")]
  public void Mmgetlocation_ParsesThreeReplicaDiskIds() {
    // IBM mmgetlocation -Y -L example. One logical chunk is reported on three
    // NSDs; the disk IDs cross-reference mmlsdisk -L.
    const string text = """
      mmgetlocation:fileDataInfor:0:0):data_c3m3n03_sdd:c3m3n03:5:3,0,0::data_c3m3n02_sdc:c3m3n02:3:1,0,0::data_c3m3n04_sdc:c3m3n04:9:2,0,0::
      """;

    var location = GpfsOracleParser.ParseMmgetlocation(text).Single();

    Assert.Multiple(() => {
      Assert.That(location.ChunkIndex, Is.Zero);
      Assert.That(location.Offset, Is.Zero);
      Assert.That(location.Replicas.Select(static replica => replica.DiskId), Is.EqualTo(new[] { 5, 3, 9 }));
      Assert.That(location.Replicas.Select(static replica => replica.NsdName), Is.EqualTo(new[] {
        "data_c3m3n03_sdd",
        "data_c3m3n02_sdc",
        "data_c3m3n04_sdc",
      }));
    });
  }

  [TestCase("7:332464", 7, 332464L)]
  [TestCase("0:0", 0, 0L)]
  public void DiskAddress_ParsesDiskAndSector(string text, int disk, long sector) {
    Assert.That(GpfsDiskAddress.TryParse(text, out var address), Is.True);
    Assert.That(address, Is.EqualTo(new GpfsDiskAddress(disk, sector)));
  }

  [TestCase("")]
  [TestCase("7")]
  [TestCase(":332464")]
  [TestCase("7:-1")]
  [TestCase("x:332464")]
  public void DiskAddress_RejectsMalformedValues(string text)
    => Assert.That(GpfsDiskAddress.TryParse(text, out _), Is.False);
}
