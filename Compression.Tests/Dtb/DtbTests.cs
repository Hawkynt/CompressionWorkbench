using System.Buffers.Binary;
using System.Text;
using FileFormat.Dtb;

namespace Compression.Tests.Dtb;

[TestFixture]
public class DtbTests {

  // Builds a tiny DTB with one root node containing a 'compatible' string property.
  private static byte[] BuildTinyDtb() {
    // Strings block: just "compatible\0".
    var strings = Encoding.ASCII.GetBytes("compatible\0");
    // Structure block:
    //   FDT_BEGIN_NODE "" (root, name is empty string)
    //     FDT_PROP "compatible" = "foo,bar\0"
    //   FDT_END_NODE
    //   FDT_END
    var structBlock = new List<byte>();
    WriteU32BE(structBlock, DtbReader.FDT_BEGIN_NODE);
    structBlock.Add(0); // empty node name terminator
    PadTo4(structBlock);
    // Property
    var propValue = Encoding.ASCII.GetBytes("foo,bar\0");
    WriteU32BE(structBlock, DtbReader.FDT_PROP);
    WriteU32BE(structBlock, (uint)propValue.Length);
    WriteU32BE(structBlock, 0); // name offset (points at "compatible")
    structBlock.AddRange(propValue);
    PadTo4(structBlock);
    WriteU32BE(structBlock, DtbReader.FDT_END_NODE);
    WriteU32BE(structBlock, DtbReader.FDT_END);

    var structArr = structBlock.ToArray();

    // Memory reservation map: one empty terminator entry (16 zero bytes).
    var memRsvmap = new byte[16];

    var headerSize = 40;
    var offRsvmap = (uint)headerSize;
    var offStruct = offRsvmap + (uint)memRsvmap.Length;
    var offStrings = offStruct + (uint)structArr.Length;
    var total = offStrings + (uint)strings.Length;

    var output = new byte[total];
    BinaryPrimitives.WriteUInt32BigEndian(output, DtbReader.Magic);
    BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(4), total);
    BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(8), offStruct);
    BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(12), offStrings);
    BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(16), offRsvmap);
    BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(20), 17); // version
    BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(24), 16); // last_comp_version
    BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(28), 0);  // boot_cpuid_phys
    BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(32), (uint)strings.Length);
    BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(36), (uint)structArr.Length);
    memRsvmap.CopyTo(output, (int)offRsvmap);
    structArr.CopyTo(output, (int)offStruct);
    strings.CopyTo(output, (int)offStrings);
    return output;

    static void WriteU32BE(List<byte> dst, uint value) {
      dst.Add((byte)(value >> 24));
      dst.Add((byte)(value >> 16));
      dst.Add((byte)(value >> 8));
      dst.Add((byte)value);
    }
    static void PadTo4(List<byte> dst) {
      while ((dst.Count & 3) != 0) dst.Add(0);
    }
  }

  [Test, Category("HappyPath")]
  public void Reader_ParsesHeaderAndCompatibleProperty() {
    var data = BuildTinyDtb();
    var fdt = DtbReader.Read(data);

    Assert.Multiple(() => {
      Assert.That(fdt.Header.Magic, Is.EqualTo(DtbReader.Magic));
      Assert.That(fdt.Header.Version, Is.EqualTo(17u));
      Assert.That(fdt.Properties, Has.Count.EqualTo(1));
      Assert.That(fdt.Properties[0].Name, Is.EqualTo("compatible"));
      Assert.That(Encoding.ASCII.GetString(fdt.Properties[0].Data),
        Is.EqualTo("foo,bar\0"));
    });
  }

  [Test, Category("HappyPath")]
  public void Descriptor_EmitsMetadataAndPropertyEntry() {
    var data = BuildTinyDtb();
    using var ms = new MemoryStream(data);
    var entries = new DtbFormatDescriptor().List(ms, null);
    var names = entries.Select(e => e.Name).ToList();

    Assert.That(names, Does.Contain("metadata.ini"));
    // The root node path is '/', so BuildNodePath returns it — the descriptor
    // replaces an empty base with "_root".
    Assert.That(names.Any(n => n.EndsWith("compatible.txt")), Is.True,
      $"Expected a *compatible.txt entry, got: {string.Join(", ", names)}");
  }

  [Test, Category("EdgeCase")]
  public void Reader_RejectsWrongMagic() {
    var data = new byte[40];
    Assert.That(() => DtbReader.Read(data), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("EdgeCase")]
  public void Reader_RejectsTruncatedHeader() {
    var data = new byte[20];
    Assert.That(() => DtbReader.Read(data), Throws.InstanceOf<InvalidDataException>());
  }
}
