using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.Gbs;

namespace Compression.Tests.Gbs;

[TestFixture]
public class GbsTests {

  // Builds a minimal valid GBS: 0x70-byte header + code+data.
  private static byte[] BuildSample(out byte[] program) {
    program = [0x3E, 0x80, 0xE0, 0x26, 0xC9];

    var file = new byte[0x70 + program.Length];
    Encoding.ASCII.GetBytes("GBS").CopyTo(file, 0);
    file[0x03] = 0x01; // version
    file[0x04] = 4;    // songs
    file[0x05] = 1;    // first song
    BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(0x06), 0x0400); // load
    BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(0x08), 0x0400); // init
    BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(0x0A), 0x0408); // play
    BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(0x0C), 0xFFFE); // stack
    file[0x0E] = 0x00; // timer modulo
    file[0x0F] = 0x00; // timer control
    WriteField(file, 0x10, "Test GBS");
    WriteField(file, 0x30, "Test Author");
    WriteField(file, 0x50, "2026 Workbench");

    Array.Copy(program, 0, file, 0x70, program.Length);
    return file;
  }

  private static void WriteField(byte[] file, int offset, string value) {
    var bytes = Encoding.Latin1.GetBytes(value);
    Array.Copy(bytes, 0, file, offset, Math.Min(bytes.Length, 32));
  }

  [Test, Category("HappyPath")]
  public void List_ExposesFullMetadataAndProgram() {
    var sample = BuildSample(out _);
    var desc = new GbsFormatDescriptor();
    using var ms = new MemoryStream(sample);
    var entries = desc.List(ms, null);

    Assert.That(entries[0].Name, Is.EqualTo("FULL.gbs"));
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "program.bin"), Is.True);
  }

  [Test, Category("RoundTrip")]
  public void Extract_FullIsByteIdentical_AndMetadataParsed() {
    var sample = BuildSample(out var program);
    var desc = new GbsFormatDescriptor();
    var dir = Path.Combine(Path.GetTempPath(), "gbs_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      using (var ms = new MemoryStream(sample))
        desc.Extract(ms, dir, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(dir, "FULL.gbs")), Is.EqualTo(sample));
      Assert.That(File.ReadAllBytes(Path.Combine(dir, "program.bin")), Is.EqualTo(program));
      var meta = File.ReadAllText(Path.Combine(dir, "metadata.ini"));
      Assert.That(meta, Does.Contain("name = Test GBS"));
      Assert.That(meta, Does.Contain("author = Test Author"));
      Assert.That(meta, Does.Contain("songs = 4"));
      Assert.That(meta, Does.Contain("init_address = 0x0400"));
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
  }

  [Test, Category("ErrorHandling")]
  public void List_Malformed_DoesNotThrow() {
    var garbage = new byte[16];
    Array.Fill(garbage, (byte)0x77);
    var desc = new GbsFormatDescriptor();
    using var ms = new MemoryStream(garbage);
    List<ArchiveEntryInfo> entries = null!;
    Assert.That(() => entries = desc.List(ms, null), Throws.Nothing);
    Assert.That(entries[0].Name, Is.EqualTo("FULL.gbs"));
  }

  [Test, Category("Detection")]
  public void Magic_MatchesGbs() {
    var desc = new GbsFormatDescriptor();
    Assert.That(desc.MagicSignatures[0].Bytes, Is.EqualTo("GBS"u8.ToArray()));
  }
}
