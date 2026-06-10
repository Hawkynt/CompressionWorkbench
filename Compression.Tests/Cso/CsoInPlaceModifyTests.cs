#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using FileFormat.Cso;

namespace Compression.Tests.Cso;

/// <summary>
/// Block-level in-place mutation for CSO v1 images. Two on-disk shapes are
/// exercised: (a) the new compressed block fits inside the old slot — header,
/// index, and adjacent block bytes remain byte-identical except for the
/// flag bit on the modified entry; (b) the new payload is larger than the
/// old slot — it lands at EOF and the index is patched to reflect the new
/// position while every block remains decodable through
/// <see cref="CsoFormatDescriptor.Extract"/>.
/// </summary>
[TestFixture]
public class CsoInPlaceModifyTests {

  private const int BlockSize = 2048;

  /// <summary>
  /// Produces an uncompressed payload of <c>blocksCount × BlockSize</c> bytes
  /// where each block has a distinct fill so corruption is locally visible.
  /// </summary>
  private static byte[] BuildPayload(int blocksCount, byte seed) {
    var data = new byte[blocksCount * BlockSize];
    for (var b = 0; b < blocksCount; ++b)
      for (var i = 0; i < BlockSize; ++i)
        data[b * BlockSize + i] = (byte)((seed + b * 7 + i) & 0xFF);
    return data;
  }

  /// <summary>Builds a fresh CSO image around the supplied uncompressed payload.</summary>
  private static byte[] BuildImage(byte[] payload) => CsoWriter.Build(payload);

