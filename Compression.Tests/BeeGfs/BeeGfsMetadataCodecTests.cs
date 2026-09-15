using System.Text;
using Compression.Registry;
using FileSystem.BeeGfs;

namespace Compression.Tests.BeeGfs;

[TestFixture]
public sealed class BeeGfsMetadataCodecTests {
  [Test, Category("KnownAnswer")]
  public void V3DirectoryDentry_DecodesIdentityAndOwner() {
    var bytes = BuildV3Directory("0-67059D13-1", ownerNodeId: 7);

    var decoded = BeeGfsMetadataCodec.ParseDentry(bytes, "root");

    Assert.Multiple(() => {
      Assert.That(decoded.MetadataType, Is.EqualTo(BeeGfsDiskMetadataType.DirectoryDentry));
      Assert.That(decoded.StorageFormatVersion, Is.EqualTo(3));
      Assert.That(decoded.Kind, Is.EqualTo(FilesystemNodeKind.Directory));
      Assert.That(decoded.EntryId, Is.EqualTo("0-67059D13-1"));
      Assert.That(decoded.OwnerNodeId, Is.EqualTo(7));
      Assert.That(decoded.HasInlineInode, Is.False);
      Assert.That(decoded.Raid0Pattern, Is.Null);
    });
  }

  [Test, Category("KnownAnswer")]
  public void V6InlineRaid0File_DecodesStatPathAndTargets() {
    var bytes = BuildV6File(
      entryId: "1-6705972F-1",
      parentId: "0-67059D13-1",
      fileSize: 1_234_567,
      statFlags: 0,
      patternType: 1,
      targetIds: [101, 201]);

    var decoded = BeeGfsMetadataCodec.ParseDentry(bytes, "different-current-parent");

    Assert.Multiple(() => {
      Assert.That(decoded.MetadataType, Is.EqualTo(BeeGfsDiskMetadataType.FileDentry));
      Assert.That(decoded.StorageFormatVersion, Is.EqualTo(6));
      Assert.That(decoded.Kind, Is.EqualTo(FilesystemNodeKind.RegularFile));
      Assert.That(decoded.EntryId, Is.EqualTo("1-6705972F-1"));
      Assert.That(decoded.HasInlineInode, Is.True);
      Assert.That(decoded.Size, Is.EqualTo(1_234_567));
      Assert.That(decoded.LinkCount, Is.EqualTo(1));
      Assert.That(decoded.UserId, Is.EqualTo(1000));
      Assert.That(decoded.GroupId, Is.EqualTo(100));
      Assert.That(decoded.OriginalUserId, Is.EqualTo(2000));
      Assert.That(decoded.OriginalParentEntryId, Is.EqualTo("0-67059D13-1"));
      Assert.That(decoded.Mode, Is.EqualTo(0x81A4u));
      Assert.That(decoded.Raid0Pattern, Is.Not.Null);
      Assert.That(decoded.Raid0Pattern!.ChunkSize, Is.EqualTo(512u * 1024));
      Assert.That(decoded.Raid0Pattern.StoragePoolId, Is.EqualTo(1));
      Assert.That(decoded.Raid0Pattern.DefaultTargetCount, Is.EqualTo(4));
      Assert.That(decoded.Raid0Pattern.TargetIds, Is.EqualTo(new ushort[] { 101, 201 }));
    });
  }

  [Test, Category("KnownAnswer")]
  public void ChunkPath_MatchesDocumentedRootExample() {
    var path = BeeGfsChunkLayout.BuildChunkRelativePath(0, "root", "1-6705972F-1");

    Assert.That(path, Is.EqualTo("u0/0/r/root/1-6705972F-1"));
  }

  [Test, Category("KnownAnswer")]
  public void ChunkPath_UsesUppercaseUidHexAndParentTimestampBuckets() {
    var path = BeeGfsChunkLayout.BuildChunkRelativePath(1000, "0-67059D13-1", "9-6705A000-1");

    Assert.That(path, Is.EqualTo("u3E8/6705/9/0-67059D13-1/9-6705A000-1"));
  }

  [Test, Category("HappyPath")]
  public void Raid0OffsetMapping_AlternatesTargetsAndCompactsLocalOffsets() {
    const uint chunk = 64 * 1024;

    Assert.Multiple(() => {
      Assert.That(BeeGfsChunkLayout.TargetIndex(0, chunk, 2), Is.EqualTo(0));
      Assert.That(BeeGfsChunkLayout.TargetLocalOffset(0, chunk, 2), Is.EqualTo(0));
      Assert.That(BeeGfsChunkLayout.TargetIndex(chunk, chunk, 2), Is.EqualTo(1));
      Assert.That(BeeGfsChunkLayout.TargetLocalOffset(chunk, chunk, 2), Is.EqualTo(0));
      Assert.That(BeeGfsChunkLayout.TargetIndex(2L * chunk + 123, chunk, 2), Is.EqualTo(0));
      Assert.That(BeeGfsChunkLayout.TargetLocalOffset(2L * chunk + 123, chunk, 2), Is.EqualTo(chunk + 123));
      Assert.That(BeeGfsChunkLayout.TargetIndex(3L * chunk + 456, chunk, 2), Is.EqualTo(1));
      Assert.That(BeeGfsChunkLayout.TargetLocalOffset(3L * chunk + 456, chunk, 2), Is.EqualTo(chunk + 456));
      Assert.That(BeeGfsChunkLayout.BytesUntilNextChunk(chunk - 7, chunk, 100), Is.EqualTo(7));
    });
  }

