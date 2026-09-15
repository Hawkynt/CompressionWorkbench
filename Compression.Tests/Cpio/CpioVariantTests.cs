using System.Text;
using Compression.Registry;
using FileFormat.Cpio;

namespace Compression.Tests.Cpio;

[TestFixture]
public class CpioVariantTests {
  private static readonly byte[] Payload = "legacy-cpio"u8.ToArray();

  [TestCase(CpioArchiveFormat.NewAscii)]
  [TestCase(CpioArchiveFormat.NewCrc)]
  [TestCase(CpioArchiveFormat.PortableAscii)]
  [TestCase(CpioArchiveFormat.BinaryLittleEndian)]
  [TestCase(CpioArchiveFormat.BinaryBigEndian)]
  [TestCase(CpioArchiveFormat.PwbBinary)]
  [Category("RoundTrip")]
  public void WriterReader_RoundTripsEverySupportedVariant(CpioArchiveFormat format) {
    using var archive = new MemoryStream();
    using (var writer = new CpioWriter(archive, format, leaveOpen: true)) {
      writer.AddFile("hello.txt", Payload);
      writer.Finish();
    }

    archive.Position = 0;
    using var reader = new CpioReader(
      archive,
      leaveOpen: true,
      assumePwbBinary: format == CpioArchiveFormat.PwbBinary);
    var entries = reader.ReadAll();

    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.Multiple(() => {
      Assert.That(entries[0].Entry.Name, Is.EqualTo("hello.txt"));
      Assert.That(entries[0].Entry.Format, Is.EqualTo(format));
      Assert.That(entries[0].Data, Is.EqualTo(Payload));
    });
  }

