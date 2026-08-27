#pragma warning disable CS1591
using System.Buffers.Binary;
using System.IO.Compression;
using Compression.Core.Streams;
using FileFormat.Zstd;
using static FileSystem.BcacheFs.BcacheFsFormat;

namespace FileSystem.BcacheFs;

/// <summary>
/// Reads one logical extent fragment without losing the original encoded extent
/// bounds. Each physical replica is tried independently with its own CRC/
/// compression descriptor; checksum, decompression and slicing happen in the
/// same order as the kernel read path.
/// </summary>
internal static class BcacheFsExtentDataReader {
  internal static bool TryReadFragment(
      BcacheFsCoreVolume volume,
      BcacheFsExtentFragment fragment,
      out byte[] data,
      out BcacheFsExtentReadTrace? trace,
      out string error) {
    ArgumentNullException.ThrowIfNull(volume);
    ArgumentNullException.ThrowIfNull(fragment);
    trace = null;
    error = string.Empty;

    var wantedBytes64 = fragment.SectorCount * SectorSize;
    if (wantedBytes64 > int.MaxValue) {
      data = [];
      error = $"extent fragment is {wantedBytes64} bytes; one read buffer cannot exceed Int32.MaxValue.";
      return false;
    }
    var wantedBytes = (int)wantedBytes64;

    switch (fragment.Kind) {
      case BcacheFsExtentFragmentKind.Reservation:
      case BcacheFsExtentFragmentKind.Whiteout:
        data = new byte[wantedBytes];
        trace = new BcacheFsExtentReadTrace(null, null, false, false, ["logical zero range"]);
        return true;

      case BcacheFsExtentFragmentKind.Error:
        data = [];
        error = "bcachefs extent is explicitly marked as unrecoverable data.";
        return false;

      case BcacheFsExtentFragmentKind.InlineData:
        return TryReadInline(fragment, wantedBytes, out data, out trace, out error);

      case BcacheFsExtentFragmentKind.Reflink:
        data = [];
        error = "reflink_p requires resolving the reflink btree before physical data can be read.";
        return false;

      case BcacheFsExtentFragmentKind.Data:
        break;

      default:
        data = [];
        error = $"unsupported extent fragment kind {fragment.Kind}.";
        return false;
    }

    if (!TryBuildCandidates(fragment.SourceKey, volume.Superblock, out var candidates, out error)) {
      data = [];
      return false;
    }

    var failures = new List<string>();
    foreach (var candidate in candidates) {
      if (candidate.Pointer.Unused) {
        failures.Add($"device {candidate.Pointer.Device} sector {candidate.Pointer.Sector}: pointer is unused.");
        continue;
      }
      if (candidate.Pointer.Unwritten) {
        // An unwritten pointer represents reserved but not initialized storage;
        // exposing stale physical bytes would be a data leak, so its logical
        // contents are zeros.
        data = new byte[wantedBytes];
        trace = new BcacheFsExtentReadTrace(
          candidate.Pointer, candidate.Crc, false, false,
          ["unwritten physical pointer returned as zeros"]);
        return true;
      }
      if (!volume.Devices.TryGetValue(candidate.Pointer.Device, out var device)) {
        failures.Add($"device {candidate.Pointer.Device}: member device was not supplied.");
        continue;
      }

      if (TryReadCandidate(device, fragment, candidate, out data, out var candidateTrace, out var candidateError)) {
        trace = candidateTrace;
        return true;
      }
      failures.Add($"device {candidate.Pointer.Device} sector {candidate.Pointer.Sector}: {candidateError}");
    }

    data = [];
    error = failures.Count == 0
      ? "extent has no readable physical replicas."
      : $"all extent replicas failed: {string.Join("; ", failures)}";
    return false;
  }

