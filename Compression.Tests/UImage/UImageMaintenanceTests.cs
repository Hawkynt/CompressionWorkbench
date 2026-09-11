using System.Text;
using Compression.Registry;
using FileFormat.UImage;

namespace Compression.Tests.UImage;

[TestFixture]
public sealed class UImageMaintenanceTests {

  [Test, Category("HappyPath")]
  public void Writer_PreservesFullThirtyTwoByteName() {
    const string name = "12345678901234567890123456789012";
    Assert.That(name.Length, Is.EqualTo(UImageReader.NameLength));

    var bytes = CreateImage("payload"u8.ToArray(), name: name);
    var image = UImageReader.Read(bytes);

    Assert.Multiple(() => {
      Assert.That(image.Name, Is.EqualTo(name));
      Assert.That(image.Header.AsSpan(32, UImageReader.NameLength).ToArray(),
        Is.EqualTo(Encoding.ASCII.GetBytes(name)));
      Assert.That(image.HeaderCrc, Is.EqualTo(image.ComputedHeaderCrc));
    });
  }

  [Test, Category("HappyPath")]
  public void Writer_MatchesKnownAnswerVector() {
    var payload = "hello uImage\n"u8.ToArray();
    using var stream = new MemoryStream();
    UImageWriter.Write(stream, new UImageWriter.Header(
      Timestamp: 0x12345678,
      LoadAddress: 0x80008000,
      EntryPoint: 0x80008040,
      Os: 5,
      Architecture: 2,
      Type: 2,
      Compression: 0,
      Name: "known-answer"), payload);

    var expected = Convert.FromHexString(
      "2705195621C0144E123456780000000D8000800080008040C134B46005020200" +
      "6B6E6F776E2D616E737765720000000000000000000000000000000000000000" +
      "68656C6C6F2075496D6167650A");

    Assert.That(stream.ToArray(), Is.EqualTo(expected));
  }

