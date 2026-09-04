#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.AppleSingle;

namespace Compression.Tests.AppleSingle;

/// <summary>
/// AppleDouble is the same RFC 1740 body as AppleSingle under a different leading magic, and the
/// reader has always accepted both. Only the writer was single-only, so the sidecar half of a
/// format the library fully understands could be read and never written. These tests pin the
/// container it now produces, and the one entry it must refuse.
/// </summary>
[TestFixture]
public sealed class AppleDoubleWriterTests {

  private static readonly byte[] Resource = "resource fork bytes"u8.ToArray();
  private static readonly byte[] Comment = "a Finder comment"u8.ToArray();

  private static AppleDoubleFormatDescriptor Descriptor => new();

  [Test, Category("HappyPath")]
  public void DescriptorAdvertisesCreateAndModify() {
    var d = Descriptor;
    Assert.Multiple(() => {
      Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
      Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
      Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
      Assert.That(d, Is.InstanceOf<IArchiveModifiable>());
    });
  }

  [Test, Category("HappyPath")]
  public void CreateEmitsTheSidecarMagicNotTheSingleOne() {
    using var ms = new MemoryStream();
    Descriptor.Create(ms, [ArchiveInputInfo.InMemory("resource_fork.bin", Resource)], new FormatCreateOptions());

    var bytes = ms.ToArray();
    var magic = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes);
    Assert.That(magic, Is.EqualTo(AppleSingleReader.MagicDouble),
      "an AppleDouble container written under the AppleSingle magic is an AppleSingle container");
    Assert.That(AppleSingleReader.Read(bytes).IsDouble, Is.True);
  }

  [Test, Category("RoundTrip")]
  public void CreateRoundTripsThroughTheSharedReader() {
    using var ms = new MemoryStream();
    Descriptor.Create(ms, [
      ArchiveInputInfo.InMemory("resource_fork.bin", Resource),
      ArchiveInputInfo.InMemory("comment.txt", Comment),
    ], new FormatCreateOptions());

    ms.Position = 0;
    var listed = Descriptor.List(ms, null);
    // metadata.ini is synthetic and always leads the listing.
    Assert.That(listed.Select(e => e.Name),
      Is.EquivalentTo(new[] { "metadata.ini", "resource_fork.bin", "comment.txt" }));

    Assert.Multiple(() => {
      Assert.That(Descriptor.ExtractEntryToMemory(ms, "resource_fork.bin", null), Is.EqualTo(Resource));
      Assert.That(Descriptor.ExtractEntryToMemory(ms, "comment.txt", null), Is.EqualTo(Comment));
    });
  }

  [Test, Category("HappyPath")]
  public void TheSyntheticMetadataEntryIsNotWrittenBack() {
    using var ms = new MemoryStream();
    Descriptor.Create(ms, [
      ArchiveInputInfo.InMemory("metadata.ini", "[applesingle]\n"u8.ToArray()),
      ArchiveInputInfo.InMemory("resource_fork.bin", Resource),
    ], new FormatCreateOptions());

    var container = AppleSingleReader.Read(ms.ToArray());
    Assert.That(container.Entries, Has.Count.EqualTo(1),
      "metadata.ini is a view the descriptor synthesises on read, not an entry in the container");
    Assert.That(container.Entries[0].Name, Is.EqualTo("resource_fork.bin"));
  }

  [Test, Category("Sad")]
  public void ADataForkIsRefusedRatherThanWritten() {
    using var ms = new MemoryStream();
    var ex = Assert.Throws<NotSupportedException>(() =>
      Descriptor.Create(ms, [ArchiveInputInfo.InMemory("data_fork.bin", "payload"u8.ToArray())],
        new FormatCreateOptions()));
    Assert.That(ex!.Message, Does.Contain("sibling file"),
      "the refusal has to say where the data fork does belong");
  }

  [Test, Category("RoundTrip")]
  public void ModifyingAContainerKeepsItAnAppleDouble() {
    using var ms = new MemoryStream();
    Descriptor.Create(ms, [ArchiveInputInfo.InMemory("resource_fork.bin", Resource)], new FormatCreateOptions());

    Descriptor.Add(ms, [ArchiveInputInfo.InMemory("comment.txt", Comment)]);
    ms.Position = 0;
    Assert.That(AppleSingleReader.Read(ms.ToArray()).IsDouble, Is.True,
      "an in-place edit must not rewrite the container's magic");
    Assert.That(Descriptor.ExtractEntryToMemory(ms, "comment.txt", null), Is.EqualTo(Comment));
    Assert.That(Descriptor.ExtractEntryToMemory(ms, "resource_fork.bin", null), Is.EqualTo(Resource),
      "the untouched entry's payload must survive the edit");

    Descriptor.Remove(ms, ["comment.txt"]);
    ms.Position = 0;
    var remaining = Descriptor.List(ms, null).Select(e => e.Name).ToArray();
    Assert.That(remaining, Does.Not.Contain("comment.txt"));
    Assert.That(remaining, Does.Contain("resource_fork.bin"));
    Assert.That(AppleSingleReader.Read(ms.ToArray()).IsDouble, Is.True);
  }

  [Test, Category("HappyPath")]
  public void TheSingleWriterKeepsItsOwnMagicThroughTheNewOverload() {
    var bytes = AppleSingleWriter.Build([(2u, Resource)]);
    Assert.That(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes),
      Is.EqualTo(AppleSingleReader.MagicSingle),
      "the one-argument Build is what every existing caller uses; it must still write AppleSingle");
  }
}
