#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Avi;

namespace Compression.Tests.Avi;

[TestFixture]
public class AviFrameExtractionTests {

  /// <summary>Synthesises an AVI with MJPEG video (2 frames) + PCM audio.</summary>
  private static byte[] MakeMjpegAvi(int frameCount = 2) {
    // ── avih (56 bytes) ────────────────────────────────────────
    var avih = new byte[56];
    BinaryPrimitives.WriteUInt32LittleEndian(avih.AsSpan(0), 33333);    // microseconds per frame
    BinaryPrimitives.WriteUInt32LittleEndian(avih.AsSpan(16), (uint)frameCount);
    BinaryPrimitives.WriteUInt32LittleEndian(avih.AsSpan(24), 1);        // streams
    BinaryPrimitives.WriteUInt32LittleEndian(avih.AsSpan(32), 320);      // width
    BinaryPrimitives.WriteUInt32LittleEndian(avih.AsSpan(36), 240);      // height

    // ── Video strh (56 bytes) ─────────────────────────────────
    var vidStrh = new byte[56];
    "vids"u8.CopyTo(vidStrh.AsSpan(0));
    "MJPG"u8.CopyTo(vidStrh.AsSpan(4));

    // ── Video strf = BITMAPINFOHEADER (40 bytes) ──────────────
    var vidStrf = new byte[40];
    BinaryPrimitives.WriteUInt32LittleEndian(vidStrf.AsSpan(0), 40);
    BinaryPrimitives.WriteUInt32LittleEndian(vidStrf.AsSpan(4), 320);
    BinaryPrimitives.WriteUInt32LittleEndian(vidStrf.AsSpan(8), 240);
    BinaryPrimitives.WriteUInt16LittleEndian(vidStrf.AsSpan(12), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(vidStrf.AsSpan(14), 24);
    "MJPG"u8.CopyTo(vidStrf.AsSpan(16));

    var vidStrl = BuildList("strl",
      BuildChunk("strh", vidStrh),
      BuildChunk("strf", vidStrf));

    var hdrl = BuildList("hdrl",
      BuildChunk("avih", avih),
      vidStrl);

    // ── movi list with N video frames ────────────────────────
    var moviChunks = new List<byte[]>();
    for (var i = 0; i < frameCount; ++i) {
      // Minimal fake JPEG: starts with FFD8 (SOI), ends with FFD9 (EOI)
      var jpegData = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, (byte)(i + 1), 0xFF, 0xD9 };
      moviChunks.Add(BuildChunk("00dc", jpegData));
    }
    var movi = BuildList("movi", moviChunks.ToArray());

    // ── RIFF wrap ─────────────────────────────────────────────
    using var mem = new MemoryStream();
    mem.Write("RIFF"u8);
    var inner = new byte[4 + hdrl.Length + movi.Length];
    "AVI "u8.CopyTo(inner.AsSpan(0));
    hdrl.CopyTo(inner.AsSpan(4));
    movi.CopyTo(inner.AsSpan(4 + hdrl.Length));
    var sizeBytes = new byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(sizeBytes, (uint)inner.Length);
    mem.Write(sizeBytes);
    mem.Write(inner);
    return mem.ToArray();
  }