  internal static bool TryBuildCandidates(
      BcacheFsRawKey key,
      BcacheFsSuperblockRecord superblock,
      out IReadOnlyList<BcacheFsExtentReplicaCandidate> candidates,
      out string error) {
    ArgumentNullException.ThrowIfNull(key);
    ArgumentNullException.ThrowIfNull(superblock);
    if (key.Type != BcacheFsKeyType.Extent) {
      candidates = [];
      error = $"key type {key.RawType} is not a physical extent.";
      return false;
    }
    if (!BcacheFsExtentCodec.TryParseEntries(key.Value, superblock, out var entries, out error)) {
      candidates = [];
      return false;
    }

    BcacheFsExtentCrc? current = null;
    var result = new List<BcacheFsExtentReplicaCandidate>();
    foreach (var entry in entries) {
      switch (entry.KnownType) {
        case BcacheFsExtentEntryType.Crc32:
        case BcacheFsExtentEntryType.Crc64:
        case BcacheFsExtentEntryType.Crc128:
          if (!BcacheFsExtentCodec.TryReadExtentCrc(entry, out current, out error)) {
            candidates = [];
            return false;
          }
          break;

        case BcacheFsExtentEntryType.Pointer: {
          var word = BinaryPrimitives.ReadUInt64LittleEndian(entry.RawBytes);
          var pointer = new BcacheFsExtentPointer(
            Device: (byte)((word >> 48) & 0xFF),
            Sector: (long)((word >> 4) & ((1UL << 44) - 1)),
            Generation: (byte)(word >> 56),
            Cached: (word & (1UL << 1)) != 0,
            Unused: (word & (1UL << 2)) != 0,
            Unwritten: (word & (1UL << 3)) != 0,
            RawWord: word);
          result.Add(new BcacheFsExtentReplicaCandidate(
            pointer,
            current ?? Unencoded(key.Size)));
          break;
        }

        case BcacheFsExtentEntryType.StripePointer:
          // A stripe pointer supplies an EC reconstruction path rather than an
          // ordinary device pointer. Preserve the fact that it exists; physical
          // EC reconstruction is implemented in the stripe layer.
          break;

        default:
          // flags/reconcile entries modify policy, not how the immediately
          // following physical bytes are decoded.
          break;
      }
    }

    if (result.Count == 0) {
      candidates = [];
      error = entries.Any(e => e.KnownType == BcacheFsExtentEntryType.StripePointer)
        ? "extent has only erasure-coded stripe pointers; stripe reconstruction is required."
        : "extent has no physical pointer entries.";
      return false;
    }

    candidates = result;
    error = string.Empty;
    return true;
  }

  private static BcacheFsExtentCrc Unencoded(uint liveSize)
    => new(
      CompressedSize: liveSize,
      UncompressedSize: liveSize,
      Offset: 0,
      Nonce: 0,
      ChecksumType: BcacheFsChecksumType.None,
      CompressionType: BcacheFsCompressionType.None,
      Checksum: default);

