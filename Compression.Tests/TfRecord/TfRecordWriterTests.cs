using Compression.Registry;
using FileFormat.TfRecord;

namespace Compression.Tests.TfRecord;

[TestFixture]
public class TfRecordWriterTests {

  [Test, Category("HappyPath")]
  public void Capabilities_IncludeWormCreate() {
    var d = new TfRecordFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Create_RoundTripsThroughReader() {
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("rec_a", "alpha"u8.ToArray()),
      ArchiveInputInfo.InMemory("rec_b", "beta-record"u8.ToArray()),
      ArchiveInputInfo.InMemory("rec_c", []),
    };

    using var output = new MemoryStream();
    new TfRecordFormatDescriptor().Create(output, inputs, new FormatCreateOptions());

    output.Position = 0;
    var reader = new TfRecordReader(output);
    Assert.That(reader.Entries, Has.Count.EqualTo(3));
    Assert.That(reader.Extract(reader.Entries[0]), Is.EqualTo("alpha"u8.ToArray()));
    Assert.That(reader.Extract(reader.Entries[1]), Is.EqualTo("beta-record"u8.ToArray()));
    Assert.That(reader.Extract(reader.Entries[2]), Is.Empty);
  }

  [Test, Category("EdgeCase")]
  public void Descriptor_Create_EmptyInputs_ProducesEmptyStream() {
    using var output = new MemoryStream();
    new TfRecordFormatDescriptor().Create(output, new List<ArchiveInputInfo>(), new FormatCreateOptions());
    Assert.That(output.Length, Is.EqualTo(0));
  }
}
