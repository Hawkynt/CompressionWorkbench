using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.Ktx2;

namespace Compression.Tests.Ktx2;

[TestFixture]
public class Ktx2Tests {

  // Builds a minimal valid KTX2: identifier + header + 2-level index + KVD + level data.
  private static byte[] BuildSample(out byte[] level0, out byte[] level1) {
    level0 = [0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88];
    level1 = [0xAA, 0xBB, 0xCC, 0xDD];

    // KVD: one entry "KTXorientation\0rd" padded to 4 bytes.
    var key = "KTXorientation"u8.ToArray();
    var val = "rd"u8.ToArray();
    var kvEntry = new byte[key.Length + 1 + val.Length + 1];
    Array.Copy(key, kvEntry, key.Length);
    kvEntry[key.Length] = 0;
    Array.Copy(val, 0, kvEntry, key.Length + 1, val.Length);
    kvEntry[^1] = 0;
    var kvdPayload = new byte[4 + kvEntry.Length];
    BinaryPrimitives.WriteUInt32LittleEndian(kvdPayload, (uint)kvEntry.Length);
    Array.Copy(kvEntry, 0, kvdPayload, 4, kvEntry.Length);
    while (kvdPayload.Length % 4 != 0) Array.Resize(ref kvdPayload, kvdPayload.Length + 1);

    var levelCount = 2;
    var headerSize = 80;
    var indexSize = levelCount * 24;
    var kvdOffset = headerSize + indexSize;
    var level0Offset = kvdOffset + kvdPayload.Length;
    var level1Offset = level0Offset + level0.Length;
    var total = level1Offset + level1.Length;

    var file = new byte[total];
    Ktx2Decomposer.Identifier.CopyTo(file);
    BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(12), 43); // vkFormat (R8G8B8A8)
    BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(16), 1);  // typeSize
    BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(20), 16); // pixelWidth
    BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(24), 8);  // pixelHeight
    BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(28), 0);  // pixelDepth
    BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(32), 0);  // layerCount
    BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(36), 1);  // faceCount
    BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(40), (uint)levelCount);
    BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(44), 0);  // supercompression: none
    // Section index: DFD off/len = 0, KVD off/len, SGD off/len = 0.
    BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(56), (uint)kvdOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(60), (uint)kvdPayload.Length);

    // Level index.
    var idx = headerSize;
    BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(idx), (ulong)level0Offset);
    BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(idx + 8), (ulong)level0.Length);
    BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(idx + 16), (ulong)level0.Length);
    idx += 24;
    BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(idx), (ulong)level1Offset);
    BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(idx + 8), (ulong)level1.Length);
    BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(idx + 16), (ulong)level1.Length);

    Array.Copy(kvdPayload, 0, file, kvdOffset, kvdPayload.Length);
    Array.Copy(level0, 0, file, level0Offset, level0.Length);
    Array.Copy(level1, 0, file, level1Offset, level1.Length);
    return file;
  }

  [Test, Category("HappyPath")]
  public void List_ExposesFullMetadataAndLevels() {
    var sample = BuildSample(out var level0, out var level1);
    var desc = new Ktx2FormatDescriptor();
    using var ms = new MemoryStream(sample);
    var entries = desc.List(ms, null);

    Assert.That(entries[0].Name, Is.EqualTo("FULL.ktx2"));
    Assert.That(entries[0].Kind, Is.EqualTo("Track"));
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "levels/level_00.bin"), Is.True);
    Assert.That(entries.Any(e => e.Name == "levels/level_01.bin"), Is.True);
    Assert.That(entries.Any(e => e.Name == "kvd.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "kvd.bin"), Is.True);
    var lvl = entries.First(e => e.Name == "levels/level_00.bin");
    Assert.That(lvl.OriginalSize, Is.EqualTo(level0.Length));
    Assert.That(entries.First(e => e.Name == "levels/level_01.bin").OriginalSize, Is.EqualTo(level1.Length));
  }

  [Test, Category("RoundTrip")]
  public void Extract_FullIsByteIdentical_AndLevelsMatch() {
    var sample = BuildSample(out var level0, out var level1);
    var desc = new Ktx2FormatDescriptor();
    var dir = Path.Combine(Path.GetTempPath(), "ktx2_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      using (var ms = new MemoryStream(sample))
        desc.Extract(ms, dir, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(dir, "FULL.ktx2")), Is.EqualTo(sample));
      Assert.That(File.ReadAllBytes(Path.Combine(dir, "levels", "level_00.bin")), Is.EqualTo(level0));
      Assert.That(File.ReadAllBytes(Path.Combine(dir, "levels", "level_01.bin")), Is.EqualTo(level1));
      var meta = File.ReadAllText(Path.Combine(dir, "metadata.ini"));
      Assert.That(meta, Does.Contain("pixel_width = 16"));
      Assert.That(meta, Does.Contain("supercompression_scheme = none"));
      var kvd = File.ReadAllText(Path.Combine(dir, "kvd.ini"));
      Assert.That(kvd, Does.Contain("KTXorientation"));
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
  }

  [Test, Category("ErrorHandling")]
  public void List_Malformed_DoesNotThrow_AndMarksPartial() {
    var garbage = Encoding.ASCII.GetBytes("not a ktx2 file at all, just text");
    var desc = new Ktx2FormatDescriptor();
    using var ms = new MemoryStream(garbage);
    List<ArchiveEntryInfo> entries = null!;
    Assert.That(() => entries = desc.List(ms, null), Throws.Nothing);
    Assert.That(entries[0].Name, Is.EqualTo("FULL.ktx2"));
    var meta = entries.First(e => e.Name == "metadata.ini");
    Assert.That(meta.OriginalSize, Is.GreaterThan(0));
  }

  [Test, Category("Detection")]
  public void Magic_MatchesIdentifier() {
    var desc = new Ktx2FormatDescriptor();
    var sig = desc.MagicSignatures[0].Bytes;
    Assert.That(sig.AsSpan().SequenceEqual(Ktx2Decomposer.Identifier), Is.True);
  }
}
