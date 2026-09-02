using Compression.Registry;
using FileSystem.Erofs;

namespace Compression.Tests.Erofs;

[TestFixture]
public sealed class ErofsRwTests {
  private static readonly byte[] Alpha = "alpha payload"u8.ToArray();
  private static readonly byte[] Beta = Enumerable.Range(0, 9000).Select(i => (byte)(i * 29 + 3)).ToArray();

  private static MemoryStream CreateImage(string label = "CWB") {
    var descriptor = new ErofsFormatDescriptor();
    var stream = new MemoryStream();
    descriptor.Create(stream, [
      ArchiveInputInfo.InMemory("dir/alpha.txt", Alpha),
      ArchiveInputInfo.InMemory("beta.bin", Beta),
    ], new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["VolumeLabel"] = label },
    });
    stream.Position = 0;
    return stream;
  }

  [Test]
  public void SupportedFlatProfile_AdvertisesRwAndMaintenance() {
    var descriptor = new ErofsFormatDescriptor();
    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
    Assert.That(descriptor, Is.InstanceOf<IArchiveModifiable>());
    Assert.That(descriptor, Is.InstanceOf<IArchivePurgeable>());
    Assert.That(descriptor, Is.InstanceOf<IArchiveDefragmentable>());
    Assert.That(descriptor, Is.InstanceOf<IArchiveShrinkable>());
    Assert.That(descriptor, Is.InstanceOf<IFilesystemExtentMap>());
    Assert.That(descriptor, Is.InstanceOf<IWipeEmpty>());
  }

  [Test]
  public void Add_ReplacesExistingFile_AndPreservesVolumeLabel() {
    using var image = CreateImage("SYSTEM");
    var replacement = "replacement alpha"u8.ToArray();
    var descriptor = new ErofsFormatDescriptor();

    descriptor.Add(image, [ArchiveInputInfo.InMemory("dir/alpha.txt", replacement)]);

    image.Position = 0;
    var reader = new ErofsReader(image);
    Assert.That(reader.VolumeName, Is.EqualTo("SYSTEM"));
    var alpha = reader.Entries.Single(e => e.Path == "dir/alpha.txt");
    Assert.That(reader.ExtractFile(alpha), Is.EqualTo(replacement));
    var beta = reader.Entries.Single(e => e.Path == "beta.bin");
    Assert.That(reader.ExtractFile(beta), Is.EqualTo(Beta));
  }

  [Test]
  public void Remove_DropsFile_AndPreservesOtherPayload() {
    using var image = CreateImage();
    var descriptor = new ErofsFormatDescriptor();

    descriptor.Remove(image, ["dir/alpha.txt"]);

    image.Position = 0;
    var reader = new ErofsReader(image);
    Assert.That(reader.Entries.Any(e => e.Path == "dir/alpha.txt"), Is.False);
    var beta = reader.Entries.Single(e => e.Path == "beta.bin");
    Assert.That(reader.ExtractFile(beta), Is.EqualTo(Beta));
  }

  [Test]
  public void Purge_LeavesValidEmptyImage() {
    using var image = CreateImage("EMPTYME");
    var descriptor = new ErofsFormatDescriptor();

    ((IArchivePurgeable)descriptor).Purge(image);

    image.Position = 0;
    var reader = new ErofsReader(image);
    Assert.That(reader.VolumeName, Is.EqualTo("EMPTYME"));
    Assert.That(reader.Entries.Where(e => !e.IsDirectory), Is.Empty);
  }

  [Test]
  public void Defragment_PreservesPayloadAndEmitsProgress() {
    using var image = CreateImage();
    var descriptor = new ErofsFormatDescriptor();
    var phases = new List<string>();

    descriptor.Defragment(image, new DefragOptions { OnProgress = e => phases.Add(e.Phase) });

    image.Position = 0;
    var reader = new ErofsReader(image);
    Assert.That(reader.ExtractFile(reader.Entries.Single(e => e.Path == "dir/alpha.txt")), Is.EqualTo(Alpha));
    Assert.That(reader.ExtractFile(reader.Entries.Single(e => e.Path == "beta.bin")), Is.EqualTo(Beta));
    Assert.That(phases, Does.Contain("scanning").Or.Contain("complete"));
  }

  [Test]
  public void Wipe_IsConservativeWithoutAllocatorProof() {
    using var image = CreateImage();
    var before = image.ToArray();
    var descriptor = new ErofsFormatDescriptor();

    var wiped = descriptor.WipeUnusedSpace(image);

    Assert.That(wiped, Is.Zero);
    Assert.That(image.ToArray(), Is.EqualTo(before));
    var map = descriptor.EnumerateExtents(image).ToArray();
    Assert.That(map, Is.Not.Empty);
    Assert.That(map.Any(e => e.Kind == DefragBlockKind.Free), Is.False,
      "Unproven EROFS bytes must be reserved, never inferred free.");
  }
}
