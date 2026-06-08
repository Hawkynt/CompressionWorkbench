using System.Buffers.Binary;
using System.Text;
using FileFormat.Partclone;

namespace Compression.Tests.Partclone;

[TestFixture]
public class PartcloneTests {

  // Synthetic v2 partclone image with `totalBlocks` blocks of `blockSize` bytes
  // each. The `used` bitmask marks which blocks should be present in the data
  // stream — used[i] true means block i appears in the output with a fixed
  // recognisable byte pattern; unused blocks should be reconstructed as zeros.
  private static byte[] BuildPartclone(
    string ptcVersion,
    string fs,
    ulong totalBlocks,
    uint blockSize,
    bool[] used,
    byte bitmapMode = PartcloneReader.BmBit,
    ushort checksumMode = 0,
    ushort checksumSize = 0,
    uint blocksPerChecksum = 0
  ) {
    if ((ulong)used.Length != totalBlocks)
      throw new ArgumentException("used[] length must equal totalBlocks");

    using var ms = new MemoryStream();

    // image_head_v2: magic[15] + ptc_version[14] + endianess[2] = 31 bytes
    Span<byte> head = stackalloc byte[31];
    PartcloneReader.Magic.CopyTo(head);
    var verBytes = Encoding.ASCII.GetBytes(ptcVersion);
    verBytes.AsSpan(0, Math.Min(verBytes.Length, 14)).CopyTo(head[15..29]);
    BinaryPrimitives.WriteUInt16LittleEndian(head[29..], PartcloneReader.EndianMagic);
    ms.Write(head);

    // file_system_info_v2: fs[15] + 4×u64 + u32 = 51 bytes
    Span<byte> fsInfo = stackalloc byte[51];
    var fsBytes = Encoding.ASCII.GetBytes(fs);
    fsBytes.AsSpan(0, Math.Min(fsBytes.Length, 15)).CopyTo(fsInfo[..15]);
    BinaryPrimitives.WriteUInt64LittleEndian(fsInfo[15..], totalBlocks * blockSize); // device_size
    BinaryPrimitives.WriteUInt64LittleEndian(fsInfo[23..], totalBlocks);              // totalblock
    var usedCount = (ulong)used.Count(u => u);
    BinaryPrimitives.WriteUInt64LittleEndian(fsInfo[31..], usedCount);                // usedblocks
    BinaryPrimitives.WriteUInt64LittleEndian(fsInfo[39..], usedCount);                // superBlockUsedBlocks
    BinaryPrimitives.WriteUInt32LittleEndian(fsInfo[47..], blockSize);                // block_size
    ms.Write(fsInfo);

    // image_options_v2: 22 bytes
    Span<byte> opts = stackalloc byte[22];
    BinaryPrimitives.WriteUInt32LittleEndian(opts[..4],   22);                  // feature_size
    BinaryPrimitives.WriteUInt16LittleEndian(opts[4..],   2);                   // image_version
    BinaryPrimitives.WriteUInt16LittleEndian(opts[6..],   64);                  // cpu_bits
    BinaryPrimitives.WriteUInt16LittleEndian(opts[8..],   checksumMode);
    BinaryPrimitives.WriteUInt16LittleEndian(opts[10..],  checksumSize);
    BinaryPrimitives.WriteUInt32LittleEndian(opts[12..],  blocksPerChecksum);
    opts[16] = 0;                                                                // reseed_checksum
    opts[17] = bitmapMode;
    BinaryPrimitives.WriteUInt32LittleEndian(opts[18..],  0xDEADBEEF);          // crc
    ms.Write(opts);

    // bitmap
    if (bitmapMode == PartcloneReader.BmBit) {
      var bm = new byte[(used.Length + 7) / 8];
      for (var i = 0; i < used.Length; i++)
        if (used[i]) bm[i / 8] |= (byte)(1 << (i % 8));
      ms.Write(bm);
    } else if (bitmapMode == PartcloneReader.BmByte) {
      var bm = new byte[used.Length];
      for (var i = 0; i < used.Length; i++) bm[i] = used[i] ? (byte)1 : (byte)0;
      ms.Write(bm);
    }

    // optional trailing CRC after bitmap when checksum_mode != 0
    if (checksumMode != 0 && checksumSize > 0) {
      var trailer = new byte[checksumSize];
      ms.Write(trailer);
    }

    // data: one block_size payload per used block; pattern = block index * 7
    // so we can verify position-correct reconstruction.
    for (var i = 0; i < used.Length; i++) {
      if (!used[i]) continue;
      var block = new byte[blockSize];
      Array.Fill(block, (byte)((i * 7) & 0xFF));
      ms.Write(block);
    }

    return ms.ToArray();
  }

  private static byte ExpectedPattern(int blockIndex) => (byte)((blockIndex * 7) & 0xFF);

