#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Jxl;

namespace Compression.Tests.Jxl;

[TestFixture]
public class JxlTests {

  private static void WriteBox(Stream ms, string type, byte[] body) {
    Span<byte> header = stackalloc byte[8];
    BinaryPrimitives.WriteUInt32BigEndian(header[..4], (uint)(8 + body.Length));
    header[4] = (byte)type[0]; header[5] = (byte)type[1]; header[6] = (byte)type[2]; header[7] = (byte)type[3];
    ms.Write(header);
    ms.Write(body);
  }

  /// <summary>
  /// Build a minimal box-form JXL fixture: signature box, ftyp, jxll, jxlc, Exif, xml, jumb.
  /// </summary>
  private static byte[] MakeBoxForm() {
    using var ms = new MemoryStream();
    // Signature box: 'JXL ' + 0D 0A 87 0A.
    WriteBox(ms, "JXL ", new byte[] { 0x0D, 0x0A, 0x87, 0x0A });
    // ftyp: major 'jxl ', minor 0, compat 'jxl '.
    var ftyp = new List<byte>();
    ftyp.AddRange("jxl "u8.ToArray());
    ftyp.AddRange(new byte[] { 0, 0, 0, 0 });
    ftyp.AddRange("jxl "u8.ToArray());
    WriteBox(ms, "ftyp", ftyp.ToArray());
    // jxll: one byte level = 5.
    WriteBox(ms, "jxll", new byte[] { 0x05 });
    // jxlc: a pretend codestream starting with the naked magic.
    WriteBox(ms, "jxlc", new byte[] { 0xFF, 0x0A, 0x11, 0x22, 0x33, 0x44 });
    // Exif: non-empty body.
    WriteBox(ms, "Exif", "exifdata"u8.ToArray());
    // xml : XMP-like content.
    WriteBox(ms, "xml ", "<xmp/>"u8.ToArray());
    // jumb: small JUMBF stub.
    WriteBox(ms, "jumb", new byte[] { 0xAA, 0xBB, 0xCC });
    return ms.ToArray();
  }

  /// <summary>Build a box-form fixture with two jxlp parts (no jxlc) to test concatenation.</summary>
  private static byte[] MakeBoxFormWithJxlpParts() {
    using var ms = new MemoryStream();
    WriteBox(ms, "JXL ", new byte[] { 0x0D, 0x0A, 0x87, 0x0A });
    var ftyp = new List<byte>();
    ftyp.AddRange("jxl "u8.ToArray());
    ftyp.AddRange(new byte[] { 0, 0, 0, 0 });
    ftyp.AddRange("jxl "u8.ToArray());
    WriteBox(ms, "ftyp", ftyp.ToArray());
    // Two jxlp parts: each has 4-byte index prefix + payload.
    WriteBox(ms, "jxlp", new byte[] { 0, 0, 0, 0,  0xAA, 0xBB });
    WriteBox(ms, "jxlp", new byte[] { 0x80, 0, 0, 1,  0xCC, 0xDD });
    return ms.ToArray();
  }

  private static byte[] MakeNaked()
    => [0xFF, 0x0A, 0x12, 0x34, 0x56, 0x78, 0x9A];

  [Test]
  public void BoxForm_ListsCanonicalEntries() {
    var data = MakeBoxForm();
    using var ms = new MemoryStream(data);
    var entries = new JxlFormatDescriptor().List(ms, null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Contains.Item("FULL.jxl"));
    Assert.That(names, Contains.Item("metadata.ini"));
    Assert.That(names, Contains.Item("codestream.jxl"));
    Assert.That(names, Contains.Item("metadata/exif.bin"));
    Assert.That(names, Contains.Item("metadata/xmp.xml"));
    Assert.That(names, Contains.Item("metadata/jumb.bin"));
  }

  [Test]
  public void BoxForm_ExtractWritesFiles() {
    var data = MakeBoxForm();
    var tmp = Path.Combine(Path.GetTempPath(), "jxl_box_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(data);
      new JxlFormatDescriptor().Extract(ms, tmp, null, null);
      Assert.That(File.Exists(Path.Combine(tmp, "FULL.jxl")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "codestream.jxl")), Is.True);
      var ini = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(ini, Does.Contain("form=box"));
      Assert.That(ini, Does.Contain("level=5"));
      Assert.That(ini, Does.Contain("has_exif=true"));
      Assert.That(ini, Does.Contain("has_xmp=true"));
      Assert.That(ini, Does.Contain("has_jumb=true"));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }

  [Test]
  public void BoxForm_ConcatenatesJxlpParts() {
    var data = MakeBoxFormWithJxlpParts();
    var tmp = Path.Combine(Path.GetTempPath(), "jxl_jxlp_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(data);
      new JxlFormatDescriptor().Extract(ms, tmp, null, null);
      var cs = File.ReadAllBytes(Path.Combine(tmp, "codestream.jxl"));
      // Expect the payload bytes of both parts, in order: AA BB CC DD.
      Assert.That(cs, Is.EqualTo(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }

  [Test]
  public void Naked_ListsFullAndCodestream() {
    var data = MakeNaked();
    using var ms = new MemoryStream(data);
    var entries = new JxlFormatDescriptor().List(ms, null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Contains.Item("FULL.jxl"));
    Assert.That(names, Contains.Item("metadata.ini"));
    Assert.That(names, Contains.Item("codestream.jxl"));
    Assert.That(names, Does.Not.Contain("metadata/exif.bin"));
  }

  [Test]
  public void Naked_MetadataMarksForm() {
    var data = MakeNaked();
    var tmp = Path.Combine(Path.GetTempPath(), "jxl_naked_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(data);
      new JxlFormatDescriptor().Extract(ms, tmp, null, null);
      var ini = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(ini, Does.Contain("form=naked"));
      Assert.That(ini, Does.Contain("has_exif=false"));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }

  [Test]
  public void Descriptor_BasicProperties() {
    var d = new JxlFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Jxl"));
    Assert.That(d.Extensions, Contains.Item(".jxl"));
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
    Assert.That(d.MagicSignatures.Count, Is.EqualTo(2));
  }

  [Test]
  public void List_DoesNotThrowOnGarbage() {
    var junk = Encoding.ASCII.GetBytes("not a jxl file at all");
    using var ms = new MemoryStream(junk);
    Assert.DoesNotThrow(() => new JxlFormatDescriptor().List(ms, null));
  }
}
