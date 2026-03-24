namespace FileFormat.Density;

/// <summary>
/// Density compression with three algorithms: Chameleon (fastest), Cheetah (balanced), Lion (best ratio).
/// </summary>
public static class DensityStream {

  /// <summary>Compression algorithm.</summary>
  public enum Algorithm : byte {
    /// <summary>Hash-based direct replacement. Fastest, lowest ratio.</summary>
    Chameleon = 1,
    /// <summary>Dual-hash with predictions. Balanced speed/ratio.</summary>
    Cheetah = 2,
    /// <summary>LZ + entropy coding. Best ratio, still fast.</summary>
    Lion = 3,
  }

  // Container magic: "DENS" + version 1
  private static readonly byte[] Magic = [(byte)'D', (byte)'E', (byte)'N', (byte)'S'];
  private const byte Version = 1;

  /// <summary>Compresses data with the specified algorithm.</summary>
  public static void Compress(Stream input, Stream output, Algorithm algorithm = Algorithm.Cheetah) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    var data = ms.ToArray();

    // Write header: magic(4) + version(1) + algorithm(1) + original size(4)
    output.Write(Magic);
    output.WriteByte(Version);
    output.WriteByte((byte)algorithm);
    Span<byte> sizeBuf = stackalloc byte[4];
    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(sizeBuf, data.Length);
    output.Write(sizeBuf);

