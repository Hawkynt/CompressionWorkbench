#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileFormat.FirmwareHex;

namespace Compression.Tests.FirmwareHex;

[TestFixture]
public sealed class IntelHexRwTests {

  private const string SparseHex =
    ":0410000001020304E2\n" +
    ":03200000AABBCCAC\n" +
    ":0400000312345678E5\n" +
    ":00000001FF\n";

  [Test, Category("HappyPath")]
  public void Reader_DecodesType03AsCsShift4PlusIp_AndKeepsRegisters() {
    var image = IntelHexReader.Read(":0400000312345678E5\n:00000001FF\n");

    Assert.Multiple(() => {
      Assert.That(image.StartAddress, Is.EqualTo(0x000179B8u));
      Assert.That(image.StartSegmentAddress, Is.Not.Null);
      Assert.That(image.StartSegmentAddress?.CodeSegment, Is.EqualTo((ushort)0x1234));
      Assert.That(image.StartSegmentAddress?.InstructionPointer, Is.EqualTo((ushort)0x5678));
    });
  }

  [TestCase(":0100000412E9\n:00000001FF\n", TestName = "Reader_RejectsWrongExtendedLinearPayloadLength")]
  [TestCase(":020001040001F8\n:00000001FF\n", TestName = "Reader_RejectsNonZeroExtendedLinearAddressField")]
  [TestCase(":00000001FF\n:00000001FF\n", TestName = "Reader_RejectsRecordAfterEof")]
  [Category("EdgeCase")]
  public void Reader_RejectsMalformedControlRecords(string text)
    => Assert.That(() => IntelHexReader.Read(text), Throws.InstanceOf<InvalidDataException>());

  [Test, Category("RoundTrip")]
  public void ExtractCreateRoundTrip_PreservesSparseRunsAndSegmentedStartRecord() {
    var descriptor = new IntelHexFormatDescriptor();
    var extracted = Extract(descriptor, Encoding.ASCII.GetBytes(SparseHex));

    using var rebuilt = new MemoryStream();
    descriptor.Create(rebuilt,
      extracted.Select(pair => ArchiveInputInfo.InMemory(pair.Key, pair.Value)).ToArray(),
      new FormatCreateOptions());

    var text = Encoding.ASCII.GetString(rebuilt.ToArray());
    var image = IntelHexReader.Read(text);
    Assert.Multiple(() => {
      Assert.That(image.Segments, Has.Count.EqualTo(2), "the address hole was flattened into programmed 0xFF bytes");
      Assert.That(image.Segments[0].Address, Is.EqualTo(0x00001000u));
      Assert.That(image.Segments[0].Data, Is.EqualTo(new byte[] { 1, 2, 3, 4 }).AsCollection);
      Assert.That(image.Segments[1].Address, Is.EqualTo(0x00002000u));
      Assert.That(image.Segments[1].Data, Is.EqualTo(new byte[] { 0xAA, 0xBB, 0xCC }).AsCollection);
      Assert.That(image.StartSegmentAddress?.CodeSegment, Is.EqualTo((ushort)0x1234));
      Assert.That(image.StartSegmentAddress?.InstructionPointer, Is.EqualTo((ushort)0x5678));
      Assert.That(text, Does.Contain(":0400000312345678E5"), "type-03 CS:IP was rewritten as a type-05 linear start");
    });
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_AdvertisesRw_AndCanReplaceItsFirmwareView() {
    var descriptor = new IntelHexFormatDescriptor();
    Assert.Multiple(() => {
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
      Assert.That(descriptor, Is.InstanceOf<IArchiveModifiable>());
      Assert.That(descriptor, Is.InstanceOf<IArchiveDefragmentable>());
      Assert.That(descriptor, Is.InstanceOf<IArchivePurgeable>());
    });

    var original = Create(descriptor, [ArchiveInputInfo.InMemory("firmware.bin", new byte[] { 1, 2, 3, 4 })]);
    using var archive = Expandable(original);
    ((IArchiveModifiable)descriptor).Add(archive,
      [ArchiveInputInfo.InMemory("firmware.bin", new byte[] { 9, 8, 7, 6 })]);

    var extracted = Extract(descriptor, archive.ToArray());
    Assert.That(extracted["firmware.bin"], Is.EqualTo(new byte[] { 9, 8, 7, 6 }).AsCollection);
  }

  [Test, Category("RoundTrip")]
  public void Defragment_RebuildsCanonicallyWithoutProgrammingSparseHole() {
    var descriptor = new IntelHexFormatDescriptor();
    using var archive = Expandable(Encoding.ASCII.GetBytes(SparseHex));

    ((IArchiveDefragmentable)descriptor).Defragment(archive);

    archive.Position = 0;
    using var reader = new StreamReader(archive, Encoding.ASCII, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
    var image = IntelHexReader.Read(reader.ReadToEnd());
    Assert.Multiple(() => {
      Assert.That(image.Segments, Has.Count.EqualTo(2));
      Assert.That(image.Segments[0].Address, Is.EqualTo(0x00001000u));
      Assert.That(image.Segments[1].Address, Is.EqualTo(0x00002000u));
      Assert.That(image.TotalDataBytes, Is.EqualTo(7));
      Assert.That(image.StartSegmentAddress, Is.Not.Null);
    });
  }

  [Test, Category("HappyPath")]
  public void Purge_LeavesCanonicalEmptyIntelHex() {
    var descriptor = new IntelHexFormatDescriptor();
    using var archive = Expandable(Encoding.ASCII.GetBytes(SparseHex));

    ((IArchivePurgeable)descriptor).Purge(archive);

    var text = Encoding.ASCII.GetString(archive.ToArray());
    var image = IntelHexReader.Read(text);
    Assert.Multiple(() => {
      Assert.That(text.Trim(), Is.EqualTo(":00000001FF"));
      Assert.That(image.Segments, Is.Empty);
      Assert.That(image.StartAddress, Is.Null);
      Assert.That(image.StartSegmentAddress, Is.Null);
    });
  }

  private static byte[] Create(IntelHexFormatDescriptor descriptor, IReadOnlyList<ArchiveInputInfo> inputs) {
    using var output = new MemoryStream();
    descriptor.Create(output, inputs, new FormatCreateOptions());
    return output.ToArray();
  }

  private static MemoryStream Expandable(byte[] data) {
    var result = new MemoryStream();
    result.Write(data);
    result.Position = 0;
    return result;
  }

  private static Dictionary<string, byte[]> Extract(IntelHexFormatDescriptor descriptor, byte[] image) {
    var directory = Path.Combine(Path.GetTempPath(), "cwb_ihex_" + Guid.NewGuid().ToString("N")[..10]);
    Directory.CreateDirectory(directory);
    try {
      using var input = new MemoryStream(image, writable: false);
      descriptor.Extract(input, directory, null, null);
      return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetFileName(path), File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
    } finally {
      try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
    }
  }
}
