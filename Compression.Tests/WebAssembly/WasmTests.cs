using System.Buffers.Binary;
using System.Text;
using FileFormat.WebAssembly;

namespace Compression.Tests.WebAssembly;

[TestFixture]
public class WasmTests {

  private static void WriteLeb128(MemoryStream ms, ulong value) {
    while (true) {
      var b = (byte)(value & 0x7F);
      value >>= 7;
      if (value == 0) { ms.WriteByte(b); return; }
      ms.WriteByte((byte)(b | 0x80));
    }
  }

  /// <summary>Builds a wasm module with a type section and one custom section named "producers".</summary>
  private static byte[] BuildWasm() {
    var ms = new MemoryStream();
    ms.Write([0x00, 0x61, 0x73, 0x6D]); // magic
    ms.Write([0x01, 0x00, 0x00, 0x00]); // version 1

    // Section 1 (type): empty body of length 1 (count=0)
    ms.WriteByte(1);
    WriteLeb128(ms, 1);
    ms.WriteByte(0);

    // Section 0 (custom) with name "producers" and 2 payload bytes.
    const string name = "producers";
    var nameBytes = Encoding.UTF8.GetBytes(name);
    using (var body = new MemoryStream()) {
      WriteLeb128(body, (ulong)nameBytes.Length);
      body.Write(nameBytes);
      body.Write([0xDE, 0xAD]);
      var bodyBytes = body.ToArray();
      ms.WriteByte(0);
      WriteLeb128(ms, (ulong)bodyBytes.Length);
      ms.Write(bodyBytes);
    }

    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Read_ParsesMagicAndVersion() {
    var m = WasmReader.Read(BuildWasm());
    Assert.That(m.Version, Is.EqualTo(1u));
  }

  [Test, Category("HappyPath")]
  public void Read_FindsTypeAndCustomSections() {
    var m = WasmReader.Read(BuildWasm());
    Assert.That(m.Sections, Has.Count.EqualTo(2));
    Assert.That(m.Sections[0].TypeName, Is.EqualTo("type"));
    Assert.That(m.Sections[1].TypeName, Is.EqualTo("custom"));
    Assert.That(m.Sections[1].CustomName, Is.EqualTo("producers"));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Extract_WritesSectionsWithDescriptiveNames() {
    var data = BuildWasm();
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(data);
      new WasmFormatDescriptor().Extract(ms, tmp, null, null);
      Assert.That(File.Exists(Path.Combine(tmp, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "section_01_type.bin")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "custom_producers.bin")), Is.True);
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("EdgeCase")]
  public void Read_BadMagic_Throws() {
    var buf = new byte[8];
    buf[0] = 0xFF; buf[1] = 0xFF; buf[2] = 0xFF; buf[3] = 0xFF;
    Assert.That(() => WasmReader.Read(buf), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("EdgeCase")]
  public void Read_TooShort_Throws() {
    Assert.That(() => WasmReader.Read(new byte[4]), Throws.InstanceOf<InvalidDataException>());
  }
}
