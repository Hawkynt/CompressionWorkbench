#pragma warning disable CS1591
using System.Buffers.Binary;
using static FileSystem.BcacheFs.BcacheFsFormat;

namespace FileSystem.BcacheFs;

/// <summary>
/// Lossless representation of one bcachefs bkey after its position fields have
/// been unpacked. The original encoded bytes are retained as well: an unchanged
/// key can therefore be copied byte-for-byte even when its semantic value codec
/// is not implemented yet.
/// </summary>
internal sealed record BcacheFsRawKey(
  byte Format,
  bool NeedsWhiteout,
  byte RawType,
  BcacheFsKeyVersion Version,
  Bpos Position,
  uint Size,
  byte[] Value,
  byte[] EncodedBytes,
  bool BigEndian = false) {

  internal BcacheFsKeyType? Type
    => Enum.IsDefined(typeof(BcacheFsKeyType), this.RawType)
      ? (BcacheFsKeyType)this.RawType
      : null;

  internal bool IsPacked => this.Format == 0;

  /// <summary>
  /// Serializes the decoded key in KEY_FORMAT_CURRENT form. This is deliberately
  /// independent of the source packing: bcachefs permits unpacked keys in nodes
  /// and journal entries, and using one canonical form avoids losing bversion or
  /// needs_whiteout when a transaction changes the key.
  /// </summary>
  internal byte[] EncodeCurrent() {
    var valueBytes = (this.Value.Length + 7) & ~7;
    var result = new byte[BkeyBytes + valueBytes];
    result[0] = checked((byte)(result.Length / sizeof(ulong)));
    result[1] = (byte)(KeyFormatCurrent | (this.NeedsWhiteout ? 0x80 : 0));
    result[2] = this.RawType;
    BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(4), this.Version.Lo);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12), this.Version.Hi);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16), this.Size);
    WriteBpos(result.AsSpan(20), this.Position);
    this.Value.CopyTo(result.AsSpan(BkeyBytes));
    return result;
  }
}

internal readonly record struct BcacheFsKeyVersion(ulong Lo, uint Hi) : IComparable<BcacheFsKeyVersion> {
  internal static readonly BcacheFsKeyVersion Zero = new(0, 0);
  internal static readonly BcacheFsKeyVersion Max = new(ulong.MaxValue, uint.MaxValue);

  public int CompareTo(BcacheFsKeyVersion other) {
    var high = this.Hi.CompareTo(other.Hi);
    return high != 0 ? high : this.Lo.CompareTo(other.Lo);
  }
}

/// <summary>The node-local format used to decode KEY_FORMAT_LOCAL_BTREE keys.</summary>
internal sealed record BcacheFsKeyFormat(int KeyU64s, int FieldCount, int[] Bits, ulong[] Offsets) {
  internal const int FieldCountCurrent = 6;

  internal static BcacheFsKeyFormat Read(ReadOnlySpan<byte> bytes) {
    if (bytes.Length < 56)
      throw new InvalidDataException("bcachefs bkey_format is truncated.");

    var bits = new int[FieldCountCurrent];
    var offsets = new ulong[FieldCountCurrent];
    for (var i = 0; i < FieldCountCurrent; ++i) {
      bits[i] = bytes[2 + i];
      offsets[i] = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(8 + 8 * i)..]);
    }

    return new BcacheFsKeyFormat(bytes[0], bytes[1], bits, offsets);
  }
}

