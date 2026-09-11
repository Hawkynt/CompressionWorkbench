using Compression.Registry;
using FileSystem.Nwfs;

namespace Compression.Tests.Nwfs;

[TestFixture]
public sealed class NwfsMaintenanceTests {

  private static byte[] Bytes(int length, int seed) {
    var data = new byte[length];
    new Random(seed).NextBytes(data);
    return data;
  }

  private static MemoryStream CreatePaddedVolume() {
    var writer = new NwfsWriter { MinimumImageSize = 512 * 1024 };
    writer.AddDirectory("EMPTY");
    writer.AddFile("HELLO.TXT", Bytes(6000, 1));
    writer.AddFile("DOCS/NOTE.TXT", Bytes(700, 2));
    return new MemoryStream(writer.Build(), writable: true);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesTheWritableMaintenanceProfile() {
    var descriptor = new NwfsFormatDescriptor();

    Assert.Multiple(() => {
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
      Assert.That(descriptor, Is.InstanceOf<IArchiveModifiable>());
      Assert.That(descriptor, Is.InstanceOf<IArchiveDefragmentable>());
      Assert.That(descriptor, Is.InstanceOf<IArchiveShrinkable>());
      Assert.That(descriptor, Is.InstanceOf<IFilesystemExtentMap>());
      Assert.That(descriptor, Is.InstanceOf<IWipeEmpty>());
      Assert.That(descriptor, Is.InstanceOf<ILayoutOptimizable>());
      Assert.That(descriptor, Is.InstanceOf<IArchivePurgeable>());
    });
  }

  [Test, Category("HappyPath")]
  public void Create_PreservesFilesAndExplicitEmptyDirectories() {
    var payload = Bytes(3000, 3);
    var descriptor = new NwfsFormatDescriptor();
    using var image = new MemoryStream();

    descriptor.Create(image, [
      ArchiveInputInfo.InMemory("DOCS/README.TXT", payload),
      new ArchiveInputInfo("", "EMPTY/", true),
    ], new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
        ["BlockSize"] = "8 KB",
        ["VolumeLabel"] = "DATA",
      },
    });

