using Compression.Registry;
using FileFormat.Pfs0;

namespace Compression.Tests.Pfs0;

[TestFixture]
public class Pfs0WriterTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Create_RoundTripsThroughList() {
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("main.nca", "Switch NCA payload"u8.ToArray()),
      ArchiveInputInfo.InMemory("manifest.cnmt", new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }),
    };

    using var ms = new MemoryStream();
    var d = new Pfs0FormatDescriptor();
    d.Create(ms, inputs, new FormatCreateOptions());

    ms.Position = 0;
    var list = d.List(ms, null);
    Assert.That(list, Has.Count.EqualTo(2));
    Assert.That(list.Any(e => e.Name == "main.nca"), Is.True);
    Assert.That(list.Any(e => e.Name == "manifest.cnmt"), Is.True);

    ms.Position = 0;
    var bytes = d.ExtractEntryToMemory(ms, "main.nca", null);
    Assert.That(System.Text.Encoding.UTF8.GetString(bytes), Is.EqualTo("Switch NCA payload"));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesCanCreate() {
    var d = new Pfs0FormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.SupportsMultipleEntries), Is.True);
  }
}
