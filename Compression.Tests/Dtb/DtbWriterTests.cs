#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.Dtb;

namespace Compression.Tests.Dtb;

[TestFixture]
public class DtbWriterTests {

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesCanCreate() {
    var d = new DtbFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(d is IArchiveCreatable, Is.True);
  }

  [Test, Category("HappyPath")]
  public void Write_EmitsMagicAndHeaderInBigEndian() {
    using var ms = new MemoryStream();
    DtbWriter.Write(ms, [("compatible", "vendor,board\0vendor\0"u8.ToArray())]);
    var blob = ms.ToArray();
    Assert.That(BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(0, 4)), Is.EqualTo(DtbReader.Magic));
    Assert.That(BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(4, 4)), Is.EqualTo((uint)blob.Length));
    Assert.That(BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(20, 4)), Is.EqualTo(17u)); // v17
    Assert.That(BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(24, 4)), Is.EqualTo(16u)); // last comp version
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_RootProperties_ReadBackThroughDtbReader() {
    var compat = "vendor,board\0linux,dummy\0"u8.ToArray();
    var modelStr = "Test Board"u8.ToArray();
    using var ms = new MemoryStream();
    DtbWriter.Write(ms, [
      ("compatible", compat),
      ("model", modelStr),
    ]);

    var fdt = DtbReader.Read(ms.ToArray());
    // Plus spec-required #address-cells, #size-cells on the root.
    Assert.That(fdt.Properties.Count, Is.GreaterThanOrEqualTo(4));

    var compatProp = fdt.Properties.FirstOrDefault(p => p.Name == "compatible");
    Assert.That(compatProp, Is.Not.Null);
    Assert.That(compatProp!.Data, Is.EqualTo(compat));

    var modelProp = fdt.Properties.FirstOrDefault(p => p.Name == "model");
    Assert.That(modelProp, Is.Not.Null);
    Assert.That(modelProp!.Data, Is.EqualTo(modelStr));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Create_RoundTripsThroughList() {
    var d = new DtbFormatDescriptor();
    var inputs = new[] {
      ArchiveInputInfo.InMemory("compatible.txt", "vendor,board\0"u8.ToArray()),
      ArchiveInputInfo.InMemory("model.txt", "Test\0"u8.ToArray()),
    };

    using var outStream = new MemoryStream();
    d.Create(outStream, inputs, new FormatCreateOptions());
    outStream.Position = 0;

    var entries = d.List(outStream, null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Contains.Item("metadata.ini"));
    Assert.That(names.Any(n => n.EndsWith("compatible.txt")), Is.True);
    Assert.That(names.Any(n => n.EndsWith("model.txt")), Is.True);
  }

  // Boundary: writer rejects nothing — empty input list still produces a valid root-only DTB.
  [Test, Category("Boundary")]
  public void Write_NoInputs_ProducesRootOnlyButValid() {
    using var ms = new MemoryStream();
    DtbWriter.Write(ms, []);
    var fdt = DtbReader.Read(ms.ToArray());
    // Root carries the two spec-required cell properties.
    Assert.That(fdt.Properties.Any(p => p.Name == "#address-cells"), Is.True);
    Assert.That(fdt.Properties.Any(p => p.Name == "#size-cells"), Is.True);
  }

  // Boundary: property name sanitisation
  [Test, Category("Boundary")]
  public void SanitisePropertyName_ReplacesInvalidChars() {
    Assert.That(DtbWriter.SanitisePropertyName("foo/bar"), Is.EqualTo("bar"));
    Assert.That(DtbWriter.SanitisePropertyName("name with spaces"), Is.EqualTo("name_with_spaces"));
    Assert.That(DtbWriter.SanitisePropertyName(""), Is.EqualTo("_"));
    Assert.That(DtbWriter.SanitisePropertyName("#address-cells"), Is.EqualTo("#address-cells"));
  }

  // Equivalence: strings block must contain NUL-terminated property names.
  [Test, Category("HappyPath")]
  public void Write_StringsBlock_HasNulTerminatedNames() {
    using var ms = new MemoryStream();
    DtbWriter.Write(ms, [("compatible", "x"u8.ToArray()), ("model", "y"u8.ToArray())]);
    var blob = ms.ToArray();
    var stringsOff = (int)BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(12, 4));
    var stringsSize = (int)BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(32, 4));
    var strings = Encoding.ASCII.GetString(blob.AsSpan(stringsOff, stringsSize));
    Assert.That(strings, Does.Contain("#address-cells\0"));
    Assert.That(strings, Does.Contain("#size-cells\0"));
    Assert.That(strings, Does.Contain("compatible\0"));
    Assert.That(strings, Does.Contain("model\0"));
  }
}
