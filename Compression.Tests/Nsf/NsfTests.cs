using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.Nsf;

namespace Compression.Tests.Nsf;

[TestFixture]
public class NsfTests {

  // Builds a minimal valid NSF: 128-byte header + program data.
  private static byte[] BuildSample(out byte[] program) {
    program = [0xA9, 0x00, 0x8D, 0x00, 0x40, 0x60];

    var file = new byte[0x80 + program.Length];
    Encoding.ASCII.GetBytes("NESM").CopyTo(file, 0);
    file[0x04] = 0x1A;
    file[0x05] = 0x01; // version
    file[0x06] = 5;    // total songs
    file[0x07] = 1;    // starting song
    BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(0x08), 0x8000); // load
    BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(0x0A), 0x8000); // init
    BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(0x0C), 0x8003); // play
    WriteField(file, 0x0E, "Test NSF");
    WriteField(file, 0x2E, "Test Artist");
    WriteField(file, 0x4E, "2026 Workbench");
    BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(0x6E), 16639);  // NTSC speed
    file[0x7A] = 0x00; // NTSC region
    file[0x7B] = 0x01 | 0x04; // VRC6 + FDS

    Array.Copy(program, 0, file, 0x80, program.Length);
    return file;
  }

  private static void WriteField(byte[] file, int offset, string value) {
    var bytes = Encoding.Latin1.GetBytes(value);
    Array.Copy(bytes, 0, file, offset, Math.Min(bytes.Length, 32));
  }

  [Test, Category("HappyPath")]
  public void List_ExposesFullMetadataAndProgram() {
    var sample = BuildSample(out _);
    var desc = new NsfFormatDescriptor();
    using var ms = new MemoryStream(sample);
    var entries = desc.List(ms, null);

    Assert.That(entries[0].Name, Is.EqualTo("FULL.nsf"));
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "program.bin"), Is.True);
  }

  [Test, Category("RoundTrip")]
  public void Extract_FullIsByteIdentical_AndMetadataParsed() {
    var sample = BuildSample(out var program);
    var desc = new NsfFormatDescriptor();
    var dir = Path.Combine(Path.GetTempPath(), "nsf_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      using (var ms = new MemoryStream(sample))
        desc.Extract(ms, dir, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(dir, "FULL.nsf")), Is.EqualTo(sample));
      Assert.That(File.ReadAllBytes(Path.Combine(dir, "program.bin")), Is.EqualTo(program));
      var meta = File.ReadAllText(Path.Combine(dir, "metadata.ini"));
      Assert.That(meta, Does.Contain("name = Test NSF"));
      Assert.That(meta, Does.Contain("artist = Test Artist"));
      Assert.That(meta, Does.Contain("songs = 5"));
      Assert.That(meta, Does.Contain("region = NTSC"));
      Assert.That(meta, Does.Contain("VRC6"));
      Assert.That(meta, Does.Contain("FDS"));
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
  }

  [Test, Category("ErrorHandling")]
  public void List_Malformed_DoesNotThrow() {
    var garbage = new byte[32];
    Array.Fill(garbage, (byte)0xAB);
    var desc = new NsfFormatDescriptor();
    using var ms = new MemoryStream(garbage);
    List<ArchiveEntryInfo> entries = null!;
    Assert.That(() => entries = desc.List(ms, null), Throws.Nothing);
    Assert.That(entries[0].Name, Is.EqualTo("FULL.nsf"));
  }

  [Test, Category("Detection")]
  public void Magic_MatchesNesm() {
    var desc = new NsfFormatDescriptor();
    Assert.That(desc.MagicSignatures[0].Bytes, Is.EqualTo(new byte[] { 0x4E, 0x45, 0x53, 0x4D, 0x1A }));
  }
}