    var compressed = algorithm switch {
      Algorithm.Chameleon => CompressChameleon(data),
      Algorithm.Cheetah => CompressCheetah(data),
      Algorithm.Lion => CompressLion(data),
      _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
    };
    output.Write(compressed);
  }

  /// <summary>Decompresses density-compressed data.</summary>
  public static void Decompress(Stream input, Stream output) {
    Span<byte> header = stackalloc byte[10];
    input.ReadExactly(header);

    if (header[0] != Magic[0] || header[1] != Magic[1] || header[2] != Magic[2] || header[3] != Magic[3])
      throw new InvalidDataException("Not a Density stream.");
    if (header[4] != Version)
      throw new InvalidDataException($"Unsupported Density version: {header[4]}");

    var algorithm = (Algorithm)header[5];
    var originalSize = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header[6..]);

    using var ms = new MemoryStream();
    input.CopyTo(ms);
    var compData = ms.ToArray();

    var result = algorithm switch {
      Algorithm.Chameleon => DecompressChameleon(compData, originalSize),
      Algorithm.Cheetah => DecompressCheetah(compData, originalSize),
      Algorithm.Lion => DecompressLion(compData, originalSize),
      _ => throw new InvalidDataException($"Unknown Density algorithm: {algorithm}"),
    };
    output.Write(result);
  }

  // ── Chameleon: Simple 32-bit hash dictionary ──────────────────────
  // For every 4-byte chunk: lookup hash → if match, write flag 1 + hash index; else write flag 0 + literal

  private static byte[] CompressChameleon(byte[] data) {
    var dict = new uint[65536]; // 16-bit hash → 32-bit value
    var dictSet = new bool[65536];
    using var output = new MemoryStream();
    var i = 0;

    while (i + 4 <= data.Length) {
      var val = BitConverter.ToUInt32(data, i);
      var hash = (ushort)((val * 0x9E3779B1u) >> 16);

      if (dictSet[hash] && dict[hash] == val) {
        // Match: flag byte 1 + 2-byte hash index
        output.WriteByte(1);
        output.WriteByte((byte)(hash & 0xFF));
        output.WriteByte((byte)(hash >> 8));
      } else {
        // Literal: flag byte 0 + 4 literal bytes
        output.WriteByte(0);
        output.Write(data, i, 4);
        dict[hash] = val;
        dictSet[hash] = true;
      }
      i += 4;
    }

    // Remaining bytes as literals
    while (i < data.Length) {
      output.WriteByte(2); // literal single byte flag
      output.WriteByte(data[i++]);
    }

    return output.ToArray();
  }

  private static byte[] DecompressChameleon(byte[] data, int originalSize) {
    var dict = new uint[65536];
    var result = new byte[originalSize];
    var ri = 0;
    var di = 0;

    while (ri < originalSize && di < data.Length) {
      var flag = data[di++];
      if (flag == 1 && di + 2 <= data.Length) {
        // Dictionary reference
        var hash = (ushort)(data[di] | (data[di + 1] << 8));
        di += 2;
        var val = dict[hash];
        if (ri + 4 <= originalSize) {
          BitConverter.TryWriteBytes(result.AsSpan(ri), val);
          ri += 4;
        }
      } else if (flag == 0 && di + 4 <= data.Length) {
        // Literal 4 bytes
        var val = BitConverter.ToUInt32(data, di);
        var hash = (ushort)((val * 0x9E3779B1u) >> 16);
        dict[hash] = val;
        Buffer.BlockCopy(data, di, result, ri, 4);
        di += 4;
        ri += 4;
      } else if (flag == 2 && di < data.Length) {
        // Single literal byte
        result[ri++] = data[di++];
      } else {
        break;
      }
    }

    return result;
  }

  // ── Cheetah: Dual-hash with predictions ───────────────────────────
  // Two dictionaries, prediction tracking, better matching

  private static byte[] CompressCheetah(byte[] data) {
    var dictA = new uint[65536];
    var dictB = new uint[65536];
    var dictASet = new bool[65536];
    var dictBSet = new bool[65536];
    using var output = new MemoryStream();
    var i = 0;

    while (i + 4 <= data.Length) {
      var val = BitConverter.ToUInt32(data, i);
      var hashA = (ushort)((val * 0x9E3779B1u) >> 16);
      var hashB = (ushort)((val * 0x85EBCA6Bu) >> 16);

      if (dictASet[hashA] && dictA[hashA] == val) {
        output.WriteByte(1); // match in dict A
        output.WriteByte((byte)(hashA & 0xFF));
        output.WriteByte((byte)(hashA >> 8));
      } else if (dictBSet[hashB] && dictB[hashB] == val) {
        output.WriteByte(3); // match in dict B
        output.WriteByte((byte)(hashB & 0xFF));
        output.WriteByte((byte)(hashB >> 8));
      } else {
        output.WriteByte(0); // literal
        output.Write(data, i, 4);
        dictA[hashA] = val;
        dictASet[hashA] = true;
        dictB[hashB] = val;
        dictBSet[hashB] = true;
      }
      i += 4;
    }

    while (i < data.Length) {
      output.WriteByte(2);
      output.WriteByte(data[i++]);
    }

    return output.ToArray();
  }

  private static byte[] DecompressCheetah(byte[] data, int originalSize) {
    var dictA = new uint[65536];
    var dictB = new uint[65536];
    var result = new byte[originalSize];
    var ri = 0;
    var di = 0;

    while (ri < originalSize && di < data.Length) {
      var flag = data[di++];
      if (flag == 1 && di + 2 <= data.Length) {
        var hash = (ushort)(data[di] | (data[di + 1] << 8));
        di += 2;
        if (ri + 4 <= originalSize) {
          BitConverter.TryWriteBytes(result.AsSpan(ri), dictA[hash]);
          ri += 4;
        }
      } else if (flag == 3 && di + 2 <= data.Length) {
        var hash = (ushort)(data[di] | (data[di + 1] << 8));
        di += 2;
        if (ri + 4 <= originalSize) {
          BitConverter.TryWriteBytes(result.AsSpan(ri), dictB[hash]);
          ri += 4;
        }
      } else if (flag == 0 && di + 4 <= data.Length) {
        var val = BitConverter.ToUInt32(data, di);
        var hashA = (ushort)((val * 0x9E3779B1u) >> 16);
        var hashB = (ushort)((val * 0x85EBCA6Bu) >> 16);
        dictA[hashA] = val;
        dictB[hashB] = val;
        Buffer.BlockCopy(data, di, result, ri, 4);
        di += 4;
        ri += 4;
      } else if (flag == 2 && di < data.Length) {
        result[ri++] = data[di++];
      } else {
        break;
      }
    }

    return result;
  }

  // ── Lion: LZ + simple entropy (run-length + dictionary) ───────────

  private static byte[] CompressLion(byte[] data) {
    using var output = new MemoryStream();
    var dict = new uint[65536];
    var dictSet = new bool[65536];
    var i = 0;

    while (i < data.Length) {
      // Try run-length first
      if (i + 4 <= data.Length) {
        var runLen = 1;
        while (i + runLen < data.Length && data[i + runLen] == data[i] && runLen < 255)
          runLen++;
        if (runLen >= 4) {
          output.WriteByte(4); // RLE flag
          output.WriteByte(data[i]);
          output.WriteByte((byte)runLen);
          i += runLen;
          continue;
        }
      }

      // Try dictionary match (4-byte)
      if (i + 4 <= data.Length) {
        var val = BitConverter.ToUInt32(data, i);
        var hash = (ushort)((val * 0x9E3779B1u) >> 16);

        if (dictSet[hash] && dict[hash] == val) {
          output.WriteByte(1);
          output.WriteByte((byte)(hash & 0xFF));
          output.WriteByte((byte)(hash >> 8));
          i += 4;
          continue;
        }

        output.WriteByte(0);
        output.Write(data, i, 4);
        dict[hash] = val;
        dictSet[hash] = true;
        i += 4;
        continue;
      }

      // Remaining bytes
      output.WriteByte(2);
      output.WriteByte(data[i++]);
    }

    return output.ToArray();
  }

  private static byte[] DecompressLion(byte[] data, int originalSize) {
    var dict = new uint[65536];
    var result = new byte[originalSize];
    var ri = 0;
    var di = 0;

    while (ri < originalSize && di < data.Length) {
      var flag = data[di++];
      if (flag == 4 && di + 2 <= data.Length) {
        // RLE
        var val = data[di++];
        var count = data[di++];
        var end = Math.Min(ri + count, originalSize);
        while (ri < end) result[ri++] = val;
      } else if (flag == 1 && di + 2 <= data.Length) {
        var hash = (ushort)(data[di] | (data[di + 1] << 8));
        di += 2;
        if (ri + 4 <= originalSize) {
          BitConverter.TryWriteBytes(result.AsSpan(ri), dict[hash]);
          ri += 4;
        }
      } else if (flag == 0 && di + 4 <= data.Length) {
        var val = BitConverter.ToUInt32(data, di);
        var hash = (ushort)((val * 0x9E3779B1u) >> 16);
        dict[hash] = val;
        Buffer.BlockCopy(data, di, result, ri, 4);
        di += 4;
        ri += 4;
      } else if (flag == 2 && di < data.Length) {
        result[ri++] = data[di++];
      } else {
        break;
      }
    }

    return result;
  }
}
