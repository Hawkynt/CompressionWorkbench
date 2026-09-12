#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Lib;
using Compression.Registry;
using FileFormat.PngCrushAdapters;

namespace Compression.Tests.Office;

[TestFixture]
public sealed class LegacyOfficeCompoundFileDescriptorTests {

  [SetUp]
  public void SetUp() => FormatRegistration.EnsureInitialized();

  [Test]
  public void Registry_ClaimsLegacyOfficeFamilies_AndKeepsGenericCfbMagic() {
    var office = FormatRegistry.GetById("LegacyOfficeCompoundFile");
    var cfb = FormatRegistry.GetById("CompoundFileBinary");

    Assert.Multiple(() => {
      Assert.That(office, Is.Not.Null);
      Assert.That(cfb, Is.Not.Null);
      Assert.That(office!.Extensions, Is.EquivalentTo(new[] { ".doc", ".dot", ".xls", ".xlt", ".ppt", ".pps", ".pot" }));
      Assert.That(office.MagicSignatures, Is.Empty,
        "The shared CFB signature belongs to the generic structured-storage descriptor.");
      Assert.That(cfb!.MagicSignatures, Has.Count.EqualTo(1));
      Assert.That(cfb.MagicSignatures[0].Bytes, Is.EqualTo(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }));
    });
  }

  [TestCase(".doc")]
  [TestCase(".dot")]
  [TestCase(".xls")]
  [TestCase(".xlt")]
  [TestCase(".ppt")]
  [TestCase(".pps")]
  [TestCase(".pot")]
  public void Detector_UsesLegacyOfficeViewForLegacyOfficeExtensions(string extension) {
    var path = Path.Combine(Path.GetTempPath(), $"cwb_cfb_{Guid.NewGuid():N}{extension}");
    try {
      File.WriteAllBytes(path, _BuildCompound("WordDocument"));
      Assert.That(FormatDetector.Detect(path).ToString(), Is.EqualTo("LegacyOfficeCompoundFile"));
    } finally {
      try { File.Delete(path); } catch { /* best effort */ }
    }
  }

  [Test]
  public void MagicOnlyDetection_UsesGenericCompoundFileView() {
    var bytes = _BuildCompound("WordDocument");
    Assert.That(FormatDetector.DetectByMagic(bytes.AsSpan(0, 512)).ToString(), Is.EqualTo("CompoundFileBinary"));
  }

  [Test]
  public void List_PreservesStorageHierarchyAndLogicalStreams() {
    var bytes = _BuildCompound("PowerPoint Document");
    var descriptor = new LegacyOfficeCompoundFileDescriptor();

    using var input = new MemoryStream(bytes, writable: false);
    var entries = descriptor.List(input, password: null);

    Assert.Multiple(() => {
      Assert.That(entries.Select(entry => entry.Name), Is.EqualTo(new[] {
        "PowerPoint Document",
        "ObjectPool",
        "ObjectPool/Preview",
      }));
      Assert.That(entries.Single(entry => entry.Name == "ObjectPool").IsDirectory, Is.True);
      Assert.That(entries.Single(entry => entry.Name == "ObjectPool/Preview").OriginalSize, Is.EqualTo(13));
      Assert.That(entries.Any(entry => entry.Name.Contains("page_", StringComparison.OrdinalIgnoreCase)), Is.False);
      Assert.That(entries.Any(entry => entry.Name.Contains("frame_", StringComparison.OrdinalIgnoreCase)), Is.False);
    });
  }

  [Test]
  public void OpenEntry_ReadsRegularFatStreamByteExact() {
    var expected = Enumerable.Range(0, 4096).Select(i => (byte)(i * 31 + 7)).ToArray();
    var bytes = _BuildCompound("Workbook", expected, "mini payload!"u8.ToArray());
    var descriptor = new LegacyOfficeCompoundFileDescriptor();

    using var input = new MemoryStream(bytes, writable: false);
    using var opened = descriptor.OpenEntry(input, "Workbook", password: null);
    using var actual = new MemoryStream();
    opened.CopyTo(actual);

    Assert.That(actual.ToArray(), Is.EqualTo(expected));
  }

  [Test]
  public void OpenEntry_ReadsMiniFatStreamByteExact() {
    var expected = "mini payload!"u8.ToArray();
    var bytes = _BuildCompound("WordDocument", preview: expected);
    var descriptor = new LegacyOfficeCompoundFileDescriptor();

    using var input = new MemoryStream(bytes, writable: false);
    var actual = descriptor.ExtractEntryToMemory(input, "ObjectPool/Preview", password: null);

    Assert.That(actual, Is.EqualTo(expected));
  }

  [Test]
  public void GenericCfb_ViewDoesNotRequireOfficeMainStream() {
    var bytes = _BuildCompound("CustomStream");
    var generic = new CompoundFileBinaryDescriptor();
    var office = new LegacyOfficeCompoundFileDescriptor();

    using var genericInput = new MemoryStream(bytes, writable: false);
    Assert.That(generic.List(genericInput, null).Select(entry => entry.Name), Does.Contain("CustomStream"));

    using var officeInput = new MemoryStream(bytes, writable: false);
    Assert.Throws<InvalidDataException>(() => office.List(officeInput, null));
  }

  [Test]
  public void StructureValidation_RejectsOutOfBoundsDirectorySector() {
    var bytes = _BuildCompound("WordDocument");
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(48, 4), 0x0000FFFF);
    var descriptor = new CompoundFileBinaryDescriptor();

    using var input = new MemoryStream(bytes, writable: false);
    var result = descriptor.ValidateStructure(input);

    Assert.Multiple(() => {
      Assert.That(result.IsValid, Is.False);
      Assert.That(result.Issues.Any(issue => issue.Code == "CFB_STRUCTURE_INVALID"), Is.True);
    });
  }

  private static byte[] _BuildCompound(
    string mainStreamName,
    byte[]? mainStream = null,
    byte[]? preview = null) {

    const int sectorSize = 512;
    const uint end = 0xFFFFFFFE;
    const uint free = 0xFFFFFFFF;
    const uint fatSector = 0xFFFFFFFD;

    mainStream ??= Enumerable.Range(0, 4096).Select(i => (byte)(i * 17 + 3)).ToArray();
    if (mainStream.Length != 4096)
      throw new ArgumentException("Synthetic main CFB stream must be exactly 4096 bytes.", nameof(mainStream));
    preview ??= "mini payload!"u8.ToArray();
    if (preview.Length > 64)
      throw new ArgumentException("Synthetic preview must fit one mini sector.", nameof(preview));

    // sector 0..7 main stream; 8 root mini-stream; 9 MiniFAT; 10 directory; 11 FAT.
    var file = new byte[13 * sectorSize];
    var header = file.AsSpan(0, sectorSize);
    new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(header);
    BinaryPrimitives.WriteUInt16LittleEndian(header[24..26], 0x003E);
    BinaryPrimitives.WriteUInt16LittleEndian(header[26..28], 3);
    BinaryPrimitives.WriteUInt16LittleEndian(header[28..30], 0xFFFE);
    BinaryPrimitives.WriteUInt16LittleEndian(header[30..32], 9);
    BinaryPrimitives.WriteUInt16LittleEndian(header[32..34], 6);
    BinaryPrimitives.WriteUInt32LittleEndian(header[40..44], 0);
    BinaryPrimitives.WriteUInt32LittleEndian(header[44..48], 1);
    BinaryPrimitives.WriteUInt32LittleEndian(header[48..52], 10);
    BinaryPrimitives.WriteUInt32LittleEndian(header[56..60], 4096);
    BinaryPrimitives.WriteUInt32LittleEndian(header[60..64], 9);
    BinaryPrimitives.WriteUInt32LittleEndian(header[64..68], 1);
    BinaryPrimitives.WriteUInt32LittleEndian(header[68..72], end);
    BinaryPrimitives.WriteUInt32LittleEndian(header[72..76], 0);
    for (var i = 0; i < 109; ++i)
      BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(76 + i * 4, 4), free);
    BinaryPrimitives.WriteUInt32LittleEndian(header[76..80], 11);

    mainStream.CopyTo(file.AsSpan(sectorSize, 4096));
    preview.CopyTo(file.AsSpan((8 + 1) * sectorSize, preview.Length));

    var miniFat = file.AsSpan((9 + 1) * sectorSize, sectorSize);
    for (var offset = 0; offset < sectorSize; offset += 4)
      BinaryPrimitives.WriteUInt32LittleEndian(miniFat.Slice(offset, 4), free);
    BinaryPrimitives.WriteUInt32LittleEndian(miniFat[..4], end);

    var directory = file.AsSpan((10 + 1) * sectorSize, sectorSize);
    _DirectoryEntry(directory[..128], "Root Entry", 5, free, free, 1, 8, 64);
    _DirectoryEntry(directory.Slice(128, 128), mainStreamName, 2, free, 2, free, 0, 4096);
    _DirectoryEntry(directory.Slice(256, 128), "ObjectPool", 1, free, free, 3, end, 0);
    _DirectoryEntry(directory.Slice(384, 128), "Preview", 2, free, free, free, 0, (ulong)preview.Length);

    var fat = file.AsSpan((11 + 1) * sectorSize, sectorSize);
    for (var offset = 0; offset < sectorSize; offset += 4)
      BinaryPrimitives.WriteUInt32LittleEndian(fat.Slice(offset, 4), free);
    for (uint sector = 0; sector < 7; ++sector)
      BinaryPrimitives.WriteUInt32LittleEndian(fat.Slice((int)sector * 4, 4), sector + 1);
    BinaryPrimitives.WriteUInt32LittleEndian(fat.Slice(7 * 4, 4), end);
    BinaryPrimitives.WriteUInt32LittleEndian(fat.Slice(8 * 4, 4), end);
    BinaryPrimitives.WriteUInt32LittleEndian(fat.Slice(9 * 4, 4), end);
    BinaryPrimitives.WriteUInt32LittleEndian(fat.Slice(10 * 4, 4), end);
    BinaryPrimitives.WriteUInt32LittleEndian(fat.Slice(11 * 4, 4), fatSector);

    return file;
  }

  private static void _DirectoryEntry(
    Span<byte> destination,
    string name,
    byte objectType,
    uint left,
    uint right,
    uint child,
    uint startSector,
    ulong streamSize) {

    var encoded = System.Text.Encoding.Unicode.GetBytes(name + "\0");
    encoded.CopyTo(destination);
    BinaryPrimitives.WriteUInt16LittleEndian(destination[64..66], checked((ushort)encoded.Length));
    destination[66] = objectType;
    destination[67] = 1;
    BinaryPrimitives.WriteUInt32LittleEndian(destination[68..72], left);
    BinaryPrimitives.WriteUInt32LittleEndian(destination[72..76], right);
    BinaryPrimitives.WriteUInt32LittleEndian(destination[76..80], child);
    BinaryPrimitives.WriteUInt32LittleEndian(destination[116..120], startSector);
    BinaryPrimitives.WriteUInt64LittleEndian(destination[120..128], streamSize);
  }
}