  /// <summary>Synthesises an AVI with uncompressed DIB video (2 frames).</summary>
  private static byte[] MakeDibAvi(int frameCount = 2) {
    var avih = new byte[56];
    BinaryPrimitives.WriteUInt32LittleEndian(avih.AsSpan(0), 33333);
    BinaryPrimitives.WriteUInt32LittleEndian(avih.AsSpan(16), (uint)frameCount);
    BinaryPrimitives.WriteUInt32LittleEndian(avih.AsSpan(24), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(avih.AsSpan(32), 4);   // width
    BinaryPrimitives.WriteUInt32LittleEndian(avih.AsSpan(36), 2);   // height

    // Handler = 0 (DIB/uncompressed, equivalent to "    " or all-zero fourcc)
    var vidStrh = new byte[56];
    "vids"u8.CopyTo(vidStrh.AsSpan(0));
    // handler = 0 → all-zero fourcc → uncompressed

    var vidStrf = new byte[40];
    BinaryPrimitives.WriteUInt32LittleEndian(vidStrf.AsSpan(0), 40);
    BinaryPrimitives.WriteUInt32LittleEndian(vidStrf.AsSpan(4), 4);    // width
    BinaryPrimitives.WriteUInt32LittleEndian(vidStrf.AsSpan(8), 2);    // height
    BinaryPrimitives.WriteUInt16LittleEndian(vidStrf.AsSpan(12), 1);   // planes
    BinaryPrimitives.WriteUInt16LittleEndian(vidStrf.AsSpan(14), 24);  // 24bpp

    var vidStrl = BuildList("strl",
      BuildChunk("strh", vidStrh),
      BuildChunk("strf", vidStrf));

    var hdrl = BuildList("hdrl",
      BuildChunk("avih", avih),
      vidStrl);

    // 4×2 24bpp → row=12, padded to 12 (multiple of 4). 2 rows = 24 bytes per frame.
    var moviChunks = new List<byte[]>();
    for (var i = 0; i < frameCount; ++i) {
      var frame = new byte[24];
      Array.Fill(frame, (byte)(0x10 + i));
      moviChunks.Add(BuildChunk("00dc", frame));
    }
    var movi = BuildList("movi", moviChunks.ToArray());

    using var mem = new MemoryStream();
    mem.Write("RIFF"u8);
    var inner = new byte[4 + hdrl.Length + movi.Length];
    "AVI "u8.CopyTo(inner.AsSpan(0));
    hdrl.CopyTo(inner.AsSpan(4));
    movi.CopyTo(inner.AsSpan(4 + hdrl.Length));
    var sizeBytes = new byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(sizeBytes, (uint)inner.Length);
    mem.Write(sizeBytes);
    mem.Write(inner);
    return mem.ToArray();
  }

  private static byte[] BuildChunk(string id, byte[] body) {
    var sizeAligned = body.Length + (body.Length & 1);
    var chunk = new byte[8 + sizeAligned];
    Encoding.ASCII.GetBytes(id).CopyTo(chunk.AsSpan(0));
    BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4), (uint)body.Length);
    body.CopyTo(chunk.AsSpan(8));
    return chunk;
  }

  private static byte[] BuildList(string listType, params byte[][] children) {
    var bodyLen = 4 + children.Sum(c => c.Length);
    var chunk = new byte[8 + bodyLen];
    "LIST"u8.CopyTo(chunk.AsSpan(0));
    BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4), (uint)bodyLen);
    Encoding.ASCII.GetBytes(listType).CopyTo(chunk.AsSpan(8));
    var off = 12;
    foreach (var c in children) { c.CopyTo(chunk.AsSpan(off)); off += c.Length; }
    return chunk;
  }

  // ── AviReader per-frame chunk tests ─────────────────────────

  [Test]
  public void AviReader_MjpegChunks_PreservesFrameBoundaries() {
    var blob = MakeMjpegAvi(3);
    var parsed = new AviReader().Read(blob);
    var vidTrack = parsed.Tracks.First(t => t.StreamType == "vids");
    Assert.That(vidTrack.Chunks.Count, Is.EqualTo(3));
    for (var i = 0; i < 3; ++i) {
      Assert.That(vidTrack.Chunks[i].ChunkId, Is.EqualTo("00dc"));
      Assert.That(vidTrack.Chunks[i].Data[0], Is.EqualTo(0xFF));
      Assert.That(vidTrack.Chunks[i].Data[1], Is.EqualTo(0xD8));
    }
  }

  [Test]
  public void AviReader_DibChunks_PreservesFrameBoundaries() {
    var blob = MakeDibAvi(2);
    var parsed = new AviReader().Read(blob);
    var vidTrack = parsed.Tracks.First(t => t.StreamType == "vids");
    Assert.That(vidTrack.Chunks.Count, Is.EqualTo(2));
    Assert.That(vidTrack.Chunks[0].Data[0], Is.EqualTo(0x10));
    Assert.That(vidTrack.Chunks[1].Data[0], Is.EqualTo(0x11));
  }

  // ── Descriptor frame entry tests ───────────────────────────

  [Test]
  public void Descriptor_MjpegAvi_ListsFrameEntries() {
    var blob = MakeMjpegAvi(3);
    using var ms = new MemoryStream(blob);
    var entries = new AviFormatDescriptor().List(ms, null);
    var frameEntries = entries.Where(e => e.Kind == "Frame").ToList();
    Assert.That(frameEntries.Count, Is.EqualTo(3));
    Assert.That(frameEntries[0].Name, Does.StartWith("frames/track_00/frame_000001"));
    Assert.That(frameEntries[0].Name, Does.EndWith(".jpg"));
  }

  [Test]
  public void Descriptor_MjpegAvi_FrameExtensionIsJpg() {
    var blob = MakeMjpegAvi(1);
    using var ms = new MemoryStream(blob);
    var entries = new AviFormatDescriptor().List(ms, null);
    var frame = entries.First(e => e.Kind == "Frame");
    Assert.That(frame.Name, Does.EndWith(".jpg"));
  }

  [Test]
  public void Descriptor_DibAvi_FrameExtensionIsBmp() {
    var blob = MakeDibAvi(1);
    using var ms = new MemoryStream(blob);
    var entries = new AviFormatDescriptor().List(ms, null);
    var frame = entries.First(e => e.Kind == "Frame");
    Assert.That(frame.Name, Does.EndWith(".bmp"));
  }

  [Test]
  public void Descriptor_DibAvi_FrameWrappedAsBmp() {
    var blob = MakeDibAvi(1);
    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    var desc = new AviFormatDescriptor();
    var entries = desc.List(ms, null);
    var frameName = entries.First(e => e.Kind == "Frame").Name;

    ms.Position = 0;
    desc.ExtractEntry(ms, frameName, output, null);
    var bmp = output.ToArray();

    // BMP header check
    Assert.That(bmp[0], Is.EqualTo((byte)'B'));
    Assert.That(bmp[1], Is.EqualTo((byte)'M'));
    // Width = 4 at offset 18 (little-endian)
    Assert.That(BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(18)), Is.EqualTo(4));
    // Height = 2 at offset 22
    Assert.That(BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(22)), Is.EqualTo(2));
  }

  [Test]
  public void Descriptor_MjpegAvi_ExtractFrame_ReturnsJpegData() {
    var blob = MakeMjpegAvi(2);
    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    var desc = new AviFormatDescriptor();
    var entries = desc.List(ms, null);
    var frameName = entries.First(e => e.Kind == "Frame").Name;

    ms.Position = 0;
    desc.ExtractEntry(ms, frameName, output, null);
    var data = output.ToArray();
    // Should start with JPEG SOI marker
    Assert.That(data[0], Is.EqualTo(0xFF));
    Assert.That(data[1], Is.EqualTo(0xD8));
  }

  [Test]
  public void Descriptor_MetadataIni_ContainsFrameCount() {
    var blob = MakeMjpegAvi(5);
    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    new AviFormatDescriptor().ExtractEntry(ms, "metadata.ini", output, null);
    var text = Encoding.UTF8.GetString(output.ToArray());
    Assert.That(text, Does.Contain("frame_count=5"));
  }

  [Test]
  public void Descriptor_TrackEntryStillPresent() {
    // The track-level video blob entry should still exist alongside frame entries
    var blob = MakeMjpegAvi(2);
    using var ms = new MemoryStream(blob);
    var entries = new AviFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name.Contains("track_00_video") && e.Kind == "Track"), Is.True);
    Assert.That(entries.Any(e => e.Kind == "Frame"), Is.True);
  }
}
