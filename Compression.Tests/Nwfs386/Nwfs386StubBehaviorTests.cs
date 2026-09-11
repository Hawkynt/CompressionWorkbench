#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using FileSystem.Nwfs;
using FileSystem.Nwfs386;

namespace Compression.Tests.Nwfs386;

[TestFixture]
public class Nwfs386BehaviorTests {

  private static readonly byte[] Hello = "hello nwfs386"u8.ToArray();
  private static readonly byte[] Readme = Enumerable.Range(0, 5000).Select(i => (byte)(i * 31)).ToArray();

  private static MemoryStream CreateImage(int blockSize = 4096) {
    var image = new MemoryStream();
    new Nwfs386FormatDescriptor().Create(
      image,
      [
        ArchiveInputInfo.InMemory("HELLO.TXT", Hello),
        ArchiveInputInfo.InMemory("PUBLIC/README.DOC", Readme),
      ],
      new FormatCreateOptions {
        FormatSpecific = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
          ["BlockSize"] = blockSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
          ["VolumeName"] = "SYS",
        },
      });
    image.Position = 0;
    return image;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesTheBackedRwSurface() {
    var descriptor = new Nwfs386FormatDescriptor();

    Assert.Multiple(() => {
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
      Assert.That(descriptor, Is.InstanceOf<IArchiveCreatable>());
      Assert.That(descriptor, Is.InstanceOf<IArchiveModifiable>());
      Assert.That(descriptor, Is.InstanceOf<IArchiveDefragmentable>());
      Assert.That(descriptor, Is.InstanceOf<IArchiveShrinkable>());
      Assert.That(descriptor, Is.InstanceOf<ILayoutOptimizable>());
      Assert.That(descriptor, Is.Not.InstanceOf<IWipeEmpty>(),
        "NWFS386 must not claim wipe until all free/salvage/suballocation regions can be proven safely.");
    });
  }

  [Test, Category("HappyPath")]
  public void Create_ListAndExtract_RoundTripFilesAndDirectories() {
    using var image = CreateImage();
    var descriptor = new Nwfs386FormatDescriptor();

    var entries = descriptor.List(image, null);

    Assert.That(entries.Select(entry => entry.Name), Does.Contain("HELLO.TXT"));
    Assert.That(entries.Select(entry => entry.Name), Does.Contain("PUBLIC"));
    Assert.That(entries.Select(entry => entry.Name), Does.Contain("PUBLIC/README.DOC"));

    image.Position = 0;
    using var opened = descriptor.OpenEntry(image, "PUBLIC/README.DOC", null);
    using var copied = new MemoryStream();
    opened.CopyTo(copied);
    Assert.That(copied.ToArray(), Is.EqualTo(Readme));
  }

