using System.Text;
using Compression.Registry;
using FileFormat.FirmwareHex;

namespace Compression.Tests.FirmwareHex;

[TestFixture]
public sealed class TiTxtMaintenanceTests {

  [Test]
  public void Parser_RejectsOverlappingSections() {
    const string text = "@1000\n01 02 03 04\n@1002\nAA BB\nq\n";
    Assert.That(() => TiTxtReader.Read(text), Throws.InstanceOf<InvalidDataException>());
  }

  [Test]
  public void Parser_RejectsTrailingContentAfterTerminator() {
    const string text = "@1000\n01 02\nq\n@2000\n03 04\n";
    Assert.That(() => TiTxtReader.Read(text), Throws.InstanceOf<InvalidDataException>());
  }

  [Test]
  public void Defragment_CanonicalisesAdjacentSectionsWithoutFillingHoles() {
    const string text = "@1010\n05 06\n@1004\n03 04\n@1000\n01 02 07 08\nq\n";
    using var image = StreamOf(text);
    var descriptor = new TiTxtFormatDescriptor();

    descriptor.Defragment(image);

    var rewritten = Encoding.ASCII.GetString(image.ToArray());
    var parsed = TiTxtReader.Read(rewritten);
    Assert.Multiple(() => {
      Assert.That(parsed.Segments, Has.Count.EqualTo(2));
      Assert.That(parsed.Segments[0].Address, Is.EqualTo(0x1000u));
      Assert.That(parsed.Segments[0].Data, Is.EqualTo(new byte[] { 1, 2, 7, 8, 3, 4 }).AsCollection);
      Assert.That(parsed.Segments[1].Address, Is.EqualTo(0x1010u));
      Assert.That(parsed.Segments[1].Data, Is.EqualTo(new byte[] { 5, 6 }).AsCollection);
      Assert.That(rewritten, Does.Not.Contain("@1004"));
      Assert.That(rewritten, Does.Contain("@1010"));
    });
  }

  [Test]
  public void Add_ReplacesPayloadAtExistingBaseAddress() {
    using var image = StreamOf("@C000\n01 02 03\nq\n");
    var descriptor = new TiTxtFormatDescriptor();

    descriptor.Add(image, [ArchiveInputInfo.InMemory("replacement.bin", new byte[] { 0xDE, 0xAD, 0xBE, 0xEF })]);

    var parsed = Parse(image);
    Assert.Multiple(() => {
      Assert.That(parsed.Segments, Has.Count.EqualTo(1));
      Assert.That(parsed.Segments[0].Address, Is.EqualTo(0xC000u));
      Assert.That(parsed.Segments[0].Data, Is.EqualTo(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }).AsCollection);
    });
  }

  [Test]
  public void Add_MetadataOnlyShiftPreservesSparseGaps() {
    using var image = StreamOf("@1000\n01 02\n@1010\n03 04\nq\n");
    var descriptor = new TiTxtFormatDescriptor();
    var metadata = Encoding.UTF8.GetBytes("[firmware_hex]\nbase_address = 0x00002000\n");

    descriptor.Add(image, [ArchiveInputInfo.InMemory(FirmwareHexWriter.MetadataName, metadata)]);

    var parsed = Parse(image);
    Assert.Multiple(() => {
      Assert.That(parsed.Segments, Has.Count.EqualTo(2));
      Assert.That(parsed.Segments[0].Address, Is.EqualTo(0x2000u));
      Assert.That(parsed.Segments[1].Address, Is.EqualTo(0x2010u));
      Assert.That(parsed.GapCount, Is.EqualTo(1));
      Assert.That(parsed.TotalDataBytes, Is.EqualTo(4));
    });
  }

  [Test]
  public void Remove_FirmwareLeavesValidEmptyImage() {
    using var image = StreamOf("@1000\n01 02\nq\n");
    var descriptor = new TiTxtFormatDescriptor();

    descriptor.Remove(image, [FirmwareHexWriter.PayloadName]);

    Assert.Multiple(() => {
      Assert.That(Encoding.ASCII.GetString(image.ToArray()), Is.EqualTo("q\n"));
      Assert.That(Parse(image).Segments, Is.Empty);
    });
  }

  [Test]
  public void Purge_RemovesLivePayloadAndLeavesValidEmptyImage() {
    using var image = StreamOf("@1000\n01 02\n@2000\n03 04\nq\n");
    var descriptor = new TiTxtFormatDescriptor();

    descriptor.Purge(image);

    Assert.Multiple(() => {
      Assert.That(Encoding.ASCII.GetString(image.ToArray()), Is.EqualTo("q\n"));
      Assert.That(Parse(image).Segments, Is.Empty);
    });
  }

  [Test]
  public void Create_RejectsMultiplePayloads() {
    var descriptor = new TiTxtFormatDescriptor();
    using var output = new MemoryStream();
    var inputs = new[] {
      ArchiveInputInfo.InMemory("a.bin", new byte[] { 1 }),
      ArchiveInputInfo.InMemory("b.bin", new byte[] { 2 }),
    };

    Assert.That(() => descriptor.Create(output, inputs, new FormatCreateOptions()),
      Throws.InstanceOf<InvalidDataException>());
  }

  // Built empty and written into: MemoryStream(byte[]) is fixed-capacity, and the maintenance
  // verbs under test grow the image.
  private static MemoryStream StreamOf(string text) {
    var result = new MemoryStream();
    var bytes = Encoding.ASCII.GetBytes(text);
    result.Write(bytes, 0, bytes.Length);
    result.Position = 0;
    return result;
  }

  private static FirmwareImage Parse(MemoryStream stream) {
    stream.Position = 0;
    return TiTxtReader.Read(Encoding.ASCII.GetString(stream.ToArray()));
  }
}