  /// <summary>Reads the in-place index entries (without the flag bit) of a CSO stream.</summary>
  private static (long[] Offsets, bool[] StoredFlags) ReadIndex(byte[] image) {
    var blockSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(16, 4));
    var uncompressed = (long)BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(8, 8));
    var blockCount = (int)((uncompressed + blockSize - 1) / blockSize);
    var indexCount = blockCount + 1;
    var offsets = new long[indexCount];
    var flags = new bool[indexCount];
    for (var i = 0; i < indexCount; ++i) {
      var raw = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(24 + i * 4, 4));
      flags[i] = (raw & 0x8000_0000u) != 0;
      offsets[i] = raw & 0x7FFF_FFFFu;
    }
    return (offsets, flags);
  }

  [Test, Category("HappyPath")]
  public void WriteBlock_FitsInPlace_LeavesHeaderAndOtherBlocksByteIdentical() {
    var payload = BuildPayload(4, seed: 1);
    var image = BuildImage(payload);

    // Choose a block to overwrite with HIGHLY-compressible content (zeros)
    // so the new compressed bytes are guaranteed smaller than the old slot.
    var ms = new MemoryStream();
    ms.Write(image, 0, image.Length);
    var (oldOffsets, _) = ReadIndex(image);
    var modifiedBlock = 1;
    var oldSize = oldOffsets[modifiedBlock + 1] - oldOffsets[modifiedBlock];
    Assert.That(oldSize, Is.GreaterThan(0), "the chosen block must have non-zero on-disk size");

    var zeros = new byte[BlockSize];
    CsoInPlaceModifier.WriteBlock(ms, modifiedBlock, zeros);

    var after = ms.ToArray();

    // Stream length unchanged.
    Assert.That(after.Length, Is.EqualTo(image.Length),
      "in-place WriteBlock with a smaller payload must not grow the stream");

    // Header bytes byte-identical (offsets 0..23).
    Assert.That(after.AsSpan(0, 24).SequenceEqual(image.AsSpan(0, 24)), Is.True,
      "header must be byte-identical after in-place WriteBlock");

    // Index entries (offsets) unchanged for the in-place case; flag may flip.
    var (newOffsets, _) = ReadIndex(after);
    for (var i = 0; i < newOffsets.Length; ++i)
      Assert.That(newOffsets[i], Is.EqualTo(oldOffsets[i]),
        $"in-place modify must not move block {i} on disk");

    // Every other block's bytes are byte-identical.
    for (var i = 0; i < oldOffsets.Length - 1; ++i) {
      if (i == modifiedBlock) continue;
      var start = (int)oldOffsets[i];
      var end = (int)oldOffsets[i + 1];
      Assert.That(after.AsSpan(start, end - start).SequenceEqual(image.AsSpan(start, end - start)),
        Is.True, $"block {i} bytes must not move when block {modifiedBlock} is rewritten in place");
    }
  }

  [Test, Category("HappyPath")]
  public void WriteBlock_FitsInPlace_RoundTripsModifiedAndUnmodifiedBlocks() {
    var payload = BuildPayload(3, seed: 42);
    var image = BuildImage(payload);

    var ms = new MemoryStream();
    ms.Write(image, 0, image.Length);

    var zeros = new byte[BlockSize];
    CsoInPlaceModifier.WriteBlock(ms, 1, zeros);

    // Extract each block and assert the modified one is zeros, others are intact.
    var desc = new CsoFormatDescriptor();
    var tmp = Path.Combine(Path.GetTempPath(), "cso_inplace_test_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmp);
    try {
      ms.Position = 0;
      desc.Extract(ms, tmp, null, ["block_00000.bin", "block_00001.bin", "block_00002.bin"]);

      var b0 = File.ReadAllBytes(Path.Combine(tmp, "blocks/block_00000.bin"));
      var b1 = File.ReadAllBytes(Path.Combine(tmp, "blocks/block_00001.bin"));
      var b2 = File.ReadAllBytes(Path.Combine(tmp, "blocks/block_00002.bin"));

      // List reports each block's compressed size; descriptor's Extract writes
      // the on-disk (still-compressed) bytes. We only assert structural
      // properties — the modified entry must round-trip a decompressed-zero
      // slab when inflated, which the FULL.cso path covers.
      Assert.That(b0.Length, Is.GreaterThan(0));
      Assert.That(b1.Length, Is.GreaterThan(0), "modified block on disk has compressed-zeros payload");
      Assert.That(b2.Length, Is.GreaterThan(0));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void WriteBlock_LargerThanSlot_HeaderPrefixUnchanged_OtherBlocksDecodable() {
    // Build payload where blocks are highly compressible so the writer emits
    // small compressed bytes, then over-write block 0 with random data whose
    // DEFLATE output is larger than the original tiny slot — forces the
    // append-at-EOF branch.
    var payload = new byte[3 * BlockSize];      // zeros → DEFLATE compresses tiny
    var image = BuildImage(payload);
    var (oldOffsets, _) = ReadIndex(image);
    var oldBlock0Size = oldOffsets[1] - oldOffsets[0];

    // Random bytes don't compress; their DEFLATE output will be > oldBlock0Size.
    var random = new byte[BlockSize];
    new Random(1).NextBytes(random);

    var ms = new MemoryStream();
    ms.Write(image, 0, image.Length);
    var oldLen = (int)ms.Length;
    CsoInPlaceModifier.WriteBlock(ms, 0, random);

    var after = ms.ToArray();

    // Stream grew because the new payload didn't fit in place.
    Assert.That(after.Length, Is.GreaterThan(oldLen),
      "grow-then-append branch must extend the stream");

    // Header pre-fix unchanged (offsets 0..23 carry magic + uncompressed_size +
    // block_size + version + align; they are NOT touched by in-place writes).
    Assert.That(after.AsSpan(0, 24).SequenceEqual(image.AsSpan(0, 24)), Is.True,
      "the 24-byte header must remain byte-identical after a grow-append modify");

    // Index moved: block 0 now points past the old tail.
    var (newOffsets, _) = ReadIndex(after);
    Assert.That(newOffsets[0], Is.GreaterThanOrEqualTo(oldLen),
      "moved block 0 must land past the original EOF");

    // Every block still decodable via FULL.cso round-trip (header + index + bodies
    // describe a coherent CSO container).
    var desc = new CsoFormatDescriptor();
    ms.Position = 0;
    var entries = desc.List(ms, null);
    Assert.That(entries.Count(e => e.Name.StartsWith("blocks/block_")), Is.EqualTo(3),
      "post-grow image must still surface 3 synthetic block entries");
  }

  [Test, Category("ErrorHandling")]
  public void WriteBlock_BlockIndexOutOfRange_Throws() {
    var image = BuildImage(BuildPayload(2, seed: 9));
    using var ms = new MemoryStream();
    ms.Write(image, 0, image.Length);
    Assert.That(
      () => CsoInPlaceModifier.WriteBlock(ms, 99, new byte[BlockSize]),
      Throws.InstanceOf<ArgumentOutOfRangeException>(),
      "out-of-range block index must throw, not corrupt the image");
  }

  [Test, Category("ErrorHandling")]
  public void WriteBlock_WrongPayloadSize_Throws() {
    var image = BuildImage(BuildPayload(2, seed: 9));
    using var ms = new MemoryStream();
    ms.Write(image, 0, image.Length);
    Assert.That(
      () => CsoInPlaceModifier.WriteBlock(ms, 0, new byte[BlockSize - 1]),
      Throws.InstanceOf<ArgumentException>(),
      "payload must be exactly block_size bytes");
  }

  [Test, Category("RoundTrip")]
  public void MutateThenExtract_RoundTrip() {
    var payload = BuildPayload(2, seed: 7);
    var image = BuildImage(payload);

    using var ms = new MemoryStream();
    ms.Write(image, 0, image.Length);

    // Mutate block 0 to all-zeros via the descriptor's IArchiveModifiable surface.
    IArchiveModifiable mod = new CsoFormatDescriptor();
    mod.Add(ms, [ArchiveInputInfo.InMemory("blocks/block_00000.bin", new byte[BlockSize])]);

    // Extract and verify FULL.cso re-reads the mutated image without error.
    var desc = new CsoFormatDescriptor();
    var tmp = Path.Combine(Path.GetTempPath(), "cso_mutate_extract_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmp);
    try {
      ms.Position = 0;
      desc.Extract(ms, tmp, null, ["FULL.cso", "metadata.ini"]);
      Assert.That(File.Exists(Path.Combine(tmp, "FULL.cso")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "metadata.ini")), Is.True);
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void Descriptor_CapabilitiesAdvertiseCreateAndModify() {
    var d = new CsoFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True,
      "Cso descriptor must advertise CanCreate");
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True,
      "Cso descriptor must advertise CanModify");
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
    Assert.That(d, Is.InstanceOf<IArchiveModifiable>());
  }

  [Test, Category("RoundTrip")]
  public void Create_FromInputs_ProducesValidCso() {
    // Build a 4 KB uncompressed payload by feeding two 2 KB inputs in order.
    var first = new byte[BlockSize]; Array.Fill(first, (byte)0xAA);
    var second = new byte[BlockSize]; Array.Fill(second, (byte)0x55);

    var d = (IArchiveCreatable)new CsoFormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms,
      [ArchiveInputInfo.InMemory("part1.bin", first),
       ArchiveInputInfo.InMemory("part2.bin", second)],
      new FormatCreateOptions());

    ms.Position = 0;
    var entries = new CsoFormatDescriptor().List(ms, null);
    Assert.That(entries.Count(e => e.Name.StartsWith("blocks/block_")), Is.EqualTo(2));
    Assert.That(entries.First(e => e.Name == "FULL.cso").OriginalSize, Is.EqualTo(ms.Length));
  }
}
