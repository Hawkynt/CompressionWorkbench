using Compression.Registry;
using FileSystem.Ext;
using FileSystem.GlusterFs;
using FileSystem.Xfs;

namespace Compression.Tests.GlusterFs;

[TestFixture]
public sealed class GlusterFsAllocationMapTests {
  private static readonly byte[] Gfid = [
    0x42, 0x9A, 0x10, 0x73, 0x8C, 0x54, 0x4A, 0xD7,
    0xB0, 0xE1, 0x16, 0x33, 0x6F, 0xC2, 0xD8, 0x05,
  ];

  [Test, Category("HappyPath")]
  public void Completer_CoversEveryByteAndNeverFreesWhatSomethingClaims() {
    const int block = 4096;
    var decoded = new[] {
      new DefragBlockInfo(2L * block, block, DefragBlockKind.Used, "file.bin"),
      new DefragBlockInfo(3L * block, block, DefragBlockKind.Used, "conflict.bin"),
    };
    var free = new[] {
      (Offset: (long)block, Length: (long)block),
      (Offset: 3L * block, Length: (long)block),
    };

    var map = FilesystemAllocationMapCompleter.Complete(4L * block, decoded, free);

    AssertCompleteCoverage(map, 4L * block);
    Assert.Multiple(() => {
      // Nothing decoded block 0 and the allocator did not prove it free, so it stays reserved.
      Assert.That(Owner(map, 0).Kind, Is.EqualTo(DefragBlockKind.MetadataReserved));
      Assert.That(Owner(map, block).Kind, Is.EqualTo(DefragBlockKind.Free));
      Assert.That(Owner(map, 2L * block).Kind, Is.EqualTo(DefragBlockKind.Used));
      Assert.That(Owner(map, 2L * block).FileName, Is.EqualTo("file.bin"));

      // The allocator calls block 3 free while the walker decoded a file there.
      // The decoded claim survives verbatim and the free run is trimmed away, so
      // the disagreement can never reach the wiper as a zeroing instruction.
      Assert.That(Owner(map, 3L * block).Kind, Is.EqualTo(DefragBlockKind.Used));
      Assert.That(Owner(map, 3L * block).FileName, Is.EqualTo("conflict.bin"));
      Assert.That(map.Any(extent => extent.Kind == DefragBlockKind.Free
          && extent.Offset < 4L * block && 3L * block < extent.Offset + extent.Length),
        Is.False, "a proven-free run must never overlap a decoded claim");
    });
  }

  [Test, Category("RoundTrip")]
  public void ExtExtentMap_IsGapFreeAndPreservesGfidWhenFreeSpaceIsWiped() {
    var bytes = BuildExtBrick();
    var descriptor = new GlusterFsFormatDescriptor();
    using var image = new MemoryStream(bytes, writable: true);

    var map = ((IFilesystemExtentMap)descriptor).EnumerateExtents(image).ToArray();
    AssertCompleteCoverage(map, image.Length);
    Assert.That(map.Any(extent => extent.Kind == DefragBlockKind.Free), Is.True);
    Assert.That(map.Any(extent => extent.Kind == DefragBlockKind.Used && extent.FileName == "srv/brick/data/hello.txt"), Is.True);

    DirtyAndWipeOneFreeRange(image, descriptor, map);

    image.Position = 0;
    using var reader = new GlusterFsReader(image);
    var entry = reader.Entries.Single(candidate => candidate.Name == "brick/data/hello.txt");
    Assert.Multiple(() => {
      Assert.That(reader.Extract(entry), Is.EqualTo("hello from ext"u8.ToArray()));
      Assert.That(reader.ReadExtendedAttributes(entry)["trusted.gfid"], Is.EqualTo(Gfid));
    });
  }

  [Test, Category("RoundTrip")]
  public void XfsExtentMap_IsGapFreeAndPreservesGfidWhenFreeSpaceIsWiped() {
    var bytes = BuildXfsBrick();
    var descriptor = new GlusterFsFormatDescriptor();
    using var image = new MemoryStream(bytes, writable: true);

    var map = ((IFilesystemExtentMap)descriptor).EnumerateExtents(image).ToArray();
    AssertCompleteCoverage(map, image.Length);
    Assert.That(map.Any(extent => extent.Kind == DefragBlockKind.Free), Is.True);
    Assert.That(map.Any(extent => extent.Kind == DefragBlockKind.Used && extent.FileName == "host/brick/data/hello.txt"), Is.True);

    DirtyAndWipeOneFreeRange(image, descriptor, map);

    image.Position = 0;
    using var reader = new GlusterFsReader(image);
    var entry = reader.Entries.Single(candidate => candidate.Name == "brick/data/hello.txt");
    Assert.Multiple(() => {
      Assert.That(reader.Extract(entry), Is.EqualTo("hello from xfs"u8.ToArray()));
      Assert.That(reader.ReadExtendedAttributes(entry)["trusted.gfid"], Is.EqualTo(Gfid));
    });
  }

