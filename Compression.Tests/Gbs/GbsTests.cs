#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Gbs;

namespace Compression.Tests.Gbs;

[TestFixture]
public class GbsTests {

  private static byte[] BuildGbs() {
    var blob = new byte[0x70 + 3];
    blob[0] = 0x47; blob[1] = 0x42; blob[2] = 0x53; // "GBS"
    blob[0x03] = 1;   // version
    blob[0x04] = 5;   // num songs
    blob[0x05] = 1;   // first song
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x06), 0x0400); // load
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x08), 0x0400); // init
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x0A), 0x0408); // play
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x0C), 0xFFFE); // stack
    blob[0x0E] = 0x00; // timer modulo
    blob[0x0F] = 0x00; // timer control
    WriteText(blob, 0x10, "Boss Battle", 32);
    WriteText(blob, 0x30, "Hip Composer", 32);
    WriteText(blob, 0x50, "1991 Studio", 32);
    blob[0x70] = 0x11; blob[0x71] = 0x22; blob[0x72] = 0x33;
    return blob;
  }

  private static void WriteText(byte[] b, int off, string t, int len) {
    var bytes = Encoding.ASCII.GetBytes(t);
    Array.Copy(bytes, 0, b, off, Math.Min(bytes.Length, len - 1));
  }

  private static byte[] Bytes(byte[] blob, string entry) {
    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    new GbsFormatDescriptor().ExtractEntry(ms, entry, output, null);
    return output.ToArray();
  }

  [Test]
  public void SurfacesFullMetadataAndProgram() {
    var blob = BuildGbs();
    using var ms = new MemoryStream(blob);
    var entries = new GbsFormatDescriptor().List(ms, null);

    Assert.That(entries.First(e => e.Name == "FULL.gbs").Kind, Is.EqualTo("Container"));
    Assert.That(entries.First(e => e.Name == "program.bin").Kind, Is.EqualTo("Stream"));
    Assert.That(Bytes(blob, "program.bin"), Is.EqualTo(new byte[] { 0x11, 0x22, 0x33 }));
  }

  [Test]
  public void MetadataHasAllHeaderFields() {
    using var ms = new MemoryStream(BuildGbs());
    using var output = new MemoryStream();
    new GbsFormatDescriptor().ExtractEntry(ms, "metadata.ini", output, null);
    var ini = Encoding.UTF8.GetString(output.ToArray());

    Assert.That(ini, Does.Contain("title=Boss Battle"));
    Assert.That(ini, Does.Contain("author=Hip Composer"));
    Assert.That(ini, Does.Contain("copyright=1991 Studio"));
    Assert.That(ini, Does.Contain("num_songs=5"));
    Assert.That(ini, Does.Contain("first_song=1"));
    Assert.That(ini, Does.Contain("load_addr=0x0400"));
    Assert.That(ini, Does.Contain("stack_ptr=0xFFFE"));
  }

  [Test]
  public void ShortBlob_DegradesToFullOnly() {
    using var ms = new MemoryStream(new byte[0x10]);
    var entries = new GbsFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.gbs"), Is.True);
    Assert.That(entries.Any(e => e.Name == "program.bin"), Is.False);
  }
}
