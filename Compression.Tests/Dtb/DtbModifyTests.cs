using Compression.Registry;
using FileFormat.Dtb;

namespace Compression.Tests.Dtb;

[TestFixture]
public sealed class DtbModifyTests {
  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void CreateAddReplaceRemove_PreservesHierarchy() {
    var descriptor = new DtbFormatDescriptor();
    var compatible = "vendor,board\0"u8.ToArray();
    var reg = new byte[] { 0, 0, 0, 1, 0, 0, 0, 32 };
    var status = "okay\0"u8.ToArray();
    var replacement = "disabled\0"u8.ToArray();

    using var image = new MemoryStream();
    descriptor.Create(image, [
      ArchiveInputInfo.InMemory("soc/serial@1000/compatible.bin", compatible),
      ArchiveInputInfo.InMemory("soc/serial@1000/reg.bin", reg),
    ], new FormatCreateOptions());

    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
    AssertProperty(image, "/soc/serial@1000", "compatible", compatible);
    AssertProperty(image, "/soc/serial@1000", "reg", reg);

    image.Position = 0;
    ((IArchiveModifiable)descriptor).Add(image,
      [ArchiveInputInfo.InMemory("soc/serial@1000/status.bin", status)]);
    AssertProperty(image, "/soc/serial@1000", "status", status);

    image.Position = 0;
    ((IArchiveModifiable)descriptor).Add(image,
      [ArchiveInputInfo.InMemory("soc/serial@1000/status.bin", replacement)]);
    AssertProperty(image, "/soc/serial@1000", "status", replacement);

    image.Position = 0;
    ((IArchiveModifiable)descriptor).Remove(image, ["soc/serial@1000/reg.bin"]);
    var parsed = Parse(image);
    Assert.That(parsed.Properties.Any(p => p.NodePath == "/soc/serial@1000" && p.Name == "reg"), Is.False);
    Assert.That(parsed.Properties.Any(p => p.NodePath == "/soc/serial@1000" && p.Name == "compatible"), Is.True);
  }

  [Test, Category("RoundTrip")]
  public void RootTextEntry_ListCreateRoundTrip_MapsBackToRootAndRestoresNulTermination() {
    var descriptor = new DtbFormatDescriptor();
    using var image = new MemoryStream();
    descriptor.Create(image,
      [ArchiveInputInfo.InMemory("_root/compatible.txt", "vendor,board"u8.ToArray())],
      new FormatCreateOptions());

    var parsed = Parse(image);
    var property = parsed.Properties.Single(p => p.NodePath == "/" && p.Name == "compatible");
    Assert.That(property.Data, Is.EqualTo("vendor,board\0"u8.ToArray()));
  }

  private static DtbReader.Fdt Parse(MemoryStream image) => DtbReader.Read(image.ToArray());

  private static void AssertProperty(MemoryStream image, string path, string name, byte[] data) {
    var property = Parse(image).Properties.Single(p => p.NodePath == path && p.Name == name);
    Assert.That(property.Data, Is.EqualTo(data));
  }
}
