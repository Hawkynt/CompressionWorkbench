#pragma warning disable CS1591
using static FileSystem.BcacheFs.BcacheFsFormat;

namespace FileSystem.BcacheFs;

/// <summary>
/// Native non-encrypted checksum algorithms used by bcachefs metadata, journal
/// sets and data extents. Encrypted ChaCha20/Poly1305 checksums are intentionally
/// reported as requiring a key instead of being silently treated as invalid.
/// </summary>
internal static class BcacheFsChecksumCodec {
  private const ulong Crc64EcmaPolynomial = 0x42F0E1EBA9EA3693UL;
  private const ulong XxPrime1 = 11400714785074694791UL;
  private const ulong XxPrime2 = 14029467366897019727UL;
  private const ulong XxPrime3 = 1609587929392839161UL;
  private const ulong XxPrime4 = 9650029242287828579UL;
  private const ulong XxPrime5 = 2870177450012600261UL;

  internal static bool TryCompute(
      BcacheFsChecksumType type,
      ReadOnlySpan<byte> data,
      out BcacheFsChecksum checksum) {
    switch (type) {
      case BcacheFsChecksumType.None:
        checksum = default;
        return true;
      case BcacheFsChecksumType.Crc32CNonzero:
        checksum = new BcacheFsChecksum(Crc32C(0xFFFFFFFFU, data) ^ 0xFFFFFFFFU, 0);
        return true;
      case BcacheFsChecksumType.Crc32C:
        checksum = new BcacheFsChecksum(Crc32C(0, data), 0);
        return true;
      case BcacheFsChecksumType.Crc64Nonzero:
        checksum = new BcacheFsChecksum(Crc64(ulong.MaxValue, data) ^ ulong.MaxValue, 0);
        return true;
      case BcacheFsChecksumType.Crc64:
        checksum = new BcacheFsChecksum(Crc64(0, data), 0);
        return true;
      case BcacheFsChecksumType.XxHash:
        checksum = new BcacheFsChecksum(XxHash64(data), 0);
        return true;
      case BcacheFsChecksumType.ChaCha20Poly1305_80:
      case BcacheFsChecksumType.ChaCha20Poly1305_128:
      default:
        checksum = default;
        return false;
    }
  }

  /// <summary>
  /// Checks a vstruct checksum. bcachefs always places the 16-byte bch_csum
  /// first and hashes the bytes after it through vstruct_end().
  /// </summary>
  internal static BcacheFsChecksumVerification VerifyVstruct(
      BcacheFsChecksumType type,
      ReadOnlySpan<byte> vstruct) {
    if (vstruct.Length < 16)
      return new BcacheFsChecksumVerification(false, false, "vstruct checksum field is truncated.");

    if (!TryCompute(type, vstruct[16..], out var computed))
      return new BcacheFsChecksumVerification(false, true,
        $"checksum type {type} requires encryption-key support.");

    var stored = BcacheFsChecksum.Read(vstruct);
    return stored == computed
      ? new BcacheFsChecksumVerification(true, false, string.Empty)
      : new BcacheFsChecksumVerification(false, false,
        $"checksum mismatch: stored {stored}, computed {computed}.");
  }

  private static ulong Crc64(ulong crc, ReadOnlySpan<byte> data) {
    foreach (var value in data) {
      crc ^= (ulong)value << 56;
      for (var bit = 0; bit < 8; ++bit)
        crc = (crc & 0x8000000000000000UL) != 0
          ? (crc << 1) ^ Crc64EcmaPolynomial
          : crc << 1;
    }
    return crc;
  }

  private static ulong XxHash64(ReadOnlySpan<byte> data) {
    static ulong Round(ulong accumulator, ulong input) {
      accumulator += input * XxPrime2;
      accumulator = System.Numerics.BitOperations.RotateLeft(accumulator, 31);
      return accumulator * XxPrime1;
    }

    static ulong MergeRound(ulong accumulator, ulong value) {
      accumulator ^= Round(0, value);
      return accumulator * XxPrime1 + XxPrime4;
    }

    var offset = 0;
    ulong hash;
    if (data.Length >= 32) {
      var v1 = unchecked(XxPrime1 + XxPrime2);
      var v2 = XxPrime2;
      var v3 = 0UL;
      var v4 = unchecked(0UL - XxPrime1);
      var limit = data.Length - 32;
      do {
        v1 = Round(v1, System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(data[offset..])); offset += 8;
        v2 = Round(v2, System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(data[offset..])); offset += 8;
        v3 = Round(v3, System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(data[offset..])); offset += 8;
        v4 = Round(v4, System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(data[offset..])); offset += 8;
      } while (offset <= limit);

      hash = System.Numerics.BitOperations.RotateLeft(v1, 1)
        + System.Numerics.BitOperations.RotateLeft(v2, 7)
        + System.Numerics.BitOperations.RotateLeft(v3, 12)
        + System.Numerics.BitOperations.RotateLeft(v4, 18);
      hash = MergeRound(hash, v1);
      hash = MergeRound(hash, v2);
      hash = MergeRound(hash, v3);
      hash = MergeRound(hash, v4);
    } else {
      hash = XxPrime5;
    }

    hash += (ulong)data.Length;
    while (offset + 8 <= data.Length) {
      var lane = Round(0, System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]));
      hash ^= lane;
      hash = System.Numerics.BitOperations.RotateLeft(hash, 27) * XxPrime1 + XxPrime4;
      offset += 8;
    }
    if (offset + 4 <= data.Length) {
      hash ^= (ulong)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]) * XxPrime1;
      hash = System.Numerics.BitOperations.RotateLeft(hash, 23) * XxPrime2 + XxPrime3;
      offset += 4;
    }
    while (offset < data.Length) {
      hash ^= data[offset++] * XxPrime5;
      hash = System.Numerics.BitOperations.RotateLeft(hash, 11) * XxPrime1;
    }

    hash ^= hash >> 33;
    hash *= XxPrime2;
    hash ^= hash >> 29;
    hash *= XxPrime3;
    hash ^= hash >> 32;
    return hash;
  }
}

internal readonly record struct BcacheFsChecksum(ulong Lo, ulong Hi) {
  internal static BcacheFsChecksum Read(ReadOnlySpan<byte> bytes) => new(
    System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes),
    System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]));

  public override string ToString() => $"{this.Hi:X16}{this.Lo:X16}";
}

internal readonly record struct BcacheFsChecksumVerification(
  bool Valid,
  bool KeyRequired,
  string Diagnostic);