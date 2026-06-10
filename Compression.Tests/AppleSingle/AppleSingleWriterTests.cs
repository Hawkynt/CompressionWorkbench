#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.AppleSingle;

namespace Compression.Tests.AppleSingle;

[TestFixture]
public class AppleSingleWriterTests {

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesCanCreate() {
    var d = new AppleSingleFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(d is IArchiveCreatable, Is.True);
  }

  [Test, Category("HappyPath")]
  public void Build_EmitsCanonicalHeader() {
    var blob = AppleSingleWriter.Build([(1u, "abc"u8.ToArray())]);
    var magic = BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(0, 4));
    var version = BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(4, 4));
    var count = BinaryPrimitives.ReadUInt16BigEndian(blob.AsSpan(24, 2));
    Assert.That(magic, Is.EqualTo(AppleSingleReader.MagicSingle));
    Assert.That(version, Is.EqualTo(0x00020000u));
    Assert.That(count, Is.EqualTo((ushort)1));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SingleDataFork_ReadsBack() {
    var payload = "Hello AppleSingle\n"u8.ToArray();
    var blob = AppleSingleWriter.Build([(1u, payload)]);

    var container = AppleSingleReader.Read(blob);
    Assert.That(container.IsDouble, Is.False);
    Assert.That(container.Entries, Has.Count.EqualTo(1));
    Assert.That(container.Entries[0].EntryId, Is.EqualTo(1u));
    Assert.That(container.Entries[0].Data, Is.EqualTo(payload));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Create_RoundTripsThroughReader() {
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "fork-bytes"u8.ToArray());
      var inputs = new[] {
        new ArchiveInputInfo(tmp, "data_fork.bin", IsDirectory: false),
        ArchiveInputInfo.InMemory("real_name.txt", Encoding.ASCII.GetBytes("MyFile.txt")),
      };
      var d = new AppleSingleFormatDescriptor();
      using var outStream = new MemoryStream();
      d.Create(outStream, inputs, new FormatCreateOptions());

      outStream.Position = 0;
      var entries = d.List(outStream, null);
      // metadata.ini + 2 entries.
      Assert.That(entries.Count, Is.EqualTo(3));
      Assert.That(entries.Any(e => e.Name == "data_fork.bin"), Is.True);
      Assert.That(entries.Any(e => e.Name == "real_name.txt"), Is.True);

      outStream.Position = 0;
      var data = d.ExtractEntryToMemory(outStream, "data_fork.bin", null);
      Assert.That(data, Is.EqualTo("fork-bytes"u8.ToArray()));
    } finally {
      File.Delete(tmp);
    }
  }

  // Boundary: the documented role-id table covers exactly the 15 known names.
  [Test, Category("Boundary")]
  public void EntryIdForName_DocumentedRoles() {
    Assert.That(AppleSingleWriter.EntryIdForName("data_fork.bin"), Is.EqualTo(1u));
    Assert.That(AppleSingleWriter.EntryIdForName("resource_fork.bin"), Is.EqualTo(2u));
    Assert.That(AppleSingleWriter.EntryIdForName("real_name.txt"), Is.EqualTo(3u));
    Assert.That(AppleSingleWriter.EntryIdForName("finder_info.bin"), Is.EqualTo(8u));
    Assert.That(AppleSingleWriter.EntryIdForName("entry_99999.bin"), Is.EqualTo(99999u));
  }

  // Equivalence: writer must filter the synthetic metadata.ini surfaced by the
  // reader so a list-then-create round-trip doesn't smuggle it into the output.
  [Test, Category("HappyPath")]
  public void Descriptor_Create_SkipsSyntheticMetadataIni() {
    var inputs = new[] {
      ArchiveInputInfo.InMemory("metadata.ini", "; reader synthetic"u8.ToArray()),
      ArchiveInputInfo.InMemory("data_fork.bin", "real"u8.ToArray()),
    };
    using var outStream = new MemoryStream();
    new AppleSingleFormatDescriptor().Create(outStream, inputs, new FormatCreateOptions());

    var container = AppleSingleReader.Read(outStream.ToArray());
    Assert.That(container.Entries, Has.Count.EqualTo(1));
    Assert.That(container.Entries[0].EntryId, Is.EqualTo(1u));
  }
}