  [TestCase((ushort)4)]
  [TestCase((ushort)8)]
  [Category("Exception")]
  public void MirroredDentry_FailsClosed(ushort mirroredFlag) {
    var bytes = BuildV3Directory("0-67059D13-1", ownerNodeId: 7, dentryFlags: (ushort)(16 | mirroredFlag));

    var error = Assert.Throws<NotSupportedException>(() =>
      BeeGfsMetadataCodec.ParseDentry(bytes, "root"));

    Assert.That(error!.Message, Does.Contain("mirrored dentry"));
  }

  [Test, Category("Exception")]
  public void SparseV6File_FailsClosedBeforeVariableBlockVector() {
    var bytes = BuildV6File("1-A-B", "root", 10, statFlags: 1, patternType: 1, targetIds: [1]);

    var error = Assert.Throws<NotSupportedException>(() =>
      BeeGfsMetadataCodec.ParseDentry(bytes, "root"));

    Assert.That(error!.Message, Does.Contain("sparse-file"));
  }

  [Test, Category("Exception")]
  public void BuddyPattern_FailsClosed() {
    var bytes = BuildV6File("1-A-B", "root", 10, statFlags: 0, patternType: 3, targetIds: [1]);

    var error = Assert.Throws<NotSupportedException>(() =>
      BeeGfsMetadataCodec.ParseDentry(bytes, "root"));

    Assert.That(error!.Message, Does.Contain("only RAID0"));
  }

  [TestCase(0)]
  [TestCase(7)]
  [TestCase(12)]
  [TestCase(63)]
  [Category("Malformed")]
  public void TruncatedV6Metadata_IsRejected(int bytesToKeep) {
    var complete = BuildV6File("1-6705972F-1", "root", 10, 0, 1, [1]);
    var truncated = complete[..Math.Min(bytesToKeep, complete.Length)];

    Assert.That(
      () => BeeGfsMetadataCodec.ParseDentry(truncated, "root"),
      Throws.Exception);
  }

  private static byte[] BuildV3Directory(string entryId, uint ownerNodeId, ushort dentryFlags = 16) {
    using var output = new MemoryStream();
    using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
    writer.Write((byte)2); // DiskMetaDataType_DIRDENTRY
    writer.Write((byte)3);
    writer.Write(dentryFlags); // DENTRY_FEATURE_*
    writer.Write((byte)1); // directory
    writer.Write(new byte[3]);
    WriteAlignedString4(writer, entryId);
    writer.Write(ownerNodeId);
    return output.ToArray();
  }

  private static byte[] BuildV6File(
      string entryId,
      string parentId,
      long fileSize,
      uint statFlags,
      uint patternType,
      ushort[] targetIds) {
    using var output = new MemoryStream();
    using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);

    writer.Write((byte)1); // DiskMetaDataType_FILEDENTRY
    writer.Write((byte)6);
    writer.Write((ushort)(1 | 2 | 16)); // inline inode | file inode | 32-bit ids
    writer.Write((byte)2); // regular file
    writer.Write(new byte[3]);

    const uint inodeFeatures = 16 | 32 | 64 | 128; // orig parent, orig uid, stat flags, versions
    writer.Write(inodeFeatures);
    writer.Write(0u); // no state flags: four alignment bytes

    writer.Write(statFlags);
    writer.Write(0x81A4u); // S_IFREG | 0644
    writer.Write(1_700_000_000L); // creation
    writer.Write(1_700_000_001L); // access
    writer.Write(1_700_000_002L); // modification
    writer.Write(1_700_000_003L); // ctime
    writer.Write(fileSize);
    writer.Write(1u); // hard links
    writer.Write(11u); // stat metadata version
    writer.Write(1000u); // uid
    writer.Write(100u); // gid
    writer.Write(2000u); // original uid
    WriteAlignedString4(writer, parentId);
    WriteAlignedString4(writer, entryId);

    var pattern = BuildRaidPattern(patternType, targetIds);
    writer.Write(pattern);
    writer.Write(3u); // file version
    writer.Write(11u); // metadata version
    return output.ToArray();
  }

  private static byte[] BuildRaidPattern(uint patternType, ushort[] targetIds) {
    using var body = new MemoryStream();
    using (var writer = new BinaryWriter(body, Encoding.UTF8, leaveOpen: true)) {
      writer.Write(patternType);
      writer.Write(512u * 1024);
      writer.Write((ushort)1); // storage pool
      writer.Write(4u); // desired/default targets
      writer.Write(checked(8u + (uint)targetIds.Length * 2u));
      writer.Write((uint)targetIds.Length);
      foreach (var id in targetIds) writer.Write(id);
    }

    using var pattern = new MemoryStream();
    using (var writer = new BinaryWriter(pattern, Encoding.UTF8, leaveOpen: true)) {
      writer.Write(checked((uint)(4 + body.Length)));
      writer.Write(body.ToArray());
    }
    return pattern.ToArray();
  }

  private static void WriteAlignedString4(BinaryWriter writer, string value) {
    var start = writer.BaseStream.Position;
    var bytes = Encoding.UTF8.GetBytes(value);
    writer.Write((uint)bytes.Length);
    writer.Write(bytes);
    writer.Write((byte)0);
    while (((writer.BaseStream.Position - start) & 3) != 0)
      writer.Write((byte)0);
  }
}