  [Test, Category("Regression")]
  public void GlusterDescriptor_ExposesWipeThroughCompleteExtentMapButNotDefrag() {
    var descriptor = new GlusterFsFormatDescriptor();
    Assert.Multiple(() => {
      Assert.That(descriptor, Is.InstanceOf<IFilesystemExtentMap>());
      Assert.That(descriptor, Is.InstanceOf<IWipeEmpty>());
      Assert.That(descriptor, Is.Not.InstanceOf<IArchiveDefragmentable>());
      Assert.That(descriptor, Is.Not.InstanceOf<ILayoutOptimizable>());
      Assert.That(descriptor, Is.Not.InstanceOf<IArchiveModifiable>());
    });
  }

  private static byte[] BuildExtBrick() {
    var writer = new ExtWriter();
    writer.AddFile("srv/brick/data/hello.txt", "hello from ext"u8.ToArray());
    writer.AddFile("srv/brick/.glusterfs/ab/index", "gfid-index"u8.ToArray());
    writer.AddFile("outside.txt", "outside"u8.ToArray());
    var bytes = writer.Build(
      blockSize: 1024,
      totalBlocks: 4096,
      version: ExtWriter.ExtVersion.Ext4,
      journal: false,
      volumeLabel: "gluster-map",
      inodeSize: 256);
    using var image = new MemoryStream(bytes, writable: true);
    ExtExtendedAttributes.Set(image, "srv/brick/data/hello.txt", "trusted.gfid", Gfid);
    return image.ToArray();
  }

  private static byte[] BuildXfsBrick() {
    var writer = new XfsWriter();
    writer.AddFile("host/brick/data/hello.txt", "hello from xfs"u8.ToArray());
    writer.AddFile("host/brick/.glusterfs/ab/index", "gfid-index"u8.ToArray());
    writer.AddFile("outside.txt", "outside"u8.ToArray());
    using var image = new MemoryStream();
    writer.WriteTo(image);
    XfsExtendedAttributes.Set(image, "host/brick/data/hello.txt", "trusted.gfid", Gfid);
    return image.ToArray();
  }

  private static void DirtyAndWipeOneFreeRange(
      MemoryStream image,
      GlusterFsFormatDescriptor descriptor,
      IReadOnlyList<DefragBlockInfo> map) {
    var free = map.First(extent => extent.Kind == DefragBlockKind.Free && extent.Length >= 32);
    var dirt = Enumerable.Repeat((byte)0xA5, 32).ToArray();
    image.Position = free.Offset;
    image.Write(dirt);

    image.Position = 0;
    _ = ((IWipeEmpty)descriptor).WipeUnusedSpace(image, wipeClusterTips: false, wipeDeletedEntries: false);

    Span<byte> actual = stackalloc byte[32];
    image.Position = free.Offset;
    image.ReadExactly(actual);
    Assert.That(actual.ToArray(), Is.EqualTo(new byte[32]));
  }

  /// <summary>
  /// The extent that decides what may happen to <paramref name="offset"/>. A map
  /// describes real structures at more than one granularity — an inode inside its
  /// chunk, a file run inside its allocation unit — so a byte can legitimately
  /// carry several descriptions. Any claim outranks free space.
  /// </summary>
  private static DefragBlockInfo Owner(IReadOnlyList<DefragBlockInfo> map, long offset) {
    var covering = map.Where(extent => extent.Offset <= offset && offset < extent.Offset + extent.Length).ToArray();
    Assert.That(covering, Is.Not.Empty, $"byte {offset} is described by nothing");
    return covering.FirstOrDefault(extent => extent.Kind != DefragBlockKind.Free) ?? covering[0];
  }

  /// <summary>
  /// Every byte is described by something, and nothing offered for zeroing is
  /// also claimed. Those are the two properties maintenance actually relies on;
  /// requiring a single flat partition instead would forbid describing a
  /// structure and its contents at the same time.
  /// </summary>
  private static void AssertCompleteCoverage(IReadOnlyList<DefragBlockInfo> map, long length) {
    Assert.That(map, Is.Not.Empty);
    foreach (var extent in map)
      Assert.That(extent.Length, Is.GreaterThan(0));

    var ordered = map.OrderBy(extent => extent.Offset).ToArray();
    var reached = 0L;
    foreach (var extent in ordered) {
      Assert.That(extent.Offset, Is.LessThanOrEqualTo(reached),
        $"nothing describes the bytes at {reached}");
      reached = Math.Max(reached, checked(extent.Offset + extent.Length));
    }

    Assert.That(reached, Is.EqualTo(length));

    var claimed = ordered.Where(extent => extent.Kind != DefragBlockKind.Free).ToArray();
    foreach (var free in ordered.Where(extent => extent.Kind == DefragBlockKind.Free))
      Assert.That(
        claimed.Any(other => other.Offset < free.Offset + free.Length && free.Offset < other.Offset + other.Length),
        Is.False,
        $"the free run at {free.Offset} overlaps something that claims those bytes");
  }
}
