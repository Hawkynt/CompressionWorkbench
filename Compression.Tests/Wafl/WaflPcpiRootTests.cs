using System.Buffers.Binary;
using System.Text;
using FileSystem.Wafl;

namespace Compression.Tests.Wafl;

[TestFixture]
public class WaflPcpiRootTests {
  private const int BlockSize = 4096;
  private const int VolInfoMagicOffset = 20;
  private const int VbnArrayOffset = 256;
  private const uint VolInfoMagic = 0xDAB8FBAB;
  private const uint FsInfoMagic = 0xF51F0001;
  private const uint FsInfoVersion = 4;

  [Test, Category("HappyPath")]
  public void Reader_ClassifiesPcpiSlotsAndUnionsRedundantVolInfoCopies() {
    var image = BuildImage(firstPcpiVbn: 6, secondPcpiVbn: 7);
    using var stream = new MemoryStream(image, writable: false);
    using var reader = new WaflReader(stream);

    Assert.Multiple(() => {
      Assert.That(reader.Stage, Is.EqualTo(1));
      Assert.That(reader.ActiveFsInfoVbn, Is.EqualTo(4u));
      Assert.That(reader.FsInfoVbns, Is.EqualTo(new uint[] { 4, 6, 7 }));
      Assert.That(reader.RetainedFsInfoRoots, Is.EqualTo(new[] {
        new WaflPcpiRoot(1, 6),
        new WaflPcpiRoot(1, 7),
      }));
    });

    var metadata = Encoding.UTF8.GetString(reader.Entries.Single(entry => entry.Name == "metadata.ini").Data);
    Assert.Multiple(() => {
      Assert.That(metadata, Does.Contain("retained_fsinfo_root_count=2"));
      Assert.That(metadata, Does.Contain("retained_fsinfo_roots=1:6,1:7"));
      Assert.That(metadata, Does.Contain("volinfo_vbn1_verified_pcpi_references=1"));
      Assert.That(metadata, Does.Contain("volinfo_vbn2_verified_pcpi_references=1"));
    });
  }

  [Test, Category("HappyPath")]
  public void Reader_DeduplicatesIdenticalRetainedPcpiRootsAcrossRedundantCopies() {
    var image = BuildImage(firstPcpiVbn: 6, secondPcpiVbn: 6);
    using var stream = new MemoryStream(image, writable: false);
    using var reader = new WaflReader(stream);

    Assert.That(reader.RetainedFsInfoRoots, Is.EqualTo(new[] { new WaflPcpiRoot(1, 6) }));
  }

  private static byte[] BuildImage(uint firstPcpiVbn, uint secondPcpiVbn) {
    var image = new byte[BlockSize * 8];
    WriteVolInfo(image.AsSpan(BlockSize, BlockSize), activeVbn: 4, pcpiVbn: firstPcpiVbn);
    WriteVolInfo(image.AsSpan(BlockSize * 2, BlockSize), activeVbn: 4, pcpiVbn: secondPcpiVbn);

    foreach (var vbn in new[] { 4u, firstPcpiVbn, secondPcpiVbn }.Distinct())
      WriteFsInfo(image.AsSpan(checked((int)vbn * BlockSize), BlockSize));

    return image;
  }

  private static void WriteVolInfo(Span<byte> block, uint activeVbn, uint pcpiVbn) {
    BinaryPrimitives.WriteUInt32BigEndian(block[..4], FsInfoMagic);
    BinaryPrimitives.WriteUInt32BigEndian(block.Slice(4, 4), FsInfoVersion);
    BinaryPrimitives.WriteUInt32BigEndian(block.Slice(VolInfoMagicOffset, 4), VolInfoMagic);
    BinaryPrimitives.WriteUInt32BigEndian(block.Slice(VolInfoMagicOffset + 4, 4), 4);
    BinaryPrimitives.WriteUInt32BigEndian(block.Slice(VbnArrayOffset, 4), activeVbn);
    BinaryPrimitives.WriteUInt32BigEndian(block.Slice(VbnArrayOffset + 4, 4), pcpiVbn);
    BinaryPrimitives.WriteUInt32BigEndian(block.Slice(VbnArrayOffset + 8, 4), 0);
  }

  private static void WriteFsInfo(Span<byte> block) {
    BinaryPrimitives.WriteUInt32BigEndian(block[..4], FsInfoMagic);
    BinaryPrimitives.WriteUInt32BigEndian(block.Slice(4, 4), FsInfoVersion);
  }
}