    var volume = NwfsReader.TryOpen(image.ToArray());
    Assert.That(volume, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(volume!.VolumeName, Is.EqualTo("DATA"));
      Assert.That(volume.BlockSize, Is.EqualTo(8192));
      Assert.That(volume.ReadFile("DOCS/README.TXT"), Is.EqualTo(payload));
      Assert.That(volume.List().Where(static i => i.IsDirectory).Select(static i => i.Path),
                  Does.Contain("EMPTY"));
    });
  }

  [Test, Category("HappyPath")]
  public void Modify_AddReplaceAndRemove_RoundTripsTheNamespace() {
    var descriptor = new NwfsFormatDescriptor();
    using var image = CreatePaddedVolume();
    var originalLength = image.Length;
    var replacement = Bytes(9000, 4);
    var added = Bytes(37, 5);

    descriptor.Add(image, [
      ArchiveInputInfo.InMemory("HELLO.TXT", replacement),
      ArchiveInputInfo.InMemory("NEW.BIN", added),
    ]);

    var modified = NwfsReader.TryOpen(image.ToArray())!;
    Assert.Multiple(() => {
      Assert.That(modified.ReadFile("HELLO.TXT"), Is.EqualTo(replacement));
      Assert.That(modified.ReadFile("NEW.BIN"), Is.EqualTo(added));
      Assert.That(modified.ReadFile("DOCS/NOTE.TXT"), Is.Not.Null);
      Assert.That(image.Length, Is.GreaterThanOrEqualTo(originalLength));
    });

    descriptor.Remove(image, ["DOCS"]);
    var removed = NwfsReader.TryOpen(image.ToArray())!;
    Assert.Multiple(() => {
      Assert.That(removed.ReadFile("DOCS/NOTE.TXT"), Is.Null);
      Assert.That(removed.List().Select(static i => i.Path), Does.Not.Contain("DOCS"));
      Assert.That(removed.ReadFile("HELLO.TXT"), Is.EqualTo(replacement));
      Assert.That(removed.ReadFile("NEW.BIN"), Is.EqualTo(added));
    });
  }

  [Test, Category("HappyPath")]
  public void Purge_LeavesAValidEmptyVolume() {
    var descriptor = new NwfsFormatDescriptor();
    using var image = CreatePaddedVolume();

    ((IArchivePurgeable)descriptor).Purge(image);

    var volume = NwfsReader.TryOpen(image.ToArray());
    Assert.That(volume, Is.Not.Null);
    Assert.That(volume!.List(), Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void Defragment_PreservesPayloadAndCapacity() {
    var descriptor = new NwfsFormatDescriptor();
    using var image = CreatePaddedVolume();
    var expectedLength = image.Length;
    var hello = NwfsReader.TryOpen(image.ToArray())!.ReadFile("HELLO.TXT");

    descriptor.Defragment(image);

    var volume = NwfsReader.TryOpen(image.ToArray())!;
    Assert.Multiple(() => {
      Assert.That(image.Length, Is.EqualTo(expectedLength));
      Assert.That(volume.ReadFile("HELLO.TXT"), Is.EqualTo(hello));
      Assert.That(volume.List().Where(static i => i.IsDirectory).Select(static i => i.Path),
                  Does.Contain("EMPTY"));
    });
  }

  [Test, Category("HappyPath")]
  public void Shrink_DropsReservedFreeTailWithoutChangingFiles() {
    var descriptor = new NwfsFormatDescriptor();
    using var source = CreatePaddedVolume();
    var sourceLength = source.Length;
    using var target = new MemoryStream();

    descriptor.Shrink(source, target);

    var volume = NwfsReader.TryOpen(target.ToArray());
    Assert.That(volume, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(target.Length, Is.LessThan(sourceLength));
      Assert.That(volume!.ReadFile("HELLO.TXT"), Is.EqualTo(Bytes(6000, 1)));
      Assert.That(volume.ReadFile("DOCS/NOTE.TXT"), Is.EqualTo(Bytes(700, 2)));
      Assert.That(volume.List().Where(static i => i.IsDirectory).Select(static i => i.Path),
                  Does.Contain("EMPTY"));
    });
  }

  [Test, Category("HappyPath")]
  public void Layout_RebuildsWithRequestedBlockSize() {
    var descriptor = new NwfsFormatDescriptor();
    using var source = CreatePaddedVolume();
    using var target = new MemoryStream();

    descriptor.RebuildStreaming(source, target, new LayoutRebuildOptions { UnitSize = 8192 });

    var volume = NwfsReader.TryOpen(target.ToArray());
    Assert.That(volume, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(volume!.BlockSize, Is.EqualTo(8192));
      Assert.That(volume.ReadFile("HELLO.TXT"), Is.EqualTo(Bytes(6000, 1)));
      Assert.That(volume.ReadFile("DOCS/NOTE.TXT"), Is.EqualTo(Bytes(700, 2)));
    });
  }

  [Test, Category("HappyPath")]
  public void Wipe_ZeroesDeclaredFreeBlocksAndPreservesLivePayload() {
    var descriptor = new NwfsFormatDescriptor();
    using var image = CreatePaddedVolume();
    var free = descriptor.EnumerateExtents(image)
      .First(static e => e.Kind == DefragBlockKind.Free && e.Length >= 32);
    var dirtLength = (int)Math.Min(128, free.Length);
    var dirt = Enumerable.Repeat((byte)0xA5, dirtLength).ToArray();
    image.Position = free.Offset;
    image.Write(dirt);
    image.Position = 0;

    var wiped = ((IWipeEmpty)descriptor).WipeUnusedSpace(image);

    var check = new byte[dirtLength];
    image.Position = free.Offset;
    image.ReadExactly(check);
    var volume = NwfsReader.TryOpen(image.ToArray())!;
    Assert.Multiple(() => {
      Assert.That(wiped, Is.GreaterThanOrEqualTo(dirtLength));
      Assert.That(check, Is.All.Zero);
      Assert.That(volume.ReadFile("HELLO.TXT"), Is.EqualTo(Bytes(6000, 1)));
      Assert.That(volume.ReadFile("DOCS/NOTE.TXT"), Is.EqualTo(Bytes(700, 2)));
    });
  }

  [Test, Category("ErrorHandling")]
  public void ExtentMap_FailsClosedForUnsupportedInput() {
    var descriptor = new NwfsFormatDescriptor();
    using var garbage = new MemoryStream(Bytes(64 * 1024, 6));

    Assert.That(descriptor.EnumerateExtents(garbage), Is.Empty);
  }
}