/// <summary>Bidirectional codec for the common bkey header and packed fields.</summary>
internal static class BcacheFsRawKeyCodec {
  internal static bool TryDecode(
      ReadOnlySpan<byte> encoded,
      BcacheFsKeyFormat? nodeFormat,
      out BcacheFsRawKey? key,
      out string error,
      bool bigEndian = false) {
    key = null;
    error = string.Empty;

    if (encoded.Length < 3) {
      error = "bcachefs bkey header is truncated.";
      return false;
    }

    var totalBytes = encoded[0] * sizeof(ulong);
    if (totalBytes == 0 || totalBytes > encoded.Length) {
      error = $"bcachefs bkey claims {totalBytes} bytes but {encoded.Length} are available.";
      return false;
    }

    var format = (byte)(encoded[1] & 0x7F);
    var needsWhiteout = (encoded[1] & 0x80) != 0;
    var type = encoded[2];
    var keyU64s = format switch {
      KeyFormatCurrent => BkeyU64s,
      0 when nodeFormat != null => nodeFormat.KeyU64s,
      _ => 0,
    };

    if (format == 0 && nodeFormat == null) {
      error = "packed bcachefs key has no node-local bkey_format.";
      return false;
    }
    if (format is not (0 or KeyFormatCurrent)) {
      error = $"bcachefs key format {format} is newer than this codec.";
      return false;
    }

    var keyBytes = checked(keyU64s * sizeof(ulong));
    if (keyU64s <= 0 || keyBytes > totalBytes || keyBytes < 3) {
      error = $"bcachefs key format requires {keyBytes} key bytes inside a {totalBytes}-byte bkey.";
      return false;
    }

    var original = encoded[..totalBytes].ToArray();
    var canonical = bigEndian ? original.ToArray() : original;
    if (bigEndian)
      Array.Reverse(canonical, 3, keyBytes - 3);
    var source = canonical.AsSpan();

    Bpos position;
    uint size;
    BcacheFsKeyVersion version;
    int valueOffset;

    switch (format) {
      case KeyFormatCurrent:
        if (totalBytes < BkeyBytes) {
          error = $"unpacked bcachefs key is {totalBytes} bytes; header needs {BkeyBytes}.";
          return false;
        }
        version = new BcacheFsKeyVersion(
          BinaryPrimitives.ReadUInt64LittleEndian(source[4..]),
          BinaryPrimitives.ReadUInt32LittleEndian(source[12..]));
        size = BinaryPrimitives.ReadUInt32LittleEndian(source[16..]);
        position = ReadBpos(source[20..]);
        valueOffset = BkeyBytes;
        break;

      case 0:
        if (nodeFormat!.FieldCount != BcacheFsKeyFormat.FieldCountCurrent ||
            nodeFormat.Bits.Length < BcacheFsKeyFormat.FieldCountCurrent ||
            nodeFormat.Offsets.Length < BcacheFsKeyFormat.FieldCountCurrent) {
          error = $"packed bcachefs key uses an invalid node-local bkey_format ({nodeFormat.FieldCount} fields).";
          return false;
        }
        if (nodeFormat.KeyU64s <= 0 || nodeFormat.KeyU64s * sizeof(ulong) > totalBytes) {
          error = "packed bcachefs key has an invalid key_u64s.";
          return false;
        }

        try {
          var fields = UnpackFields(source[..(nodeFormat.KeyU64s * sizeof(ulong))], nodeFormat);
          position = new Bpos(fields[0], fields[1], checked((uint)fields[2]));
          size = checked((uint)fields[3]);
          version = new BcacheFsKeyVersion(fields[5], checked((uint)fields[4]));
        } catch (Exception ex) when (ex is InvalidDataException or OverflowException) {
          error = $"invalid packed bcachefs key: {ex.Message}";
          return false;
        }
        valueOffset = nodeFormat.KeyU64s * sizeof(ulong);
        break;

      default:
        throw new InvalidOperationException("validated bkey format became invalid");
    }

    // Values have type-specific endian compatibility rules. Preserve their raw
    // bytes here; semantic codecs that know the value type perform that step.
    var value = original.AsSpan(valueOffset, totalBytes - valueOffset).ToArray();
    key = new BcacheFsRawKey(format, needsWhiteout, type, version, position, size, value, original, bigEndian);
    return true;
  }

  /// <summary>
  /// Ports little-endian bcachefs get_inc_field() semantics: fields are consumed
  /// from the most significant bits of the last key word toward lower words,
  /// each value biased by field_offset[]. The first three bytes are the packed
  /// key header and therefore not part of field data.
  /// </summary>
  private static ulong[] UnpackFields(ReadOnlySpan<byte> keyArea, BcacheFsKeyFormat format) {
    if (keyArea.Length < 3)
      throw new InvalidDataException("packed key area is shorter than its three-byte header.");

    var availableBits = checked((keyArea.Length - 3) * 8);
    var requiredBits = 0;
    for (var field = 0; field < BcacheFsKeyFormat.FieldCountCurrent; ++field) {
      var width = format.Bits[field];
      if ((uint)width > 64)
        throw new InvalidDataException($"packed field {field} is {width} bits wide.");
      requiredBits = checked(requiredBits + width);
    }
    if (requiredBits > availableBits)
      throw new InvalidDataException($"packed fields need {requiredBits} bits but key area provides {availableBits}.");

    var packed = keyArea[3..].ToArray();
    Array.Reverse(packed);

    var result = new ulong[BcacheFsKeyFormat.FieldCountCurrent];
    var bit = 0;
    for (var field = 0; field < result.Length; ++field) {
      var width = format.Bits[field];
      ulong value = 0;
      for (var i = 0; i < width; ++i, ++bit) {
        var byteIndex = bit >> 3;
        var set = (packed[byteIndex] & (0x80 >> (bit & 7))) != 0;
        value = (value << 1) | (set ? 1UL : 0UL);
      }
      result[field] = checked(value + format.Offsets[field]);
    }

    return result;
  }
}
