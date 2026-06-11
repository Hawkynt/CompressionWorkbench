#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.PngCrushAdapters;

namespace Compression.Tests.Png;

/// <summary>
/// Behaviour of <see cref="PngFormatDescriptor"/> as a chunk-structured
/// pseudo-archive: FULL.png + metadata.ini + per-chunk entries + text/embedded
/// metadata side-cars. Uses a synthetic minimal valid PNG so the test has no
/// external-file dependency.
/// </summary>
[TestFixture]
public class PngPseudoArchiveTests {

  // ── Synthetic sample ────────────────────────────────────────────────────────

  private static byte[] BuildPng() {
    using var ms = new MemoryStream();
    ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

    // IHDR: 4x3, 8-bit, color type 2 (truecolor)
    var ihdr = new byte[13];
    BinaryPrimitives.WriteUInt32BigEndian(ihdr, 4);
    BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), 3);
    ihdr[8] = 8;  // bit depth
    ihdr[9] = 2;  // color type
    WriteChunk(ms, "IHDR", ihdr);

    WriteChunk(ms, "tEXt", Encoding.Latin1.GetBytes("Comment\0hello world"));
    WriteChunk(ms, "IDAT", [0x78, 0x9C, 0x01, 0x00, 0x00, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x01]);
    WriteChunk(ms, "IEND", []);
    return ms.ToArray();
  }

  private static void WriteChunk(Stream s, string type, byte[] data) {
    Span<byte> len = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
    s.Write(len);
    var typeBytes = Encoding.ASCII.GetBytes(type);
    s.Write(typeBytes);
    s.Write(data);
    var crc = Crc32(typeBytes, data);
    Span<byte> crcBuf = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(crcBuf, crc);
    s.Write(crcBuf);
  }

  private static uint Crc32(byte[] type, byte[] data) {
    uint crc = 0xFFFFFFFF;
    foreach (var b in type) crc = Step(crc, b);
    foreach (var b in data) crc = Step(crc, b);
    return crc ^ 0xFFFFFFFF;
    static uint Step(uint crc, byte b) {
      crc ^= b;
      for (var i = 0; i < 8; i++)
        crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
      return crc;
    }
  }

  // ── List ────────────────────────────────────────────────────────────────────

  [Test]
  public void List_Exposes_Full_Metadata_And_Chunks() {
    var desc = new PngFormatDescriptor();
    using var s = new MemoryStream(BuildPng());

    var entries = desc.List(s, null);
    var names = entries.Select(e => e.Name).ToList();

    Assert.Multiple(() => {
      Assert.That(names, Does.Contain("FULL.png"));
      Assert.That(names, Does.Contain("metadata.ini"));
      Assert.That(names, Has.Some.StartsWith("chunks/").And.Some.Contains("IHDR"));
      Assert.That(names, Has.Some.Contains("IDAT"));
      Assert.That(names, Has.Some.Contains("IEND"));
      Assert.That(names, Does.Contain("comments.txt"));
    });

    Assert.That(entries.First(e => e.Name == "FULL.png").Kind, Is.EqualTo("Track"));
    Assert.That(entries.First(e => e.Name == "metadata.ini").Kind, Is.EqualTo("Tag"));
    Assert.That(entries.First(e => e.Name.Contains("IHDR")).Kind, Is.EqualTo("Chunk"));
  }

  // ── Extract ──────────────────────────────────────────────────────────────────

  [Test]
  public void Extract_Writes_Full_ByteIdentical_And_Decomposed() {
    var original = BuildPng();
    var desc = new PngFormatDescriptor();
    using var s = new MemoryStream(original);
    var outDir = Path.Combine(Path.GetTempPath(), $"cwb_png_{Guid.NewGuid():N}");
    try {
      desc.Extract(s, outDir, null, null);
      var full = File.ReadAllBytes(Path.Combine(outDir, "FULL.png"));
      Assert.That(full, Is.EqualTo(original), "FULL.png must be byte-identical to the source.");
      Assert.That(File.Exists(Path.Combine(outDir, "metadata.ini")), Is.True);
      Assert.That(Directory.GetFiles(Path.Combine(outDir, "chunks")), Is.Not.Empty);
      var comments = File.ReadAllText(Path.Combine(outDir, "comments.txt"));
      Assert.That(comments, Does.Contain("hello world"));
    } finally {
      try { Directory.Delete(outDir, true); } catch { /* best effort */ }
    }
  }

  [Test]
  public void Metadata_Records_Ihdr_Dimensions() {
    var desc = new PngFormatDescriptor();
    using var s = new MemoryStream(BuildPng());
    using var ms = new MemoryStream();
    ((IArchiveInMemoryExtract)desc).ExtractEntry(s, "metadata.ini", ms, null);
    var ini = Encoding.UTF8.GetString(ms.ToArray());
    Assert.Multiple(() => {
      Assert.That(ini, Does.Contain("width = 4"));
      Assert.That(ini, Does.Contain("height = 3"));
      Assert.That(ini, Does.Contain("bit_depth = 8"));
      Assert.That(ini, Does.Contain("color_type = 2"));
      Assert.That(ini, Does.Not.Contain("parse_status = partial"));
    });
  }

  // ── Malformed ────────────────────────────────────────────────────────────────

  [Test]
  public void List_DoesNotThrow_On_Malformed() {
    var desc = new PngFormatDescriptor();
    using var s = new MemoryStream([0x89, 0x50, 0x4E, 0x47, 0xFF, 0xFF, 0xFF]);
    List<ArchiveEntryInfo> entries = null!;
    Assert.DoesNotThrow(() => entries = desc.List(s, null));
    Assert.That(entries.Select(e => e.Name), Does.Contain("FULL.png"));
    Assert.That(entries.Select(e => e.Name), Does.Contain("metadata.ini"));
  }

  [Test]
  public void Truncated_Png_Marks_Partial_ParseStatus() {
    // Valid signature + IHDR but no IEND → structural walk doesn't complete.
    var full = BuildPng();
    var truncated = full.AsSpan(0, 8 + 25).ToArray(); // sig + IHDR chunk only
    var desc = new PngFormatDescriptor();
    using var s = new MemoryStream(truncated);
    using var ms = new MemoryStream();
    ((IArchiveInMemoryExtract)desc).ExtractEntry(s, "metadata.ini", ms, null);
    var ini = Encoding.UTF8.GetString(ms.ToArray());
    Assert.That(ini, Does.Contain("parse_status = partial"));
  }
}