  private static bool TryReadCandidate(
      Stream device,
      BcacheFsExtentFragment fragment,
      BcacheFsExtentReplicaCandidate candidate,
      out byte[] data,
      out BcacheFsExtentReadTrace trace,
      out string error) {
    var crc = candidate.Crc;
    var encoded = crc.Encoded;
    ulong physicalStartSector;
    ulong sectorsToRead;
    ulong logicalSliceSector;

    if (encoded) {
      physicalStartSector = checked((ulong)candidate.Pointer.Sector);
      sectorsToRead = crc.CompressedSize;
      logicalSliceSector = checked((ulong)crc.Offset + fragment.SourceOffsetSectors);
    } else {
      physicalStartSector = checked((ulong)candidate.Pointer.Sector + fragment.SourceOffsetSectors);
      sectorsToRead = fragment.SectorCount;
      logicalSliceSector = 0;
    }

    var encodedBytes64 = sectorsToRead * SectorSize;
    if (encodedBytes64 > int.MaxValue) {
      data = [];
      trace = new BcacheFsExtentReadTrace(candidate.Pointer, crc, false, false, []);
      error = $"physical extent read is {encodedBytes64} bytes; one read buffer cannot exceed Int32.MaxValue.";
      return false;
    }

    var byteOffset = checked((long)physicalStartSector * SectorSize);
    var byteCount = (int)encodedBytes64;
    if (byteOffset < 0 || byteOffset + byteCount > device.Length) {
      data = [];
      trace = new BcacheFsExtentReadTrace(candidate.Pointer, crc, false, false, []);
      error = $"physical extent {physicalStartSector}+{sectorsToRead} sectors lies outside device.";
      return false;
    }

    var raw = new byte[byteCount];
    device.Position = byteOffset;
    device.ReadExactly(raw);

    var checksumVerified = crc.ChecksumType == BcacheFsChecksumType.None;
    if (crc.ChecksumType != BcacheFsChecksumType.None) {
      if (!BcacheFsChecksumCodec.TryCompute(crc.ChecksumType, raw, out var computed)) {
        data = [];
        trace = new BcacheFsExtentReadTrace(candidate.Pointer, crc, false, false, []);
        error = $"checksum type {crc.ChecksumType} requires the filesystem encryption key.";
        return false;
      }
      if (computed != crc.Checksum) {
        data = [];
        trace = new BcacheFsExtentReadTrace(candidate.Pointer, crc, false, false, []);
        error = $"data checksum mismatch: stored {crc.Checksum}, computed {computed}.";
        return false;
      }
      checksumVerified = true;
    }

    byte[] uncompressed;
    var decompressed = false;
    if (crc.Compressed) {
      if (!TryDecompress(crc.CompressionType, raw, crc.UncompressedSize, out uncompressed, out error)) {
        data = [];
        trace = new BcacheFsExtentReadTrace(candidate.Pointer, crc, checksumVerified, false, []);
        return false;
      }
      decompressed = true;
    } else {
      uncompressed = raw;
    }

    var sliceBytes64 = logicalSliceSector * SectorSize;
    var wantedBytes64 = fragment.SectorCount * SectorSize;
    if (sliceBytes64 + wantedBytes64 > (ulong)uncompressed.Length) {
      data = [];
      trace = new BcacheFsExtentReadTrace(candidate.Pointer, crc, checksumVerified, decompressed, []);
      error = $"live range [{logicalSliceSector},{logicalSliceSector + fragment.SectorCount}) sectors exceeds decoded extent of {uncompressed.Length / SectorSize} sectors.";
      return false;
    }

    data = uncompressed.AsSpan((int)sliceBytes64, (int)wantedBytes64).ToArray();
    trace = new BcacheFsExtentReadTrace(
      candidate.Pointer,
      crc,
      checksumVerified,
      decompressed,
      []);
    error = string.Empty;
    return true;
  }

  private static bool TryReadInline(
      BcacheFsExtentFragment fragment,
      int wantedBytes,
      out byte[] data,
      out BcacheFsExtentReadTrace trace,
      out string error) {
    var sourceOffsetBytes64 = fragment.SourceOffsetSectors * SectorSize;
    if (sourceOffsetBytes64 > int.MaxValue) {
      data = [];
      trace = new BcacheFsExtentReadTrace(null, null, false, false, []);
      error = "inline-data source offset exceeds addressable buffer size.";
      return false;
    }

    data = new byte[wantedBytes];
    var sourceOffset = (int)sourceOffsetBytes64;
    if (sourceOffset < fragment.SourceKey.Value.Length) {
      var available = Math.Min(wantedBytes, fragment.SourceKey.Value.Length - sourceOffset);
      fragment.SourceKey.Value.AsSpan(sourceOffset, available).CopyTo(data);
    }
    trace = new BcacheFsExtentReadTrace(null, null, false, false, ["inline btree data"]);
    error = string.Empty;
    return true;
  }

  private static bool TryDecompress(
      BcacheFsCompressionType type,
      byte[] source,
      uint uncompressedSectors,
      out byte[] data,
      out string error) {
    var targetBytes64 = (ulong)uncompressedSectors * SectorSize;
    if (targetBytes64 > int.MaxValue) {
      data = [];
      error = $"decoded extent is {targetBytes64} bytes; one buffer cannot exceed Int32.MaxValue.";
      return false;
    }
    var targetBytes = (int)targetBytes64;

    try {
      switch (type) {
        case BcacheFsCompressionType.Lz4Old:
        case BcacheFsCompressionType.Lz4:
          data = DecompressLz4Partial(source, targetBytes);
          break;

        case BcacheFsCompressionType.Gzip:
          using (var input = new MemoryStream(source, writable: false))
          using (var deflate = new DeflateStream(input, CompressionMode.Decompress, leaveOpen: false)) {
            data = ReadExactDecoded(deflate, targetBytes);
          }
          break;

        case BcacheFsCompressionType.Zstd: {
          if (source.Length < 4) {
            data = [];
            error = "bcachefs zstd extent is shorter than its 32-bit compressed-length prefix.";
            return false;
          }
          var realSourceLength = BinaryPrimitives.ReadUInt32LittleEndian(source);
          if (realSourceLength > source.Length - 4) {
            data = [];
            error = $"bcachefs zstd prefix claims {realSourceLength} bytes but only {source.Length - 4} follow.";
            return false;
          }
          using var input = new MemoryStream(source, 4, (int)realSourceLength, writable: false);
          using var zstd = new ZstdStream(input, CompressionStreamMode.Decompress, leaveOpen: false);
          data = ReadExactDecoded(zstd, targetBytes);
          break;
        }

        case BcacheFsCompressionType.None:
        case BcacheFsCompressionType.Incompressible:
          if (source.Length != targetBytes)
            throw new InvalidDataException($"uncompressed extent size mismatch: expected {targetBytes}, got {source.Length}.");
          data = source;
          break;

        default:
          data = [];
          error = $"unknown bcachefs compression type {(byte)type}.";
          return false;
      }
    } catch (Exception exception) when (exception is InvalidDataException or IOException or ArgumentException) {
      data = [];
      error = $"{type} decompression failed: {exception.Message}";
      return false;
    }

    error = string.Empty;
    return true;
  }

