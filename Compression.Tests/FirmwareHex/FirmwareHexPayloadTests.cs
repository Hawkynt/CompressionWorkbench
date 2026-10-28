using Compression.Registry;
using FileFormat.FirmwareHex;

namespace Compression.Tests.FirmwareHex;

[TestFixture]
public sealed class FirmwareHexPayloadTests {
  [TestCase(false)]
  [TestCase(true)]
  public void Create_RejectsMultiplePayloadsWithoutWritingOutput(bool emptyFirst) {
    var descriptor = new IntelHexFormatDescriptor();
    using var output = new MemoryStream();
    var inputs = new[] {
      ArchiveInputInfo.InMemory("first.bin", emptyFirst ? [] : [1, 2, 3]),
      ArchiveInputInfo.InMemory("second.bin", [9, 8, 7]),
    };

    Assert.Throws<InvalidDataException>(() =>
      ((IArchiveCreatable)descriptor).Create(output, inputs, new FormatCreateOptions()));
    Assert.That(output.Length, Is.Zero);
  }

  [Test]
  public void Add_RejectsAnotherPayloadWithoutChangingTheArchive() {
    var descriptor = new IntelHexFormatDescriptor();
    using var archive = new MemoryStream();
    ((IArchiveCreatable)descriptor).Create(archive,
      [ArchiveInputInfo.InMemory("firmware.bin", [1, 2, 3])], new FormatCreateOptions());
    var original = archive.ToArray();
    archive.Position = 0;

    Assert.Throws<InvalidDataException>(() => ((IArchiveModifiable)descriptor).Add(archive,
      [ArchiveInputInfo.InMemory("ADD.BIN", [9, 8, 7])]));
    Assert.That(archive.ToArray(), Is.EqualTo(original));
  }

}
