using System.Text;
using Compression.Registry;
using FileSystem.GlusterFs;

namespace Compression.Tests.GlusterFs;

[TestFixture]
public class GlusterFsDetectionTests {

  private static byte[] BuildXfsBrick() {
    var writer = new FileSystem.Xfs.XfsWriter();
    writer.AddFile("data/hello.txt", "hello from xfs brick"u8.ToArray());
    writer.AddFile(".glusterfs/ab/internal-gfid", "backend index"u8.ToArray());
    using var stream = new MemoryStream();
    writer.WriteTo(stream);
    return stream.ToArray();
  }

  private static byte[] BuildExtBrick() {
    var writer = new FileSystem.Ext.ExtWriter();
    writer.AddFile("data/hello.txt", "hello from ext brick"u8.ToArray());
    writer.AddFile(".glusterfs/cd/internal-gfid", "backend index"u8.ToArray());
    return writer.Build();
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
    });
  }

  [Test, Category("HappyPath")]
  public void XfsBackingStore_ListsBrickFilesAndHidesGlusterInternalIndex() {
    var descriptor = new GlusterFsFormatDescriptor();
    using var stream = new MemoryStream(BuildXfsBrick());

    var names = descriptor.List(stream, password: null).Select(entry => entry.Name).ToArray();

    Assert.Multiple(() => {
      Assert.That(names, Does.Contain("metadata.ini"));
      Assert.That(names, Does.Contain("brick/data/hello.txt"));
      Assert.That(names.Any(name => name.Contains(".glusterfs", StringComparison.Ordinal)), Is.False);
    });
  }

  [Test, Category("HappyPath")]
  public void ExtBackingStore_ListsAndExtractsBrickFile() {
    using var stream = new MemoryStream(BuildExtBrick());
    using var reader = new GlusterFsReader(stream);

    var entry = reader.Entries.Single(candidate => candidate.Name == "brick/data/hello.txt");

    Assert.Multiple(() => {
      Assert.That(reader.ValidHeader, Is.True);
      Assert.That(reader.BackingFileSystem, Is.EqualTo("ext"));
      Assert.That(Encoding.UTF8.GetString(reader.Extract(entry)), Is.EqualTo("hello from ext brick"));
      Assert.That(reader.Entries.Any(candidate => candidate.Name.Contains(".glusterfs", StringComparison.Ordinal)), Is.False);
    });
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

  [Test, Category("Regression")]
  public void WorkbenchProbeMagic_IsNoLongerAcceptedAsGlusterFs() {
    byte[] oldSyntheticProbe = [0xCA, 0xFE, 0x5B, 0xAB, 0, 0, 0, 0];
    using var stream = new MemoryStream(oldSyntheticProbe);

    var error = Assert.Throws<InvalidDataException>(() => new GlusterFsReader(stream));

    Assert.That(error!.Message, Does.Contain("backing-store image"));
  }

  [Test, Category("Regression")]
  public void Descriptor_DoesNotAdvertiseMutationsThatWouldDiscardGlusterXattrs() {
    var descriptor = new GlusterFsFormatDescriptor();

    Assert.Multiple(() => {
      Assert.That(descriptor, Is.Not.InstanceOf<IArchiveCreatable>());
      Assert.That(descriptor, Is.Not.InstanceOf<IArchiveModifiable>());
      Assert.That(descriptor, Is.Not.InstanceOf<IArchivePurgeable>());
      Assert.That(descriptor, Is.Not.InstanceOf<IArchiveDefragmentable>());
      Assert.That(descriptor, Is.Not.InstanceOf<IArchiveShrinkable>());
      Assert.That(descriptor, Is.Not.InstanceOf<IWipeEmpty>());
      Assert.That(descriptor, Is.Not.InstanceOf<ILayoutOptimizable>());
    });
  }

  [Test, Category("HappyPath")]
  public void Metadata_DescribesPhysicalViewAndMutationBlocker() {
    using var stream = new MemoryStream(BuildXfsBrick());
    using var reader = new GlusterFsReader(stream);
    var metadata = reader.Entries.Single(entry => entry.Name == "metadata.ini");
    var text = Encoding.UTF8.GetString(reader.Extract(metadata));

    Assert.Multiple(() => {
      Assert.That(text, Does.Contain("parse_status=single-brick-read-only"));
      Assert.That(text, Does.Contain("backing_fs=xfs"));
      Assert.That(text, Does.Contain("view=physical single-brick namespace"));
      Assert.That(text, Does.Contain("xattrs=preserved in image but not interpreted"));
      Assert.That(text, Does.Contain("cluster_namespace_reconstruction=false"));
      Assert.That(text, Does.Contain("trusted.gfid/trusted.glusterfs.*"));
    });
  }
}