  private static byte[] ReadExactDecoded(Stream decoder, int expectedBytes) {
    var output = new byte[expectedBytes];
    var offset = 0;
    while (offset < output.Length) {
      var read = decoder.Read(output, offset, output.Length - offset);
      if (read == 0)
        throw new InvalidDataException($"decoded stream ended at {offset} bytes; expected {expectedBytes}.");
      offset += read;
    }
    // A decoder producing more than the declared uncompressed extent size is
    // corrupt too; ask for one extra byte to distinguish exact from prefix-only.
    if (decoder.ReadByte() >= 0)
      throw new InvalidDataException($"decoded stream exceeds declared size {expectedBytes}.");
    return output;
  }

  /// <summary>
  /// Linux uses LZ4_decompress_safe_partial() because the physical extent is
  /// sector padded. Stop as soon as the declared destination size is produced;
  /// trailing bytes are allocation padding, not another LZ4 sequence.
  /// </summary>
  private static byte[] DecompressLz4Partial(ReadOnlySpan<byte> source, int targetBytes) {
    var output = new byte[targetBytes];
    var src = 0;
    var dst = 0;
    while (dst < targetBytes) {
      if (src >= source.Length)
        throw new InvalidDataException("unexpected end of LZ4 extent before output was complete.");
      var token = source[src++];
      var literalLength = token >> 4;
      if (literalLength == 15) {
        byte extra;
        do {
          if (src >= source.Length) throw new InvalidDataException("truncated LZ4 literal length.");
          extra = source[src++];
          literalLength += extra;
        } while (extra == 255);
      }
      if (src + literalLength > source.Length || dst + literalLength > targetBytes)
        throw new InvalidDataException("LZ4 literal exceeds source or declared output.");
      source.Slice(src, literalLength).CopyTo(output.AsSpan(dst));
      src += literalLength;
      dst += literalLength;
      if (dst == targetBytes) break;

      if (src + 2 > source.Length)
        throw new InvalidDataException("truncated LZ4 match offset.");
      var distance = BinaryPrimitives.ReadUInt16LittleEndian(source[src..]);
      src += 2;
      if (distance == 0 || distance > dst)
        throw new InvalidDataException("invalid LZ4 match distance.");

      var matchLength = (token & 0xF) + 4;
      if ((token & 0xF) == 15) {
        byte extra;
        do {
          if (src >= source.Length) throw new InvalidDataException("truncated LZ4 match length.");
          extra = source[src++];
          matchLength += extra;
        } while (extra == 255);
      }
      if (dst + matchLength > targetBytes)
        throw new InvalidDataException("LZ4 match exceeds declared output size.");
      var match = dst - distance;
      for (var i = 0; i < matchLength; ++i)
        output[dst++] = output[match + i];
    }
    return output;
  }
}

internal sealed record BcacheFsExtentReplicaCandidate(
  BcacheFsExtentPointer Pointer,
  BcacheFsExtentCrc Crc);

internal sealed record BcacheFsExtentReadTrace(
  BcacheFsExtentPointer? Pointer,
  BcacheFsExtentCrc? Crc,
  bool ChecksumVerified,
  bool Decompressed,
  IReadOnlyList<string> Notes);