  [Test]
  [Category("Compatibility")]
  public void Reader_ParsesPublishedOdcLayout() {
    // cpio(5): 6-byte magic; eight 6-octal-digit fields around an 11-digit
    // mtime; 6-digit namesize; 11-digit filesize; no pathname/data padding.
    const string header =
      "070707" +
      "000000" + // dev
      "000001" + // ino
      "100644" + // mode
      "000000" + // uid
      "000000" + // gid
      "000001" + // nlink
      "000000" + // rdev
      "00000000000" + // mtime
      "000012" + // strlen("hello.txt\\0") == 10 == octal 12
      "00000000003"; // filesize
    Assert.That(header.Length, Is.EqualTo(76));

    using var archive = new MemoryStream();
    archive.Write(Encoding.ASCII.GetBytes(header));
    archive.Write("hello.txt\0"u8);
    archive.Write("abc"u8);
    archive.Position = 0;

    using var reader = new CpioReader(archive);
    var entry = reader.ReadEntry(out var data);

    Assert.That(entry, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(entry!.Format, Is.EqualTo(CpioArchiveFormat.PortableAscii));
      Assert.That(entry.Name, Is.EqualTo("hello.txt"));
      Assert.That(entry.Mode, Is.EqualTo(0x81A4));
      Assert.That(data, Is.EqualTo("abc"u8.ToArray()));
    });
  }

  [Test]
  [Category("Compatibility")]
  public void Reader_ParsesPublishedLittleEndianBinaryLayout() {
    byte[] archive = [
      0xC7, 0x71, // magic 070707, little-endian word
      0x00, 0x00, // dev
      0x01, 0x00, // ino
      0xA4, 0x81, // mode 0100644
      0x00, 0x00, // uid
      0x00, 0x00, // gid
      0x01, 0x00, // nlink
      0x00, 0x00, // rdev
      0x00, 0x00, 0x00, 0x00, // mtime: high word first
      0x0A, 0x00, // namesize
      0x00, 0x00, 0x03, 0x00, // filesize: high word first
      (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o', (byte)'.', (byte)'t', (byte)'x', (byte)'t', 0,
      (byte)'a', (byte)'b', (byte)'c', 0, // one byte data alignment padding
    ];

    using var reader = new CpioReader(new MemoryStream(archive));
    var entry = reader.ReadEntry(out var data);

    Assert.That(entry, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(entry!.Format, Is.EqualTo(CpioArchiveFormat.BinaryLittleEndian));
      Assert.That(entry.Name, Is.EqualTo("hello.txt"));
      Assert.That(entry.Mode, Is.EqualTo(0x81A4));
      Assert.That(data, Is.EqualTo("abc"u8.ToArray()));
    });
  }

  [Test]
  [Category("Compatibility")]
  public void Reader_AutoDetectsDistinctivePwbInodeMode() {
    // PWB/V6 directory mode 0140755 = IALLOC + directory + 0755. A V7 reader
    // would call 014xxxx a socket. nlink=2 makes the old-directory reading the
    // conservative interpretation and normalization drops the inode-only flag.
    byte[] archive = [
      0xC7, 0x71,
      0x00, 0x00,
      0x01, 0x00,
      0xED, 0xC1, // 0140755
      0x00, 0x00,
      0x00, 0x00,
      0x02, 0x00, // directory link count
      0x00, 0x00,
      0x00, 0x00, 0x00, 0x00,
      0x04, 0x00, // "dir\0"
      0x00, 0x00, 0x00, 0x00,
      (byte)'d', (byte)'i', (byte)'r', 0,
    ];

    using var reader = new CpioReader(new MemoryStream(archive));
    var entry = reader.ReadEntry(out var data);

    Assert.That(entry, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(entry!.Format, Is.EqualTo(CpioArchiveFormat.PwbBinary));
      Assert.That(entry.Mode, Is.EqualTo(0x41ED));
      Assert.That(entry.IsDirectory, Is.True);
      Assert.That(data, Is.Empty);
    });
  }

  [Test]
  [Category("Compatibility")]
  public void Reader_ExplicitPwbModeDisambiguatesRegularFile() {
    using var archive = new MemoryStream();
    using (var writer = new CpioWriter(archive, CpioArchiveFormat.PwbBinary, leaveOpen: true)) {
      writer.AddFile("regular", Payload);
      writer.Finish();
    }

    archive.Position = 0;
    using var reader = new CpioReader(archive, leaveOpen: true, assumePwbBinary: true);
    var entry = reader.ReadEntry(out var data);

    Assert.That(entry, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(entry!.Format, Is.EqualTo(CpioArchiveFormat.PwbBinary));
      Assert.That(entry.IsRegularFile, Is.True);
      Assert.That(data, Is.EqualTo(Payload));
    });
  }

  [Test]
  [Category("Boundary")]
  public void PwbWriter_RejectsFilesPast24BitLimit() {
    using var archive = new MemoryStream();
    using var writer = new CpioWriter(archive, CpioArchiveFormat.PwbBinary, leaveOpen: true);
    Assert.Throws<ArgumentOutOfRangeException>(() =>
      writer.AddStreamingFile("too-large", 0x1000000, Stream.Null));
  }

  [Test]
  [Category("Corruption")]
  public void NewCrcReader_RejectsMismatchedChecksum() {
    using var archive = new MemoryStream();
    using (var writer = new CpioWriter(archive, CpioArchiveFormat.NewCrc, leaveOpen: true)) {
      writer.AddFile("x", [1, 2, 3, 4]);
      writer.Finish();
    }

    var image = archive.ToArray();
    const int headerSize = 110;
    const int nameSize = 2;
    var dataOffset = headerSize + nameSize + ((4 - (headerSize + nameSize) % 4) % 4);
    image[dataOffset] ^= 0xFF;

    using var reader = new CpioReader(new MemoryStream(image));
    Assert.Throws<InvalidDataException>(() => reader.ReadEntry(out _));
  }

  [Test]
  [Category("Corruption")]
  public void Reader_RejectsTruncatedPayload() {
    const string header =
      "070701" +
      "00000001" + // ino
      "000081A4" + // mode
      "00000000" + // uid
      "00000000" + // gid
      "00000001" + // nlink
      "00000000" + // mtime
      "00000005" + // filesize
      "00000000" + "00000000" + // dev major/minor
      "00000000" + "00000000" + // rdev major/minor
      "00000002" + // namesize: x + NUL
      "00000000"; // checksum

    using var archive = new MemoryStream();
    archive.Write(Encoding.ASCII.GetBytes(header));
    archive.Write("x\0"u8);
    archive.Write([1, 2]); // declares 5 bytes, supplies 2
    archive.Position = 0;

    using var reader = new CpioReader(archive);
    Assert.Throws<EndOfStreamException>(() => reader.ReadEntry(out _));
  }

  [Test]
  [Category("Registry")]
  public void Descriptor_ExposesVariantOptionAndAllMagicSignatures() {
    var descriptor = new CpioFormatDescriptor();
    Assert.That(descriptor, Is.AssignableTo<IFormatOptionsSchema>());

    var format = ((IFormatOptionsSchema)descriptor).OptionsSchema.Single(x => x.Key == "Format");
    Assert.Multiple(() => {
      Assert.That(format.Default, Is.EqualTo("newc"));
      Assert.That(format.AllowedValues, Is.SupersetOf(new[] { "newc", "crc", "odc", "bin-le", "bin-be", "pwb" }));
      Assert.That(descriptor.MagicSignatures, Has.Count.EqualTo(5));
    });
  }

  [TestCase("odc", "070707")]
  [TestCase("crc", "070702")]
  [Category("Registry")]
  public void Descriptor_CreateHonorsAsciiFormatOption(string format, string expectedMagic) {
    var descriptor = new CpioFormatDescriptor();
    using var output = new MemoryStream();
    descriptor.Create(
      output,
      [ArchiveInputInfo.InMemory("x", [1, 2, 3])],
      new FormatCreateOptions { FormatSpecific = new() { ["Format"] = format } });

    var image = output.ToArray();
    Assert.That(Encoding.ASCII.GetString(image, 0, 6), Is.EqualTo(expectedMagic));
  }

  [Test]
  [Category("Registry")]
  public void Descriptor_CreateHonorsPwbFormatOption() {
    var descriptor = new CpioFormatDescriptor();
    using var output = new MemoryStream();
    descriptor.Create(
      output,
      [ArchiveInputInfo.InMemory("x", [1, 2, 3])],
      new FormatCreateOptions { FormatSpecific = new() { ["Format"] = "pwb" } });

    var image = output.ToArray();
    Assert.That(image[..2], Is.EqualTo(new byte[] { 0xC7, 0x71 }));
    using var reader = new CpioReader(new MemoryStream(image), assumePwbBinary: true);
    Assert.That(reader.ReadEntry(out var data)?.Format, Is.EqualTo(CpioArchiveFormat.PwbBinary));
    Assert.That(data, Is.EqualTo(new byte[] { 1, 2, 3 }));
  }
}
