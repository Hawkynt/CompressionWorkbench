#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using FileFormat.PngCrushAdapters;

namespace Compression.Tests.Tiff;

[TestFixture]
public class TiffLayoutMapTests {

  /// <summary>
  /// Builds a minimal little-endian TIFF with one IFD and one strip of pixel data.
  /// </summary>
  private static MemoryStream BuildTestTiff(int stripCount = 1) {
    var ms = new MemoryStream();
    var data = new List<byte>();

    // TIFF header (8 bytes)
    data.AddRange("II"u8.ToArray()); // little-endian
    AddU16LE(data, 0x002A);          // magic
    AddU32LE(data, 0);               // IFD0 offset (patched later)

    // We'll place the IFD right after the header at offset 8
    var ifdOffset = data.Count;
    PatchU32LE(data, 4, (uint)ifdOffset);

    // IFD with essential tags:
    // ImageWidth (0x0100), ImageLength (0x0101), StripOffsets (0x0111), StripByteCounts (0x0117)
    ushort entryCount = 4;
    AddU16LE(data, entryCount);

    // Tag: ImageWidth = 8
    AddIfdEntry(data, 0x0100, 3, 1, 8); // SHORT, count=1, value=8
    // Tag: ImageLength = 8
    AddIfdEntry(data, 0x0101, 3, 1, 8);

    if (stripCount == 1) {
      // StripOffsets: single value inline (we'll patch later)
      AddIfdEntry(data, 0x0111, 4, 1, 0); // LONG, count=1, placeholder
      var stripOffsetEntryValuePos = data.Count - 4;

      // StripByteCounts: single value inline
      var stripSize = 64;
      AddIfdEntry(data, 0x0117, 4, 1, (uint)stripSize);

      // Next IFD = 0 (no more IFDs)
      AddU32LE(data, 0);

      // Strip data immediately after IFD
      var stripDataOffset = data.Count;
      PatchU32LE(data, stripOffsetEntryValuePos, (uint)stripDataOffset);
      for (var i = 0; i < stripSize; i++) data.Add((byte)(i & 0xFF));
    } else {
      // Multiple strips: offsets and byte counts stored out-of-band
      var stripSize = 32;

      // StripOffsets: LONG array at an offset
      AddIfdEntry(data, 0x0111, 4, (uint)stripCount, 0); // placeholder for offset pointer
      var stripOffsetsPointerPos = data.Count - 4;

      // StripByteCounts: LONG array at an offset
      AddIfdEntry(data, 0x0117, 4, (uint)stripCount, 0); // placeholder for offset pointer
      var stripCountsPointerPos = data.Count - 4;

      // Next IFD = 0
      AddU32LE(data, 0);

      // Strip offsets array
      var stripOffsetsStart = data.Count;
      PatchU32LE(data, stripOffsetsPointerPos, (uint)stripOffsetsStart);
      var stripOffsets = new List<int>();
      for (var i = 0; i < stripCount; i++) {
        AddU32LE(data, 0); // placeholder
        stripOffsets.Add(data.Count - 4);
      }

      // Strip byte counts array
      var stripCountsStart = data.Count;
      PatchU32LE(data, stripCountsPointerPos, (uint)stripCountsStart);
      for (var i = 0; i < stripCount; i++)
        AddU32LE(data, (uint)stripSize);

      // Actual strip data
      for (var i = 0; i < stripCount; i++) {
        var stripDataOff = data.Count;
        PatchU32LE(data, stripOffsets[i], (uint)stripDataOff);
        for (var j = 0; j < stripSize; j++) data.Add((byte)((i * 32 + j) & 0xFF));
      }
    }

    ms.Write(data.ToArray());
    ms.Position = 0;
    return ms;
  }

  private static void AddU16LE(List<byte> data, ushort value) {
    data.Add((byte)(value & 0xFF));
    data.Add((byte)(value >> 8));
  }

  private static void AddU32LE(List<byte> data, uint value) {
    data.Add((byte)(value & 0xFF));
    data.Add((byte)((value >> 8) & 0xFF));
    data.Add((byte)((value >> 16) & 0xFF));
    data.Add((byte)((value >> 24) & 0xFF));
  }

  private static void PatchU32LE(List<byte> data, int offset, uint value) {
    data[offset] = (byte)(value & 0xFF);
    data[offset + 1] = (byte)((value >> 8) & 0xFF);
    data[offset + 2] = (byte)((value >> 16) & 0xFF);
    data[offset + 3] = (byte)((value >> 24) & 0xFF);
  }

  private static void AddIfdEntry(List<byte> data, ushort tag, ushort type, uint count, uint value) {
    AddU16LE(data, tag);
    AddU16LE(data, type);
    AddU32LE(data, count);
    AddU32LE(data, value);
  }

  [Test]
  public void EnumerateChunks_HasTiffHeader() {
    using var ms = BuildTestTiff();
    var chunks = TiffLayoutMap.Enumerate(ms).ToList();

    var header = chunks.FirstOrDefault(c => c.FileName != null && c.FileName.Contains("header"));
    Assert.That(header, Is.Not.Null);
    Assert.That(header!.Kind, Is.EqualTo(DefragBlockKind.MetadataReserved));
    Assert.That(header.Offset, Is.EqualTo(0));
    Assert.That(header.Length, Is.EqualTo(8));
  }

  [Test]
  public void EnumerateChunks_HasIfd() {
    using var ms = BuildTestTiff();
    var chunks = TiffLayoutMap.Enumerate(ms).ToList();

    var ifd = chunks.FirstOrDefault(c => c.FileName != null && c.FileName.Contains("IFD"));
    Assert.That(ifd, Is.Not.Null);
    Assert.That(ifd!.Kind, Is.EqualTo(DefragBlockKind.MetadataReserved));
  }

  [Test]
  public void EnumerateChunks_HasStripData() {
    using var ms = BuildTestTiff();
    var chunks = TiffLayoutMap.Enumerate(ms).ToList();

    var strips = chunks.Where(c => c.FileName != null && c.FileName.Contains("strip")).ToList();
    Assert.That(strips, Has.Count.EqualTo(1));
    Assert.That(strips[0].Kind, Is.EqualTo(DefragBlockKind.Used));
    Assert.That(strips[0].Length, Is.EqualTo(64));
  }

  [Test]
  public void EnumerateChunks_MultipleStrips() {
    using var ms = BuildTestTiff(stripCount: 4);
    var chunks = TiffLayoutMap.Enumerate(ms).ToList();

    var strips = chunks.Where(c => c.FileName != null && c.FileName.Contains("strip")).ToList();
    Assert.That(strips, Has.Count.EqualTo(4));
    foreach (var s in strips) {
      Assert.That(s.Kind, Is.EqualTo(DefragBlockKind.Used));
      Assert.That(s.Length, Is.EqualTo(32));
    }
  }

  [Test]
  public void EnumerateChunks_EmptyStream_ReturnsNothing() {
    using var ms = new MemoryStream();
    var chunks = TiffLayoutMap.Enumerate(ms).ToList();
    Assert.That(chunks, Is.Empty);
  }

  [Test]
  public void EnumerateChunks_InvalidMagic_ReturnsNothing() {
    using var ms = new MemoryStream(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });
    var chunks = TiffLayoutMap.Enumerate(ms).ToList();
    Assert.That(chunks, Is.Empty);
  }
}