  [Test, Category("EdgeCase")]
  public void Reader_RejectsTruncatedDeclaredPayload() {
    var bytes = CreateImage(new byte[17]);
    Assert.That(() => UImageReader.Read(bytes[..^1]), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesRwAndMaintenanceContracts() {
    var descriptor = new UImageFormatDescriptor();

    Assert.Multiple(() => {
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
      Assert.That(descriptor, Is.InstanceOf<IArchiveModifiable>());
      Assert.That(descriptor, Is.InstanceOf<IArchivePurgeable>());
      Assert.That(descriptor, Is.Not.InstanceOf<IArchiveDefragmentable>());
      Assert.That(descriptor, Is.InstanceOf<IArchiveShrinkable>());
      Assert.That(descriptor, Is.InstanceOf<IArchiveLayoutMap>());
      Assert.That(descriptor, Is.InstanceOf<IWipeEmpty>());
      Assert.That(descriptor, Is.InstanceOf<ISyntheticEntryNames>());
    });
  }

  [Test, Category("RoundTrip")]
  public void Add_ReplacesPayloadAndMetadata_PreservingDeadTrailer() {
    var descriptor = new UImageFormatDescriptor();
    var originalBody = "old payload"u8.ToArray();
    byte[] trailer = [0xFA, 0xCE, 0xB0, 0x0C];
    using var image = Expandable(CreateImage(originalBody, name: "old"), trailer);
    var replacement = Enumerable.Range(0, 257).Select(i => (byte)(i * 13)).ToArray();

    descriptor.Add(image, [
      ArchiveInputInfo.InMemory(UImageWriter.MetadataName,
        "[uimage]\nname = replacement\nload_address = 0x81234567\nentry_point = 0x81234589\narch = 22 (ARM64)\ncomp = 0 (none)\n"u8.ToArray()),
      ArchiveInputInfo.InMemory(UImageWriter.PayloadName, replacement),
    ]);

    var bytes = image.ToArray();
    var parsed = UImageReader.Read(bytes);
    Assert.Multiple(() => {
      Assert.That(parsed.Name, Is.EqualTo("replacement"));
      Assert.That(parsed.LoadAddress, Is.EqualTo(0x81234567u));
      Assert.That(parsed.EntryPoint, Is.EqualTo(0x81234589u));
      Assert.That(parsed.Architecture, Is.EqualTo(22));
      Assert.That(parsed.Body, Is.EqualTo(replacement));
      Assert.That(parsed.HeaderCrc, Is.EqualTo(parsed.ComputedHeaderCrc));
      Assert.That(parsed.DataCrc, Is.EqualTo(parsed.ComputedDataCrc));
      Assert.That(bytes[^trailer.Length..], Is.EqualTo(trailer));
    });
  }

  [Test, Category("HappyPath")]
  public void Purge_RemovesLivePayloadButPreservesOuterSizeUntilWipe() {
    var descriptor = new UImageFormatDescriptor();
    var originalBody = Enumerable.Range(1, 97).Select(i => (byte)i).ToArray();
    byte[] trailer = [0xDE, 0xAD, 0xBE, 0xEF];
    using var image = Expandable(CreateImage(originalBody, name: "purge-me"), trailer);
    var original = image.ToArray();

    descriptor.Purge(image);

    var purged = image.ToArray();
    var parsed = UImageReader.Read(purged);
    Assert.Multiple(() => {
      Assert.That(purged.Length, Is.EqualTo(original.Length));
      Assert.That(parsed.DataSize, Is.Zero);
      Assert.That(parsed.Body, Is.Empty);
      Assert.That(parsed.HeaderCrc, Is.EqualTo(parsed.ComputedHeaderCrc));
      Assert.That(parsed.DataCrc, Is.EqualTo(parsed.ComputedDataCrc));
      Assert.That(purged[UImageReader.HeaderSize..],
        Is.EqualTo(original[UImageReader.HeaderSize..]),
        "purge removes the live member logically; wipe owns forensic clearing of the resulting dead bytes");
    });

    var wiped = ((IWipeEmpty)descriptor).WipeUnusedSpace(image);
    var afterWipe = image.ToArray();
    Assert.Multiple(() => {
      Assert.That(wiped, Is.EqualTo(original.Length - UImageReader.HeaderSize));
      Assert.That(afterWipe.AsSpan(UImageReader.HeaderSize).ToArray(),
        Is.All.EqualTo((byte)0));
      Assert.That(UImageReader.Read(afterWipe).HeaderCrc,
        Is.EqualTo(UImageReader.Read(afterWipe).ComputedHeaderCrc));
    });
  }

  [Test, Category("HappyPath")]
  public void RemovePayload_IsPurge() {
    var descriptor = new UImageFormatDescriptor();
    using var image = Expandable(CreateImage("remove me"u8.ToArray()));
    var length = image.Length;

    descriptor.Remove(image, [UImageWriter.PayloadName]);

    var parsed = UImageReader.Read(image.ToArray());
    Assert.Multiple(() => {
      Assert.That(image.Length, Is.EqualTo(length));
      Assert.That(parsed.DataSize, Is.Zero);
      Assert.That(parsed.Body, Is.Empty);
    });
  }

  [Test, Category("HappyPath")]
  public void Layout_MapsHeaderPayloadAndDeadTrailer() {
    var descriptor = new UImageFormatDescriptor();
    var body = new byte[23];
    byte[] trailer = [1, 2, 3, 4, 5];
    using var image = Expandable(CreateImage(body), trailer);

    var layout = descriptor.EnumerateLayout(image).ToArray();

    Assert.Multiple(() => {
      Assert.That(layout, Has.Length.EqualTo(3));
      Assert.That(layout[0], Is.EqualTo(new DefragBlockInfo(0, 64, DefragBlockKind.MetadataReserved, UImageWriter.HeaderName)));
      Assert.That(layout[1], Is.EqualTo(new DefragBlockInfo(64, body.Length, DefragBlockKind.Used, UImageWriter.PayloadName)));
      Assert.That(layout[2], Is.EqualTo(new DefragBlockInfo(64 + body.Length, trailer.Length, DefragBlockKind.Free, "trailing bytes")));
    });
  }

  [Test, Category("HappyPath")]
  public void Shrink_DropsOnlyBytesBeyondDeclaredPayload() {
    var descriptor = new UImageFormatDescriptor();
    var body = Enumerable.Range(0, 101).Select(i => (byte)(255 - i)).ToArray();
    byte[] trailer = [0x10, 0x20, 0x30, 0x40, 0x50, 0x60];
    var canonical = CreateImage(body, name: "tight");
    using var input = Expandable(canonical, trailer);
    using var output = new MemoryStream();

    descriptor.Shrink(input, output);

    Assert.Multiple(() => {
      Assert.That(output.Length, Is.EqualTo(canonical.Length));
      Assert.That(output.ToArray(), Is.EqualTo(canonical));
      Assert.That(UImageReader.Read(output.ToArray()).Body, Is.EqualTo(body));
    });
  }

  [Test, Category("EdgeCase")]
  public void LayoutAndWipe_FailClosedOnBadDataCrc() {
    var descriptor = new UImageFormatDescriptor();
    var bytes = CreateImage(new byte[32]);
    bytes[UImageReader.HeaderSize + 3] ^= 0x80;
    using var image = Expandable(bytes, [0xAA, 0xBB, 0xCC]);
    var before = image.ToArray();

    var layout = descriptor.EnumerateLayout(image).ToArray();
    var wiped = ((IWipeEmpty)descriptor).WipeUnusedSpace(image);

    Assert.Multiple(() => {
      Assert.That(layout, Is.Empty);
      Assert.That(wiped, Is.Zero);
      Assert.That(image.ToArray(), Is.EqualTo(before));
    });
  }

  [Test, Category("EdgeCase")]
  public void Shrink_CopiesCorruptImageThroughUnchanged() {
    var descriptor = new UImageFormatDescriptor();
    var bytes = CreateImage(new byte[16]);
    bytes[UImageReader.HeaderSize] ^= 0x01;
    byte[] trailer = [0x44, 0x55];
    using var input = Expandable(bytes, trailer);
    using var output = new MemoryStream();

    descriptor.Shrink(input, output);

    Assert.That(output.ToArray(), Is.EqualTo(bytes.Concat(trailer).ToArray()));
  }

  private static byte[] CreateImage(byte[] body, string name = "test", byte comp = 0) {
    using var stream = new MemoryStream();
    UImageWriter.Write(stream, new UImageWriter.Header(
      Timestamp: 0x12345678,
      LoadAddress: 0x80008000,
      EntryPoint: 0x80008040,
      Os: 5,
      Architecture: 2,
      Type: 2,
      Compression: comp,
      Name: name), body);
    return stream.ToArray();
  }

  private static MemoryStream Expandable(byte[] image, byte[]? trailer = null) {
    var stream = new MemoryStream();
    stream.Write(image);
    if (trailer is { Length: > 0 }) stream.Write(trailer);
    stream.Position = 0;
    return stream;
  }
}