  [Test, Category("HappyPath")]
  public void AddReplaceRemove_PreservesGeometryAndLiveBytes() {
    using var image = CreateImage(8192);
    var descriptor = new Nwfs386FormatDescriptor();
    var replacement = "replacement"u8.ToArray();
    var added = "new file"u8.ToArray();

    descriptor.Add(image, [
      ArchiveInputInfo.InMemory("HELLO.TXT", replacement),
      ArchiveInputInfo.InMemory("NEW.BIN", added),
    ]);
    descriptor.Remove(image, ["PUBLIC/README.DOC"]);

    var volume = NwfsReader.TryOpen(image.ToArray());
    Assert.That(volume, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(volume!.BlockSize, Is.EqualTo(8192));
      Assert.That(volume.VolumeName, Is.EqualTo("SYS"));
      Assert.That(volume.ReadFile("HELLO.TXT"), Is.EqualTo(replacement));
      Assert.That(volume.ReadFile("NEW.BIN"), Is.EqualTo(added));
      Assert.That(volume.ReadFile("PUBLIC/README.DOC"), Is.Null);
    });
  }

  [Test, Category("HappyPath")]
  public void Purge_LeavesAValidEmptyVolume() {
    using var image = CreateImage();
    var descriptor = new Nwfs386FormatDescriptor();

    ((IArchivePurgeable)descriptor).Purge(image);

    image.Position = 0;
    Assert.That(descriptor.List(image, null).Where(entry => !entry.IsDirectory), Is.Empty);
    Assert.That(NwfsReader.TryOpen(image.ToArray()), Is.Not.Null);
  }

  [Test, Category("HappyPath")]
  public void DefragShrinkAndLayout_PreserveContent() {
    using var image = CreateImage(4096);
    var descriptor = new Nwfs386FormatDescriptor();

    descriptor.Defragment(image);
    var defragged = NwfsReader.TryOpen(image.ToArray())!;
    Assert.That(defragged.ReadFile("PUBLIC/README.DOC"), Is.EqualTo(Readme));

    var padded = image.ToArray().Concat(new byte[128 * 1024]).ToArray();
    using var paddedStream = new MemoryStream(padded, writable: false);
    using var shrunk = new MemoryStream();
    descriptor.Shrink(paddedStream, shrunk);
    Assert.That(shrunk.Length, Is.LessThan(padded.LongLength));
    Assert.That(NwfsReader.TryOpen(shrunk.ToArray())!.ReadFile("HELLO.TXT"), Is.EqualTo(Hello));

    image.Position = 0;
    using var relaid = new MemoryStream();
    descriptor.RebuildStreaming(image, relaid, new LayoutRebuildOptions { UnitSize = 8192 });
    var layoutVolume = NwfsReader.TryOpen(relaid.ToArray())!;
    Assert.Multiple(() => {
      Assert.That(layoutVolume.BlockSize, Is.EqualTo(8192));
      Assert.That(layoutVolume.ReadFile("HELLO.TXT"), Is.EqualTo(Hello));
      Assert.That(layoutVolume.ReadFile("PUBLIC/README.DOC"), Is.EqualTo(Readme));
    });
  }

  [Test, Category("HappyPath")]
  public void AnalyzeLayout_ReportsCurrentAndCandidateBlockSize() {
    using var image = CreateImage(16384);
    var descriptor = new Nwfs386FormatDescriptor();

    var analysis = descriptor.AnalyzeLayout(image);

    Assert.That(analysis.CurrentUnitSize, Is.EqualTo(16384));
    Assert.That(analysis.OptimalUnitSize, Is.AnyOf(1024, 2048, 4096, 8192, 16384, 32768, 65536, 131072, 262144));
    Assert.That(analysis.OptimalSlackBytes, Is.LessThanOrEqualTo(analysis.CurrentSlackBytes));
  }

  [Test, Category("ErrorHandling")]
  public void Mutation_RejectsCompressionProfileBeforeChangingSource() {
    using var image = CreateImage();
    var bytes = image.ToArray();
    var volumeArea = FindVolumeArea(bytes);
    var entry = bytes.AsSpan(volumeArea + 32, 60);
    var blockValue = BinaryPrimitives.ReadUInt32LittleEndian(entry[44..]);
    var blockSize = checked((int)(256u * 1024u / blockValue));
    var firstDirectory = BinaryPrimitives.ReadUInt32LittleEndian(entry[48..]);
    var dataArea = volumeArea + 4 * 16384;
    var root = checked(dataArea + (int)firstDirectory * blockSize);
    bytes[root + 23] |= 0x04; // FILE_COMPRESSION_ON in the ROOT volume flags.

    using var advanced = new MemoryStream(bytes, writable: true);
    var before = advanced.ToArray();
    var descriptor = new Nwfs386FormatDescriptor();

    Assert.Throws<NotSupportedException>(() => descriptor.Add(
      advanced,
      [ArchiveInputInfo.InMemory("NEW.BIN", "nope"u8.ToArray())]));
    Assert.That(advanced.ToArray(), Is.EqualTo(before), "Rejected mutation must leave the source byte-identical.");
  }

  private static int FindVolumeArea(byte[] image) {
    var magic = "NetWare Volumes\0"u8;
    for (var offset = 0; offset + magic.Length <= image.Length; offset += 512)
      if (image.AsSpan(offset, magic.Length).SequenceEqual(magic))
        return offset;
    throw new InvalidOperationException("Test image contains no NWFS volume table.");
  }
}
