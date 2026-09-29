#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Core.Checksums;

namespace FileFormat.Partclone;

/// <summary>Creates Partclone 0002 images from a raw disk image and its metadata.</summary>
public static class PartcloneWriter {
  private static readonly UTF8Encoding Utf8 = new(false);

  public static void Write(Stream output, Stream disk, ReadOnlySpan<byte> metadata, ReadOnlySpan<byte> allocationMap,
    IReadOnlyDictionary<string, string>? overrides = null) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(disk);
    if (!output.CanWrite || !disk.CanRead || !disk.CanSeek)
      throw new ArgumentException("Partclone creation requires a writable output and readable, seekable disk image.");

    var values = ParseMetadata(metadata);
    string Get(string key, string fallback) {
      var optionKey = key switch {
        "fs" => "Fs",
        "block_size" => "BlockSize",
        "checksum_mode" => "ChecksumMode",
        "blocks_per_checksum" => "BlocksPerChecksum",
        "bitmap_mode" => "BitmapMode",
        _ => key,
      };
      if (overrides is not null && (overrides.TryGetValue(optionKey, out var value) || overrides.TryGetValue(key, out value)))
        return value;
      return values.TryGetValue(key, out var metadataValue) ? metadataValue : fallback;
    }
    uint ParseUInt(string key, uint fallback) => uint.TryParse(Get(key, fallback.ToString(CultureInfo.InvariantCulture)), NumberStyles.Integer,
      CultureInfo.InvariantCulture, out var value) ? value : fallback;
    ushort ParseUShort(string key, ushort fallback) => ushort.TryParse(Get(key, fallback.ToString(CultureInfo.InvariantCulture)), NumberStyles.Integer,
      CultureInfo.InvariantCulture, out var value) ? value : fallback;

    var blockSize = ParseUInt("block_size", 4096);
    if (blockSize is 0 or > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(metadata), "block_size must be between 1 and Int32.MaxValue.");
    var totalBlocks = ParseUInt64(Get("total_blocks", "0"));
    var deviceSize = ParseUInt64(Get("device_size", "0"));
    if (totalBlocks == 0) {
      if (disk.Length % blockSize != 0) throw new InvalidDataException("Raw image length is not a multiple of block_size.");
      totalBlocks = (ulong)(disk.Length / blockSize);
    }
    if (totalBlocks > int.MaxValue) throw new NotSupportedException("Partclone bitmap exceeds the current in-memory map limit.");
    var virtualLength = checked(totalBlocks * blockSize);
    if ((ulong)disk.Length != virtualLength) throw new InvalidDataException("Raw image length does not match total_blocks × block_size.");
    deviceSize = deviceSize == 0 ? virtualLength : deviceSize;

