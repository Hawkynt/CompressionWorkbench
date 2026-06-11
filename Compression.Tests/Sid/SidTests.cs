using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.Sid;

namespace Compression.Tests.Sid;

[TestFixture]
public class SidTests {

  // Builds a minimal valid PSID v2 header + C64 program data.
  private static byte[] BuildSample(out byte[] program) {
    program = [0x00, 0x10, 0x4C, 0x00, 0x10, 0x60]; // load addr + tiny code

    var dataOffset = 0x7C; // v2 header size
    var file = new byte[dataOffset + program.Length];
    Encoding.ASCII.GetBytes("PSID").CopyTo(file, 0);
    BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(0x04), 2);            // version
    BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(0x06), (ushort)dataOffset);
    BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(0x08), 0x1000);       // load addr
    BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(0x0A), 0x1000);       // init addr
    BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(0x0C), 0x1003);       // play addr
    BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(0x0E), 3);            // songs
    BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(0x10), 1);            // start song
    BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(0x12), 0);           // speed
    WriteField(file, 0x16, "Test Tune");
    WriteField(file, 0x36, "Test Author");
    WriteField(file, 0x56, "2026 Workbench");
    // v2 flags: chip model MOS8580 (bits 4-5 = 2 → <<4), PAL (bits 2-3 = 1 → <<2).
    BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(0x76), (2 << 4) | (1 << 2));

    Array.Copy(program, 0, file, dataOffset, program.Length);
    return file;
  }

  private static void WriteField(byte[] file, int offset, string value) {
    var bytes = Encoding.Latin1.GetBytes(value);
    Array.Copy(bytes, 0, file, offset, Math.Min(bytes.Length, 32));
  }

  [Test, Category("HappyPath")]
  public void List_ExposesFullMetadataAndProgram() {
    var sample = BuildSample(out _);
    var desc = new SidFormatDescriptor();
    using var ms = new MemoryStream(sample);
    var entries = desc.List(ms, null);

    Assert.That(entries[0].Name, Is.EqualTo("FULL.sid"));
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "program.prg"), Is.True);
  }

  [Test, Category("RoundTrip")]
  public void Extract_FullIsByteIdentical_AndMetadataParsed() {
    var sample = BuildSample(out var program);
    var desc = new SidFormatDescriptor();
    var dir = Path.Combine(Path.GetTempPath(), "sid_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      using (var ms = new MemoryStream(sample))
        desc.Extract(ms, dir, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(dir, "FULL.sid")), Is.EqualTo(sample));
      Assert.That(File.ReadAllBytes(Path.Combine(dir, "program.prg")), Is.EqualTo(program));
      var meta = File.ReadAllText(Path.Combine(dir, "metadata.ini"));
      Assert.That(meta, Does.Contain("name = Test Tune"));
      Assert.That(meta, Does.Contain("author = Test Author"));
      Assert.That(meta, Does.Contain("songs = 3"));
      Assert.That(meta, Does.Contain("chip_model = MOS8580"));
      Assert.That(meta, Does.Contain("clock = PAL"));
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
  }

  [Test, Category("ErrorHandling")]
  public void List_Malformed_DoesNotThrow() {
    var garbage = new byte[16];
    Array.Fill(garbage, (byte)0x55);
    var desc = new SidFormatDescriptor();
    using var ms = new MemoryStream(garbage);
    List<ArchiveEntryInfo> entries = null!;
    Assert.That(() => entries = desc.List(ms, null), Throws.Nothing);
    Assert.That(entries[0].Name, Is.EqualTo("FULL.sid"));
  }

  [Test, Category("Detection")]
  public void Magic_MatchesPsidAndRsid() {
    var desc = new SidFormatDescriptor();
    var sigs = desc.MagicSignatures.Select(s => Encoding.ASCII.GetString(s.Bytes)).ToList();
    Assert.That(sigs, Does.Contain("PSID"));
    Assert.That(sigs, Does.Contain("RSID"));
  }
}
