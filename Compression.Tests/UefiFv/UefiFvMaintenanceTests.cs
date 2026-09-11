using System.Buffers.Binary;
using Compression.Registry;
using FileFormat.UefiFv;

namespace Compression.Tests.UefiFv;

[TestFixture]
public sealed class UefiFvMaintenanceTests {
  private const string DriverName = "11223344-5566-7788-99aa-bbccddeeff00_DRIVER.bin";
  private const string RawName = "01234567-89ab-cdef-0123-456789abcdef_RAW.bin";

  [Test, Category("RoundTrip")]
  public void Create_LargeFile_UsesFfs3Header2() {
    var payload = Enumerable.Range(0, 0x1000000).Select(i => (byte)(i * 17)).ToArray();
    var descriptor = new UefiFvFormatDescriptor();
    using var image = new MemoryStream();
    ((IArchiveCreatable)descriptor).Create(image, [ArchiveInputInfo.InMemory(DriverName, payload)], new FormatCreateOptions());

    var bytes = image.ToArray();
    var fv = UefiFvReader.Read(bytes);
    Assert.Multiple(() => {
      Assert.That(fv.Header.FileSystemGuid, Is.EqualTo(Guid.Parse("5473C07A-3DCB-4DCA-BD6F-1E9689E7349A")));
      Assert.That(fv.Files, Has.Count.EqualTo(1));
      Assert.That(fv.Files[0].Attributes & 0x01, Is.EqualTo(0x01));
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(72 + 20, 4)) & 0x00FFFFFF, Is.Zero);
      Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(72 + 24, 8)), Is.EqualTo((ulong)payload.Length + 32));
      Assert.That(fv.Files[0].Contents, Is.EqualTo(payload));
    });
  }

  [Test, Category("Regression")]
  public void Replace_WhenNoRunFits_IsTransactional() {
    var descriptor = new UefiFvFormatDescriptor();
    using var image = new MemoryStream();
    ((IArchiveCreatable)descriptor).Create(image,
      [ArchiveInputInfo.InMemory(DriverName, new byte[1024])], new FormatCreateOptions());
    ((IArchiveModifiable)descriptor).Add(image, [ArchiveInputInfo.InMemory(RawName, new byte[60000])]);
    var before = image.ToArray();

    Assert.That(() => ((IArchiveModifiable)descriptor).Add(image,
      [ArchiveInputInfo.InMemory(DriverName, new byte[12000])]), Throws.InstanceOf<IOException>());
    Assert.That(image.ToArray(), Is.EqualTo(before), "failed replacement must not erase the previous file");
  }

  [Test, Category("RoundTrip")]
  public void Defrag_ClosesErasedHole_AndPreservesPayload() {
    var descriptor = new UefiFvFormatDescriptor();
    var first = Enumerable.Range(0, 257).Select(i => (byte)i).ToArray();
    var second = Enumerable.Range(0, 721).Select(i => (byte)(i * 7)).ToArray();
    using var image = new MemoryStream();
    ((IArchiveCreatable)descriptor).Create(image,
      [ArchiveInputInfo.InMemory(DriverName, first), ArchiveInputInfo.InMemory(RawName, second)], new FormatCreateOptions());
    ((IArchiveModifiable)descriptor).Remove(image, [DriverName]);

    ((IArchiveDefragmentable)descriptor).Defragment(image);

    image.Position = 0;
    var layout = ((IArchiveLayoutMap)descriptor).EnumerateLayout(image).ToList();
    var used = layout.Single(e => e.Kind == DefragBlockKind.Used);
    Assert.That(used.Offset, Is.EqualTo(72));
    AssertFiles(image, (RawName, second));
  }

  [Test, Category("RoundTrip")]
  public void Wipe_UsesErasePolarity_AndClearsDeletedRecord() {
    var descriptor = new UefiFvFormatDescriptor();
    var deleted = Enumerable.Repeat((byte)0xA5, 257).ToArray();
    var live = Enumerable.Repeat((byte)0x5A, 333).ToArray();
    using var image = new MemoryStream();
    ((IArchiveCreatable)descriptor).Create(image,
      [ArchiveInputInfo.InMemory(DriverName, deleted), ArchiveInputInfo.InMemory(RawName, live)], new FormatCreateOptions());

    var bytes = image.ToArray();
    bytes[72 + 23] = 0xE8; // erase-polarity-one encoding of EFI_FILE_DELETED reached after DATA_VALID
    image.Position = 0;
    image.Write(bytes);
    image.Position = 0;

    var wiped = ((IWipeEmpty)descriptor).WipeUnusedSpace(image, wipeDeletedEntries: true);
    Assert.That(wiped, Is.GreaterThan(0));
    var after = image.ToArray();
    var deletedFootprint = (24 + deleted.Length + 7) & ~7;
    Assert.That(after.AsSpan(72, deletedFootprint).ToArray(), Is.All.EqualTo((byte)0xFF));
    AssertFiles(image, (RawName, live));
  }

  [Test, Category("RoundTrip")]
  public void Purge_LeavesValidEmptyVolume() {
    var descriptor = new UefiFvFormatDescriptor();
    using var image = new MemoryStream();
    ((IArchiveCreatable)descriptor).Create(image,
      [ArchiveInputInfo.InMemory(DriverName, new byte[512]), ArchiveInputInfo.InMemory(RawName, new byte[128])],
      new FormatCreateOptions());

    ((IArchivePurgeable)descriptor).Purge(image);

    var fv = UefiFvReader.Read(image.ToArray());
    Assert.That(fv.Files.Where(f => f.Type != 0xF0), Is.Empty);
    image.Position = 0;
    Assert.That(descriptor.List(image, null).Select(e => e.Name), Is.EqualTo(new[] { "metadata.ini" }));
  }

  [Test, Category("EdgeCase")]
  public void Reader_HonorsErasePolarityZeroStateEncoding() {
    var descriptor = new UefiFvFormatDescriptor();
    using var image = new MemoryStream();
    ((IArchiveCreatable)descriptor).Create(image,
      [ArchiveInputInfo.InMemory(DriverName, new byte[] { 1, 2, 3, 4 })], new FormatCreateOptions());
    var bytes = image.ToArray();
    var attributes = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(44)) & ~0x00000800u;
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(44), attributes);
    bytes[72 + 23] = 0x07;
    bytes.AsSpan((72 + 24 + 4 + 7) & ~7).Clear();

    var fv = UefiFvReader.Read(bytes);
    Assert.Multiple(() => {
      Assert.That(fv.Header.ErasePolarity, Is.False);
      Assert.That(fv.Files, Has.Count.EqualTo(1));
      Assert.That(fv.Files[0].Contents, Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
    });
  }

  private static void AssertFiles(MemoryStream image, params (string Name, byte[] Data)[] expected) {
    var actual = UefiFvReader.Read(image.ToArray()).Files.Where(f => f.Type != 0xF0)
      .ToDictionary(f => $"{f.Name:D}_{UefiFvReader.ShortTypeTag(f.Type)}.bin", f => f.Contents, StringComparer.OrdinalIgnoreCase);
    Assert.That(actual.Keys, Is.EquivalentTo(expected.Select(e => e.Name)));
    foreach (var (name, data) in expected) Assert.That(actual[name], Is.EqualTo(data), name);
  }
}
