using System.Buffers.Binary;
using System.IO.Compression;
using Compression.Registry;
using FileFormat.Vgm;

namespace Compression.Tests.Vgm;

[TestFixture]
public class VgmTests {

  // Builds a minimal valid VGM: 0x40-byte header + command stream + GD3 tag.
  private static byte[] BuildSample(out byte[] commandStream) {
    commandStream = [0x50, 0x80, 0x50, 0x90, 0x66 /* end-of-sound-data */];

    // GD3 v1.00 tag: "Gd3 " + version + length + 11 UTF-16LE NUL-terminated strings.
    var fields = new[] {
      "Test Title", "", "Test Game", "", "Sega Mega Drive", "",
      "Test Author", "", "2026", "Workbench", "notes here",
    };
    using var gms = new MemoryStream();
    using (var bw = new BinaryWriter(gms)) {
      foreach (var f in fields) {
        foreach (var ch in f) bw.Write((ushort)ch);
        bw.Write((ushort)0);
      }
    }
    var gd3Strings = gms.ToArray();
    var gd3 = new byte[12 + gd3Strings.Length];
    BinaryPrimitives.WriteUInt32LittleEndian(gd3, 0x20336447); // "Gd3 "
    BinaryPrimitives.WriteUInt32LittleEndian(gd3.AsSpan(4), 0x00000100);
    BinaryPrimitives.WriteUInt32LittleEndian(gd3.AsSpan(8), (uint)gd3Strings.Length);
    Array.Copy(gd3Strings, 0, gd3, 12, gd3Strings.Length);

    var dataStart = 0x40;
    var gd3Abs = dataStart + commandStream.Length;
    var total = gd3Abs + gd3.Length;
    var file = new byte[total];
    BinaryPrimitives.WriteUInt32LittleEndian(file, 0x206D6756); // "Vgm "
    BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x04), (uint)(total - 4)); // EOF offset
    BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x08), 0x00000150); // version 1.50
    BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x0C), 3579545);    // SN76489 clock
    BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x2C), 7670454);    // YM2612 clock
    BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x14), (uint)(gd3Abs - 0x14)); // GD3 rel offset
    BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x18), 12345);      // total samples
    BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x34), 0);          // data offset (0 → 0x40)

    Array.Copy(commandStream, 0, file, dataStart, commandStream.Length);
    Array.Copy(gd3, 0, file, gd3Abs, gd3.Length);
    return file;
  }

  [Test, Category("HappyPath")]
  public void List_ExposesFullMetadataGd3AndStream() {
    var sample = BuildSample(out _);
    var desc = new VgmFormatDescriptor();
    using var ms = new MemoryStream(sample);
    var entries = desc.List(ms, null);

    Assert.That(entries[0].Name, Is.EqualTo("FULL.vgm"));
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "command_stream.bin"), Is.True);
    Assert.That(entries.Any(e => e.Name == "gd3.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "gd3.bin"), Is.True);
  }

  [Test, Category("RoundTrip")]
  public void Extract_FullIsByteIdentical_AndMetadataParsed() {
    var sample = BuildSample(out var commandStream);
    var desc = new VgmFormatDescriptor();
    var dir = Path.Combine(Path.GetTempPath(), "vgm_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      using (var ms = new MemoryStream(sample))
        desc.Extract(ms, dir, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(dir, "FULL.vgm")), Is.EqualTo(sample));
      Assert.That(File.ReadAllBytes(Path.Combine(dir, "command_stream.bin")), Is.EqualTo(commandStream));
      var meta = File.ReadAllText(Path.Combine(dir, "metadata.ini"));
      Assert.That(meta, Does.Contain("active_chips"));
      Assert.That(meta, Does.Contain("SN76489"));
      Assert.That(meta, Does.Contain("YM2612"));
      Assert.That(meta, Does.Contain("total_samples = 12345"));
      var gd3 = File.ReadAllText(Path.Combine(dir, "gd3.ini"));
      Assert.That(gd3, Does.Contain("Test Title"));
      Assert.That(gd3, Does.Contain("Test Author"));
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
  }

  [Test, Category("HappyPath"), Category("Vgz")]
  public void List_GzipWrappedVgz_GunzipsHeader_FullKeepsGzippedBytes() {
    var inner = BuildSample(out _);
    using var gms = new MemoryStream();
    using (var gz = new GZipStream(gms, CompressionLevel.Optimal, leaveOpen: true))
      gz.Write(inner, 0, inner.Length);
    var vgz = gms.ToArray();

    var desc = new VgmFormatDescriptor();
    using var ms = new MemoryStream(vgz);
    var entries = desc.List(ms, null);

    // FULL is the gzipped bytes, not the inner VGM.
    var dir = Path.Combine(Path.GetTempPath(), "vgz_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      using (var ms2 = new MemoryStream(vgz))
        desc.Extract(ms2, dir, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(dir, "FULL.vgm")), Is.EqualTo(vgz));
      var meta = File.ReadAllText(Path.Combine(dir, "metadata.ini"));
      Assert.That(meta, Does.Contain("gzip_wrapped = true"));
      Assert.That(meta, Does.Contain("SN76489"));
      Assert.That(entries.Any(e => e.Name == "command_stream.bin"), Is.True);
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
  }

  [Test, Category("ErrorHandling")]
  public void List_Malformed_DoesNotThrow() {
    var garbage = new byte[64];
    Array.Fill(garbage, (byte)0xCC);
    var desc = new VgmFormatDescriptor();
    using var ms = new MemoryStream(garbage);
    List<ArchiveEntryInfo> entries = null!;
    Assert.That(() => entries = desc.List(ms, null), Throws.Nothing);
    Assert.That(entries[0].Name, Is.EqualTo("FULL.vgm"));
  }

  [Test, Category("Detection")]
  public void Magic_MatchesVgm() {
    var desc = new VgmFormatDescriptor();
    Assert.That(desc.MagicSignatures[0].Bytes, Is.EqualTo("Vgm "u8.ToArray()));
  }
}
