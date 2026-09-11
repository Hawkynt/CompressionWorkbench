using System.Text;
using Compression.Registry;
using FileSystem.Ext;
using FileSystem.GlusterFs;
using FileSystem.Xfs;

namespace Compression.Tests.GlusterFs;

[TestFixture]
public class GlusterFsDetectionTests {

  private static readonly byte[] Gfid = [
    0x10, 0x32, 0x54, 0x76, 0x98, 0xBA, 0xDC, 0xFE,
    0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF,
  ];

  private static byte[] BuildXfsBrick() {
    var writer = new XfsWriter();
    writer.AddFile("host/brick/data/hello.txt", "hello from xfs brick"u8.ToArray());
    writer.AddFile("host/brick/.glusterfs/ab/internal-gfid", "backend index"u8.ToArray());
    writer.AddFile("outside.txt", "not part of the brick"u8.ToArray());
    using var stream = new MemoryStream();
    writer.WriteTo(stream);
    XfsExtendedAttributes.Set(stream, "host/brick/data/hello.txt", "trusted.gfid", Gfid);
    return stream.ToArray();
  }

  private static byte[] BuildExtBrick() {
    var writer = new ExtWriter();
    writer.AddFile("srv/brick/data/hello.txt", "hello from ext brick"u8.ToArray());
    writer.AddFile("srv/brick/.glusterfs/cd/internal-gfid", "backend index"u8.ToArray());
    writer.AddFile("outside.txt", "not part of the brick"u8.ToArray());
    var bytes = writer.Build(
      blockSize: 1024,
      totalBlocks: 4096,
      version: ExtWriter.ExtVersion.Ext4,
      journal: false,
      volumeLabel: "gluster",
      inodeSize: 256);
    using var stream = new MemoryStream(bytes, writable: true);
    ExtExtendedAttributes.Set(stream, "srv/brick/data/hello.txt", "trusted.gfid", Gfid);
    return stream.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Descriptor_UsesExtensionOnlyDetectionAndAdvertisesReadOnlyBrickView() {
    var descriptor = new GlusterFsFormatDescriptor();

    Assert.Multiple(() => {
      Assert.That(descriptor.Id, Is.EqualTo("GlusterFs"));
      Assert.That(descriptor.Extensions, Does.Contain(".gluster"));
      Assert.That(descriptor.MagicSignatures, Is.Empty,
        "GlusterFS has no standalone block-format magic and must not collide with XFS/ext detection.");
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanList), Is.True);
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanExtract), Is.True);
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.SupportsDirectories), Is.True);
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False);
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
      Assert.That(descriptor, Is.InstanceOf<IArchiveShrinkable>());
    });
  }

  [Test, Category("HappyPath")]
  public void XfsBackingStore_InfersBrickRootAndHidesNonBrickContentAndGlusterIndex() {
    var descriptor = new GlusterFsFormatDescriptor();
    using var stream = new MemoryStream(BuildXfsBrick());

    var names = descriptor.List(stream, password: null).Select(entry => entry.Name).ToArray();

    Assert.Multiple(() => {
      Assert.That(names, Does.Contain("metadata.ini"));
      Assert.That(names, Does.Contain("brick/data/hello.txt"));
      Assert.That(names.Any(name => name.Contains(".glusterfs", StringComparison.Ordinal)), Is.False);
      Assert.That(names, Does.Not.Contain("brick/outside.txt"));
    });
  }

  [Test, Category("HappyPath")]
  public void ExtBackingStore_InfersBrickRootAndExtractsBrickFile() {
    using var stream = new MemoryStream(BuildExtBrick());
    using var reader = new GlusterFsReader(stream);

    var entry = reader.Entries.Single(candidate => candidate.Name == "brick/data/hello.txt");

    Assert.Multiple(() => {
      Assert.That(reader.ValidHeader, Is.True);
      Assert.That(reader.BackingFileSystem, Is.EqualTo("ext"));
      Assert.That(reader.BrickRoot, Is.EqualTo("srv/brick"));
      Assert.That(reader.HasGlusterIndex, Is.True);
      Assert.That(Encoding.UTF8.GetString(reader.Extract(entry)), Is.EqualTo("hello from ext brick"));
      Assert.That(reader.ReadExtendedAttributes(entry)["trusted.gfid"], Is.EqualTo(Gfid));
      Assert.That(reader.Entries.Any(candidate => candidate.Name.Contains(".glusterfs", StringComparison.Ordinal)), Is.False);
      Assert.That(reader.Entries.Any(candidate => candidate.Name == "brick/outside.txt"), Is.False);
    });
  }

  [Test, Category("HappyPath")]
  public void XfsBackingStore_SurfacesNativeGlusterXattrs() {
    using var stream = new MemoryStream(BuildXfsBrick());
    using var reader = new GlusterFsReader(stream);
    var entry = reader.Entries.Single(candidate => candidate.Name == "brick/data/hello.txt");

    Assert.That(reader.ReadExtendedAttributes(entry)["trusted.gfid"], Is.EqualTo(Gfid));
  }

  [Test, Category("HappyPath")]
  public void XfsBackingStore_ExtractsThroughDescriptor() {
    var descriptor = new GlusterFsFormatDescriptor();
    using var stream = new MemoryStream(BuildXfsBrick());
    var workDir = Path.Combine(Path.GetTempPath(), $"GlusterFsBrick_{Guid.NewGuid():N}");
    Directory.CreateDirectory(workDir);
    try {
      descriptor.Extract(stream, workDir, password: null, files: ["brick/data/hello.txt"]);
      var path = Path.Combine(workDir, "brick", "data", "hello.txt");
      Assert.That(File.ReadAllText(path, Encoding.UTF8), Is.EqualTo("hello from xfs brick"));
    } finally {
      try { Directory.Delete(workDir, recursive: true); } catch { /* best effort */ }
    }
  }

  [Test, Category("RoundTrip")]
  public void ExtBackingStore_ShrinkToFit_PreservesPayloadAndGfid() {
    var original = BuildExtBrick();
    var descriptor = new GlusterFsFormatDescriptor();
    using var input = new MemoryStream(original, writable: false);
    using var output = new MemoryStream();

    descriptor.Shrink(input, output);

    Assert.That(output.Length, Is.LessThan(original.LongLength));
    output.Position = 0;
    using var reader = new GlusterFsReader(output);
    var entry = reader.Entries.Single(candidate => candidate.Name == "brick/data/hello.txt");
    Assert.Multiple(() => {
      Assert.That(Encoding.UTF8.GetString(reader.Extract(entry)), Is.EqualTo("hello from ext brick"));
      Assert.That(reader.ReadExtendedAttributes(entry)["trusted.gfid"], Is.EqualTo(Gfid));
    });
  }

  [Test, Category("RoundTrip")]
  public void XfsBackingStore_ShrinkCopiesThroughWithoutRebuild() {
    var original = BuildXfsBrick();
    var descriptor = new GlusterFsFormatDescriptor();
    using var input = new MemoryStream(original, writable: false);
    using var output = new MemoryStream();

    descriptor.Shrink(input, output);

    Assert.That(output.ToArray(), Is.EqualTo(original));
    output.Position = 0;
    using var reader = new GlusterFsReader(output);
    var entry = reader.Entries.Single(candidate => candidate.Name == "brick/data/hello.txt");
    Assert.That(reader.ReadExtendedAttributes(entry)["trusted.gfid"], Is.EqualTo(Gfid));
  }

  [Test, Category("Regression")]
  public void WorkbenchProbeMagic_IsNoLongerAcceptedAsGlusterFs() {
    byte[] oldSyntheticProbe = [0xCA, 0xFE, 0x5B, 0xAB, 0, 0, 0, 0];
    using var stream = new MemoryStream(oldSyntheticProbe);

    var error = Assert.Throws<InvalidDataException>(() => new GlusterFsReader(stream));

    Assert.That(error!.Message, Does.Contain("backing-store image"));
  }

  [Test, Category("Regression")]
  public void Descriptor_DoesNotAdvertiseUnsafeMutations() {
    var descriptor = new GlusterFsFormatDescriptor();

    Assert.Multiple(() => {
      Assert.That(descriptor, Is.Not.InstanceOf<IArchiveCreatable>());
      Assert.That(descriptor, Is.Not.InstanceOf<IArchiveModifiable>());
      Assert.That(descriptor, Is.Not.InstanceOf<IArchivePurgeable>());
      Assert.That(descriptor, Is.Not.InstanceOf<IArchiveDefragmentable>());
      Assert.That(descriptor, Is.Not.InstanceOf<IWipeEmpty>());
      Assert.That(descriptor, Is.Not.InstanceOf<ILayoutOptimizable>());
    });
  }

  [Test, Category("HappyPath")]
  public void Metadata_DescribesXattrProgressAndClusterOperationBoundary() {
    using var stream = new MemoryStream(BuildXfsBrick());
    using var reader = new GlusterFsReader(stream);
    var metadata = reader.Entries.Single(entry => entry.Name == "metadata.ini");
    var text = Encoding.UTF8.GetString(reader.Extract(metadata));

    Assert.Multiple(() => {
      Assert.That(text, Does.Contain("parse_status=single-brick-read-only"));
      Assert.That(text, Does.Contain("backing_fs=xfs"));
      Assert.That(text, Does.Contain("brick_root=host/brick"));
      Assert.That(text, Does.Contain("gluster_index_detected=true"));
      Assert.That(text, Does.Contain("xattrs=readable through native backing-filesystem accessors"));
      Assert.That(text, Does.Contain("xattr_mutation=native inline/short-form subset only"));
      Assert.That(text, Does.Contain("cluster_namespace_reconstruction=false"));
      Assert.That(text, Does.Contain("cluster_operations=rebalance,fix-layout,remove-brick are outside the single-image abstraction"));
      Assert.That(text, Does.Contain("external/leaf/btree xattr mutation is not complete yet"));
    });
  }
}