    var bitmapMode = byte.TryParse(Get("bitmap_mode", "1"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bm) ? bm : (byte)1;
    if (bitmapMode is not (PartcloneReader.BmBit or PartcloneReader.BmByte))
      throw new NotSupportedException($"Partclone bitmap mode {bitmapMode} cannot represent sparse allocation.");
    var mapLength = bitmapMode == PartcloneReader.BmBit ? checked((int)((totalBlocks + 7) / 8)) : checked((int)totalBlocks);
    var map = allocationMap.IsEmpty ? new byte[mapLength] : allocationMap.ToArray();
    if (map.Length != mapLength) throw new InvalidDataException("allocation.map length does not match the selected bitmap mode and total_blocks.");

    var usedBlocks = 0UL;
    if (allocationMap.IsEmpty) {
      var block = new byte[blockSize];
      for (ulong index = 0; index < totalBlocks; ++index) {
        ReadExactly(disk, block);
        var used = block.AsSpan().IndexOfAnyExcept((byte)0) >= 0;
        if (!used) continue;
        SetUsed(map, bitmapMode, index);
        ++usedBlocks;
      }
      disk.Position = 0;
    } else
      for (ulong index = 0; index < totalBlocks; ++index)
        if (IsUsed(map, bitmapMode, index)) ++usedBlocks;

    var checksumMode = ParseUShort("checksum_mode", 1);
    var checksumSize = ParseUShort("checksum_size", checksumMode switch { 0 => (ushort)0, 2 => (ushort)8, _ => (ushort)4 });
    var blocksPerChecksum = ParseUInt("blocks_per_checksum", checksumMode == 0 ? 0 : 256);
    var reseedChecksum = byte.TryParse(Get("reseed_checksum", "1"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var reseedValue)
      ? reseedValue : (byte)1;
    if (checksumMode is not (0 or 1 or 2)) throw new NotSupportedException("Partclone checksum modes 0 (none), 1 (CRC32), and 2 (XXH64) are supported.");
    if ((checksumMode == 0 && (checksumSize != 0 || blocksPerChecksum != 0)) ||
        (checksumMode == 1 && (checksumSize != 4 || blocksPerChecksum == 0)) ||
        (checksumMode == 2 && (checksumSize != 8 || blocksPerChecksum == 0)))
      throw new InvalidDataException("Invalid Partclone checksum size/strip configuration.");
    if (checksumMode == 0) reseedChecksum = 1;

    Span<byte> head = stackalloc byte[31];
    head.Clear();
    PartcloneReader.Magic.CopyTo(head);
    Encoding.ASCII.GetBytes(Get("ptc_version", "0.3.37").AsSpan(0, Math.Min(14, Get("ptc_version", "0.3.37").Length)), head[15..29]);
    BinaryPrimitives.WriteUInt16LittleEndian(head[29..], PartcloneReader.EndianMagic);
    output.Write(head);

    Span<byte> fsInfo = stackalloc byte[51];
    fsInfo.Clear();
    var fsBytes = Encoding.ASCII.GetBytes(Get("fs", "raw"));
    fsBytes.AsSpan(0, Math.Min(fsBytes.Length, 15)).CopyTo(fsInfo);
    BinaryPrimitives.WriteUInt64LittleEndian(fsInfo[15..], deviceSize);
    BinaryPrimitives.WriteUInt64LittleEndian(fsInfo[23..], totalBlocks);
    BinaryPrimitives.WriteUInt64LittleEndian(fsInfo[31..], usedBlocks);
    BinaryPrimitives.WriteUInt64LittleEndian(fsInfo[39..], ParseUInt64(Get("superblock_used_blocks", usedBlocks.ToString(CultureInfo.InvariantCulture))));
    BinaryPrimitives.WriteUInt32LittleEndian(fsInfo[47..], blockSize);
    output.Write(fsInfo);

    Span<byte> options = stackalloc byte[22];
    options.Clear();
    BinaryPrimitives.WriteUInt32LittleEndian(options, 22);
    BinaryPrimitives.WriteUInt16LittleEndian(options[4..], 2);
    BinaryPrimitives.WriteUInt16LittleEndian(options[6..], ParseUShort("cpu_bits", 64));
    BinaryPrimitives.WriteUInt16LittleEndian(options[8..], checksumMode);
    BinaryPrimitives.WriteUInt16LittleEndian(options[10..], checksumSize);
    BinaryPrimitives.WriteUInt32LittleEndian(options[12..], blocksPerChecksum);
    options[16] = reseedChecksum;
    options[17] = bitmapMode;
    BinaryPrimitives.WriteUInt32LittleEndian(options[18..], Crc32(options[..18]));
    output.Write(options);

    output.Write(map);
    if (checksumMode == 1) WriteUInt32(output, Crc32(map));
    else if (checksumMode == 2) WriteUInt64(output, XxHash64.Compute(map));

    disk.Position = 0;
    var buffer = new byte[blockSize];
    var inStrip = 0U;
    var writtenBlocks = 0UL;
    var crcState = 0xFFFFFFFFU;
    var xxHash = new XxHash64();
    for (ulong index = 0; index < totalBlocks; ++index) {
      ReadExactly(disk, buffer);
      if (!IsUsed(map, bitmapMode, index)) continue;
      output.Write(buffer);
      if (checksumMode == 0) continue;
      if (checksumMode == 1) crcState = UpdateCrc32(crcState, buffer);
      else xxHash.Update(buffer);
      ++inStrip;
      ++writtenBlocks;
      if (inStrip < blocksPerChecksum && writtenBlocks < usedBlocks) continue;
      if (checksumMode == 1) WriteUInt32(output, ~crcState);
      else WriteUInt64(output, xxHash.Value);
      inStrip = 0;
      if (reseedChecksum != 0) {
        crcState = 0xFFFFFFFFU;
        xxHash = new XxHash64();
      }
    }
  }

  private static Dictionary<string, string> ParseMetadata(ReadOnlySpan<byte> bytes) {
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var line in Utf8.GetString(bytes).Split('\n')) {
      var text = line.Trim();
      if (text.Length == 0 || text[0] is '#' or '[') continue;
      var separator = text.IndexOf('=');
      if (separator > 0) result[text[..separator].Trim()] = text[(separator + 1)..].Trim();
    }
    return result;
  }

  private static ulong ParseUInt64(string value) => ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
    ? result : throw new InvalidDataException($"Invalid Partclone integer value: {value}");

  private static bool IsUsed(ReadOnlySpan<byte> map, byte mode, ulong index) => mode == PartcloneReader.BmBit
    ? (map[checked((int)(index / 8))] & (1 << (int)(index % 8))) != 0
    : map[checked((int)index)] != 0;

  private static void SetUsed(Span<byte> map, byte mode, ulong index) {
    if (mode == PartcloneReader.BmBit) map[checked((int)(index / 8))] |= (byte)(1 << (int)(index % 8));
    else map[checked((int)index)] = 1;
  }

  private static uint Crc32(ReadOnlySpan<byte> data) {
    return ~UpdateCrc32(0xFFFFFFFFU, data);
  }

  private static uint UpdateCrc32(uint crc, ReadOnlySpan<byte> data) {
    foreach (var value in data) {
      crc ^= value;
      for (var bit = 0; bit < 8; ++bit) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320U : 0U);
    }
    return crc;
  }

  private static void WriteUInt32(Stream stream, uint value) {
    Span<byte> bytes = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
    stream.Write(bytes);
  }

  private static void WriteUInt64(Stream stream, ulong value) {
    Span<byte> bytes = stackalloc byte[8];
    BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
    stream.Write(bytes);
  }

  private static void ReadExactly(Stream stream, Span<byte> buffer) {
    var count = 0;
    while (count < buffer.Length) {
      var read = stream.Read(buffer[count..]);
      if (read == 0) throw new EndOfStreamException("Raw Partclone input ended before its declared size.");
      count += read;
    }
  }
}