  [Test, Category("HappyPath")]
  public void Reader_ParsesHeader_AndExposesInfo() {
    var data = BuildPartclone("2.91", "fat32", totalBlocks: 8, blockSize: 16,
      used: [true, false, true, true, false, false, true, false]);
    using var ms = new MemoryStream(data);
    var r = new PartcloneReader(ms);

    Assert.That(r.Info.PtcVersion, Is.EqualTo("2.91"));
    Assert.That(r.Info.FsType, Is.EqualTo("fat32"));
    Assert.That(r.Info.TotalBlocks, Is.EqualTo((ulong)8));
    Assert.That(r.Info.UsedBlocks, Is.EqualTo((ulong)4));
    Assert.That(r.Info.BlockSize, Is.EqualTo((uint)16));
    Assert.That(r.Info.BitmapMode, Is.EqualTo(PartcloneReader.BmBit));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Reader_ReconstructDisk_PlacesUsedBlocksAtCorrectOffsets_AndZerosUnused() {
    var used = new[] { true, false, true, true, false, false, true, false };
    const uint blockSize = 16;
    var data = BuildPartclone("2.91", "fat32", totalBlocks: 8, blockSize: blockSize, used: used);
    using var ms = new MemoryStream(data);
    var disk = new PartcloneReader(ms).ReconstructDisk();

    Assert.That(disk, Has.Length.EqualTo(used.Length * (int)blockSize));
    for (var i = 0; i < used.Length; i++) {
      var slice = disk.AsSpan(i * (int)blockSize, (int)blockSize);
      var expected = used[i] ? ExpectedPattern(i) : (byte)0;
      for (var j = 0; j < blockSize; j++)
        Assert.That(slice[j], Is.EqualTo(expected),
          $"block {i} byte {j} should be 0x{expected:X2} (used={used[i]})");
    }
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Reader_StreamDiskTo_MatchesReconstructDisk() {
    var used = new[] { false, true, true, false, true };
    var data = BuildPartclone("2.91", "ext4", totalBlocks: 5, blockSize: 8, used: used);
    using var ms = new MemoryStream(data);
    var expected = new PartcloneReader(ms).ReconstructDisk();

    using var ms2 = new MemoryStream(data);
    using var sink = new MemoryStream();
    new PartcloneReader(ms2).StreamDiskTo(sink);

    Assert.That(sink.ToArray(), Is.EqualTo(expected));
  }

  [Test, Category("HappyPath")]
  public void Reader_AcceptsByteBitmap() {
    var used = new[] { true, true, false, true };
    var data = BuildPartclone("2.91", "ntfs", totalBlocks: 4, blockSize: 8, used: used,
      bitmapMode: PartcloneReader.BmByte);
    using var ms = new MemoryStream(data);
    var r = new PartcloneReader(ms);
    Assert.That(r.Info.BitmapMode, Is.EqualTo(PartcloneReader.BmByte));

    var disk = r.ReconstructDisk();
    for (var i = 0; i < used.Length; i++) {
      var expected = used[i] ? ExpectedPattern(i) : (byte)0;
      Assert.That(disk[i * 8], Is.EqualTo(expected), $"block {i}");
    }
  }

  [Test, Category("EdgeCase")]
  public void Reader_BadMagic_Throws() {
    var data = new byte[128];
    Encoding.ASCII.GetBytes("not-partclone!!").CopyTo(data.AsMemory());
    using var ms = new MemoryStream(data);
    Assert.That(() => new PartcloneReader(ms), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("EdgeCase")]
  public void Reader_BadEndianMarker_Throws() {
    var data = BuildPartclone("2.91", "fat32", totalBlocks: 2, blockSize: 8, used: [true, false]);
    // Corrupt endianess at offset 29 (right after magic[15]+version[14]).
    data[29] = 0xAA;
    data[30] = 0xBB;
    using var ms = new MemoryStream(data);
    Assert.That(() => new PartcloneReader(ms), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("EdgeCase")]
  public void Reader_TruncatedHeader_Throws() {
    var data = new byte[15];
    PartcloneReader.Magic.CopyTo(data.AsSpan());
    using var ms = new MemoryStream(data);
    Assert.That(() => new PartcloneReader(ms), Throws.InstanceOf<EndOfStreamException>());
  }

  [Test, Category("EdgeCase")]
  public void Reader_TruncatedDataStream_Throws() {
    var data = BuildPartclone("2.91", "fat32", totalBlocks: 4, blockSize: 16,
      used: [true, true, true, true]);
    // Lop off half the data section to simulate truncation.
    var truncated = data.AsSpan(0, data.Length - 32).ToArray();
    using var ms = new MemoryStream(truncated);
    Assert.That(() => new PartcloneReader(ms).ReconstructDisk(),
      Throws.InstanceOf<EndOfStreamException>());
  }

  [Test, Category("BoundaryCase")]
  public void Reader_AllUsed_NoZeroPadding() {
    var used = new[] { true, true, true };
    var data = BuildPartclone("2.91", "ext4", totalBlocks: 3, blockSize: 8, used: used);
    using var ms = new MemoryStream(data);
    var disk = new PartcloneReader(ms).ReconstructDisk();
    Assert.That(disk, Has.Length.EqualTo(24));
    for (var i = 0; i < 3; i++)
      Assert.That(disk[i * 8], Is.EqualTo(ExpectedPattern(i)));
  }

  [Test, Category("BoundaryCase")]
  public void Reader_AllUnused_DiskIsAllZero() {
    var used = new[] { false, false, false };
    var data = BuildPartclone("2.91", "ext4", totalBlocks: 3, blockSize: 8, used: used);
    using var ms = new MemoryStream(data);
    var disk = new PartcloneReader(ms).ReconstructDisk();
    Assert.That(disk, Has.Length.EqualTo(24));
    Assert.That(disk.All(b => b == 0), Is.True);
  }

  [Test, Category("BoundaryCase")]
  public void Reader_BitmapNotByteAligned_Roundtrips() {
    // 11 blocks -> bitmap is 2 bytes with 5 trailing unused bits.
    var used = new[] { true, false, true, true, false, true, false, false, true, false, true };
    var data = BuildPartclone("2.91", "fat32", totalBlocks: 11, blockSize: 8, used: used);
    using var ms = new MemoryStream(data);
    var disk = new PartcloneReader(ms).ReconstructDisk();
    for (var i = 0; i < used.Length; i++) {
      var expected = used[i] ? ExpectedPattern(i) : (byte)0;
      Assert.That(disk[i * 8], Is.EqualTo(expected), $"block {i}");
    }
  }

  [Test, Category("HappyPath")]
  public void LooksLikePartclone_DetectsMagicAtOffsetZero() {
    var data = new byte[16];
    PartcloneReader.Magic.CopyTo(data.AsSpan());
    Assert.That(PartcloneReader.LooksLikePartclone(data), Is.True);
  }

  [Test, Category("HappyPath")]
  public void LooksLikePartclone_RejectsRandomBuffer() {
    var data = new byte[16];
    Array.Fill(data, (byte)0xCC);
    Assert.That(PartcloneReader.LooksLikePartclone(data), Is.False);
  }

  // ── Descriptor tests ────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_List_ReturnsMetadataAndImageEntries() {
    var data = BuildPartclone("2.91", "fat32", totalBlocks: 4, blockSize: 16,
      used: [true, false, true, false]);
    using var ms = new MemoryStream(data);
    var entries = new PartcloneFormatDescriptor().List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(2));
    Assert.That(entries[0].Name, Is.EqualTo("metadata.ini"));
    Assert.That(entries[1].Name, Is.EqualTo("image.img"));
    Assert.That(entries[1].OriginalSize, Is.EqualTo(64));   // 4 blocks × 16 bytes
    Assert.That(entries[1].CompressedSize, Is.EqualTo(32)); // 2 used × 16 bytes
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Extract_WritesMetadataAndReconstructedImage() {
    var used = new[] { true, false, true, true };
    var data = BuildPartclone("2.91", "fat32", totalBlocks: 4, blockSize: 16, used: used);
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(data);
      new PartcloneFormatDescriptor().Extract(ms, tmp, null, null);

      var metaPath = Path.Combine(tmp, "metadata.ini");
      var imgPath = Path.Combine(tmp, "image.img");
      Assert.That(File.Exists(metaPath), Is.True);
      Assert.That(File.Exists(imgPath), Is.True);

      var meta = File.ReadAllText(metaPath);
      Assert.That(meta, Does.Contain("[partclone]"));
      Assert.That(meta, Does.Contain("fs = fat32"));
      Assert.That(meta, Does.Contain("block_size = 16"));
      Assert.That(meta, Does.Contain("total_blocks = 4"));
      Assert.That(meta, Does.Contain("used_blocks = 3"));

      var img = File.ReadAllBytes(imgPath);
      Assert.That(img, Has.Length.EqualTo(64));
      for (var i = 0; i < used.Length; i++) {
        var expected = used[i] ? ExpectedPattern(i) : (byte)0;
        Assert.That(img[i * 16], Is.EqualTo(expected), $"image byte {i * 16} (block {i})");
      }
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Extract_FilterOnlyMetadata_DoesNotWriteImage() {
    var data = BuildPartclone("2.91", "fat32", totalBlocks: 2, blockSize: 8,
      used: [true, false]);
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(data);
      new PartcloneFormatDescriptor().Extract(ms, tmp, null, ["metadata.ini"]);
      Assert.That(File.Exists(Path.Combine(tmp, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "image.img")), Is.False);
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesPartcloneMagic() {
    var d = new PartcloneFormatDescriptor();
    Assert.That(d.MagicSignatures, Has.Count.GreaterThan(0));
    var sig = d.MagicSignatures[0];
    Assert.That(sig.Offset, Is.EqualTo(0));
    Assert.That(sig.Bytes.SequenceEqual(PartcloneReader.Magic), Is.True);
  }
}
