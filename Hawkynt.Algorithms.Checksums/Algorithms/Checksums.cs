using System.Buffers.Binary;
using System.Numerics;

namespace Hawkynt.Algorithms.Checksums;

/// <summary>Adler checksum family matching the variants in Hawkynt's algorithm registry.</summary>
public static class Adler {
  public static ushort Compute16(ReadOnlySpan<byte> data) {
    const uint modulo = 251;
    uint a = 1, b = 0;
    foreach (var value in data) {
      a = (a + value) % modulo;
      b = (b + a) % modulo;
    }
    return (ushort)((b << 8) | a);
  }

  public static uint Compute32(ReadOnlySpan<byte> data) {
    const uint modulo = 65521;
    uint a = 1, b = 0;
    foreach (var value in data) {
      a += value;
      b += a;
      a %= modulo;
      b %= modulo;
    }
    return (b << 16) | a;
  }

  public static ulong Compute64(ReadOnlySpan<byte> data) {
    const ulong modulo = 4294967291UL;
    ulong a = 1, b = 0;
    foreach (var value in data) {
      a = (a + value) % modulo;
      b = (b + a) % modulo;
    }
    return (b << 32) | a;
  }
}

/// <summary>Fletcher checksum family. The 32/64-bit variants deliberately consume bytes, matching the source registry.</summary>
public static class Fletcher {
  public static byte Compute8(ReadOnlySpan<byte> data) {
    const uint modulo = 15;
    uint sum1 = 0, sum2 = 0;
    foreach (var value in data) {
      sum1 = (sum1 + value) % modulo;
      sum2 = (sum2 + sum1) % modulo;
    }
    return (byte)((sum2 << 4) | sum1);
  }

  public static ushort Compute16(ReadOnlySpan<byte> data) {
    const uint modulo = 255;
    uint sum1 = 0, sum2 = 0;
    foreach (var value in data) {
      sum1 = (sum1 + value) % modulo;
      sum2 = (sum2 + sum1) % modulo;
    }
    return (ushort)((sum2 << 8) | sum1);
  }

  public static uint Compute32(ReadOnlySpan<byte> data) {
    const uint modulo = 65535;
    uint sum1 = 0, sum2 = 0;
    foreach (var value in data) {
      sum1 = (sum1 + value) % modulo;
      sum2 = (sum2 + sum1) % modulo;
    }
    return (sum2 << 16) | sum1;
  }

  public static ulong Compute64(ReadOnlySpan<byte> data) {
    const ulong modulo = 4294967295UL;
    ulong sum1 = 0, sum2 = 0;
    foreach (var value in data) {
      sum1 = (sum1 + value) % modulo;
      sum2 = (sum2 + sum1) % modulo;
    }
    return (sum2 << 32) | sum1;
  }
}

/// <summary>BSD rotating checksum used by historic <c>sum -r</c>.</summary>
public static class BsdChecksum {
  public static ushort Compute(ReadOnlySpan<byte> data) {
    ushort checksum = 0;
    foreach (var value in data) {
      checksum = (ushort)((checksum >> 1) | ((checksum & 1) << 15));
      checksum = (ushort)(checksum + value);
    }
    return checksum;
  }
}

/// <summary>System V checksum used by historic <c>sum -s</c>.</summary>
public static class SysVChecksum {
  public static ushort Compute(ReadOnlySpan<byte> data) {
    uint sum = 0;
    foreach (var value in data)
      sum += value;
    sum = (sum & 0xFFFF) + (sum >> 16);
    sum = (sum & 0xFFFF) + (sum >> 16);
    return (ushort)sum;
  }
}

/// <summary>Simple additive checksum variants.</summary>
public static class SumChecksum {
  public static byte Compute8(ReadOnlySpan<byte> data) {
    uint sum = 0;
    foreach (var value in data)
      sum += value;
    return (byte)sum;
  }

  public static ushort Compute16(ReadOnlySpan<byte> data) {
    uint sum = 0;
    foreach (var value in data)
      sum += value;
    return (ushort)sum;
  }

  public static uint Compute32(ReadOnlySpan<byte> data) {
    uint sum = 0;
    foreach (var value in data)
      sum = unchecked(sum + value);
    return sum;
  }
}

/// <summary>Longitudinal redundancy check (two's complement of the 8-bit byte sum).</summary>
public static class Lrc {
  public static byte Compute(ReadOnlySpan<byte> data) {
    byte sum = 0;
    foreach (var value in data)
      sum = unchecked((byte)(sum + value));
    return unchecked((byte)(0 - sum));
  }
}

/// <summary>XOR checksum, also used by NMEA-0183 sentence checksums.</summary>
public static class XorChecksum {
  public static byte Compute(ReadOnlySpan<byte> data) {
    byte checksum = 0;
    foreach (var value in data)
      checksum ^= value;
    return checksum;
  }
}

/// <summary>Internet checksum from RFC 1071 (one's-complement sum of big-endian 16-bit words).</summary>
public static class InternetChecksum {
  public static ushort Compute(ReadOnlySpan<byte> data) {
    uint sum = 0;
    var offset = 0;
    while (offset + 1 < data.Length) {
      sum += BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
      sum = (sum & 0xFFFF) + (sum >> 16);
      offset += 2;
    }
    if (offset < data.Length) {
      sum += (uint)data[offset] << 8;
      sum = (sum & 0xFFFF) + (sum >> 16);
    }
    while ((sum >> 16) != 0)
      sum = (sum & 0xFFFF) + (sum >> 16);
    return (ushort)~sum;
  }

  public static bool Verify(ReadOnlySpan<byte> dataIncludingChecksum) => Compute(dataIncludingChecksum) == 0;
}

/// <summary>One's and two's complement checksum helpers.</summary>
public static class ComplementChecksum {
  public static ushort OnesComplement16(ReadOnlySpan<byte> data) => InternetChecksum.Compute(data);

  public static byte TwosComplement8(ReadOnlySpan<byte> data) {
    uint sum = 0;
    foreach (var value in data)
      sum = (sum + value) & 0xFF;
    return (byte)(0u - sum);
  }

  public static ushort TwosComplement16(ReadOnlySpan<byte> data) {
    uint sum = 0;
    foreach (var value in data)
      sum = (sum + value) & 0xFFFF;
    return (ushort)(0u - sum);
  }
}

/// <summary>Parity helpers.</summary>
public static class Parity {
  public static int BitParity(byte value) => BitOperations.PopCount((uint)value) & 1;

  public static byte EvenParityBit(byte value) => (byte)BitParity(value);

  public static byte OddParityBit(byte value) => (byte)(BitParity(value) ^ 1);

  public static byte BlockParity(ReadOnlySpan<byte> data) {
    byte parity = 0;
    foreach (var value in data)
      parity ^= value;
    return parity;
  }
}

/// <summary>NMEA-0183 XOR checksum. Delimiters '$'/'!' and '*' plus suffix are ignored when present.</summary>
public static class Nmea0183 {
  public static byte Compute(ReadOnlySpan<byte> sentence) {
    var start = 0;
    if (!sentence.IsEmpty && (sentence[0] == (byte)'$' || sentence[0] == (byte)'!'))
      start = 1;

    byte checksum = 0;
    for (var i = start; i < sentence.Length; ++i) {
      if (sentence[i] == (byte)'*')
        break;
      checksum ^= sentence[i];
    }
    return checksum;
  }
}

/// <summary>Parameters for CRC widths from 8 through 64 bits.</summary>
public readonly record struct CrcParameters(
  int Width,
  ulong Polynomial,
  ulong InitialValue,
  bool ReflectInput,
  bool ReflectOutput,
  ulong FinalXor
);

/// <summary>Bit-accurate generic CRC implementation using normal-form polynomials.</summary>
public static class Crc {
  public static ulong Compute(ReadOnlySpan<byte> data, CrcParameters parameters) {
    if (parameters.Width is < 8 or > 64)
      throw new ArgumentOutOfRangeException(nameof(parameters), "CRC width must be between 8 and 64 bits.");

    var width = parameters.Width;
    var mask = width == 64 ? ulong.MaxValue : (1UL << width) - 1;
    var topBit = 1UL << (width - 1);
    var crc = parameters.InitialValue & mask;
    var polynomial = parameters.Polynomial & mask;

    foreach (var original in data) {
      var value = parameters.ReflectInput ? (byte)Reflect(original, 8) : original;
      crc ^= (ulong)value << (width - 8);
      for (var bit = 0; bit < 8; ++bit)
        crc = (crc & topBit) != 0 ? ((crc << 1) ^ polynomial) & mask : (crc << 1) & mask;
    }

    if (parameters.ReflectOutput)
      crc = Reflect(crc, width);

    return (crc ^ parameters.FinalXor) & mask;
  }

  public static byte Compute8(ReadOnlySpan<byte> data, CrcParameters parameters) => (byte)Compute(data, parameters);
  public static ushort Compute16(ReadOnlySpan<byte> data, CrcParameters parameters) => (ushort)Compute(data, parameters);
  public static uint Compute24(ReadOnlySpan<byte> data, CrcParameters parameters) => (uint)Compute(data, parameters);
  public static uint Compute32(ReadOnlySpan<byte> data, CrcParameters parameters) => (uint)Compute(data, parameters);
  public static ulong Compute64(ReadOnlySpan<byte> data, CrcParameters parameters) => Compute(data, parameters);

  private static ulong Reflect(ulong value, int width) {
    ulong reflected = 0;
    for (var bit = 0; bit < width; ++bit) {
      if (((value >> bit) & 1) != 0)
        reflected |= 1UL << (width - 1 - bit);
    }
    return reflected;
  }
}

/// <summary>CRC parameter presets represented by the JavaScript source registry plus common interoperable aliases.</summary>
public static class CrcPresets {
  public static readonly CrcParameters Crc8Smbus = new(8, 0x07, 0x00, false, false, 0x00);
  public static readonly CrcParameters Crc8Maxim = new(8, 0x31, 0x00, true, true, 0x00);
  public static readonly CrcParameters Crc8Autosar = new(8, 0x2F, 0xFF, false, false, 0xFF);
  public static readonly CrcParameters Crc8Cdma2000 = new(8, 0x9B, 0xFF, false, false, 0x00);

  public static readonly CrcParameters Crc16Ccitt = new(16, 0x1021, 0x0000, false, false, 0x0000);
  public static readonly CrcParameters Crc16Arc = new(16, 0x8005, 0x0000, true, true, 0x0000);
  public static readonly CrcParameters Crc16Ibm = Crc16Arc;
  public static readonly CrcParameters Crc16Ansi = new(16, 0x8005, 0xFFFF, true, true, 0x0000);
  public static readonly CrcParameters Crc16Xmodem = Crc16Ccitt;

  public static readonly CrcParameters Crc24OpenPgp = new(24, 0x864CFB, 0xB704CE, false, false, 0x000000);
  public static readonly CrcParameters Crc24FlexRay = new(24, 0x5D6DCB, 0xFEDCBA, false, false, 0x000000);
  public static readonly CrcParameters Crc24Interlaken = new(24, 0x328B63, 0xFFFFFF, false, false, 0xFFFFFF);

  public static readonly CrcParameters Crc32Ieee = new(32, 0x04C11DB7, 0xFFFFFFFF, true, true, 0xFFFFFFFF);
  public static readonly CrcParameters Crc32Posix = new(32, 0x04C11DB7, 0x00000000, false, false, 0xFFFFFFFF);
  public static readonly CrcParameters Crc32Bzip2 = new(32, 0x04C11DB7, 0xFFFFFFFF, false, false, 0xFFFFFFFF);
  public static readonly CrcParameters Crc32Castagnoli = new(32, 0x1EDC6F41, 0xFFFFFFFF, true, true, 0xFFFFFFFF);

  public static readonly CrcParameters Crc64Xz = new(64, 0x42F0E1EBA9EA3693, ulong.MaxValue, true, true, ulong.MaxValue);
  public static readonly CrcParameters Crc64Ecma182 = new(64, 0x42F0E1EBA9EA3693, 0, false, false, 0);
  public static readonly CrcParameters Crc64We = new(64, 0x42F0E1EBA9EA3693, ulong.MaxValue, false, false, ulong.MaxValue);
}

/// <summary>Parameters for the educational 128-bit CRC variants in the source registry.</summary>
public readonly record struct Crc128Parameters(
  UInt128 Polynomial,
  UInt128 InitialValue,
  bool ReflectInput,
  bool ReflectOutput,
  UInt128 FinalXor
);

/// <summary>Generic 128-bit CRC using normal-form polynomials.</summary>
public static class Crc128 {
  private static readonly UInt128 TopBit = UInt128.One << 127;

  public static UInt128 Compute(ReadOnlySpan<byte> data, Crc128Parameters parameters) {
    var crc = parameters.InitialValue;
    foreach (var original in data) {
      var value = parameters.ReflectInput ? ReflectByte(original) : original;
      crc ^= (UInt128)value << 120;
      for (var bit = 0; bit < 8; ++bit)
        crc = (crc & TopBit) != 0 ? (crc << 1) ^ parameters.Polynomial : crc << 1;
    }
    if (parameters.ReflectOutput)
      crc = Reflect(crc);
    return crc ^ parameters.FinalXor;
  }

  private static byte ReflectByte(byte value) {
    var result = 0;
    for (var bit = 0; bit < 8; ++bit)
      if (((value >> bit) & 1) != 0)
        result |= 1 << (7 - bit);
    return (byte)result;
  }

  private static UInt128 Reflect(UInt128 value) {
    var result = UInt128.Zero;
    for (var bit = 0; bit < 128; ++bit)
      if (((value >> bit) & UInt128.One) != 0)
        result |= UInt128.One << (127 - bit);
    return result;
  }
}

/// <summary>128-bit CRC presets carried by the educational source registry.</summary>
public static class Crc128Presets {
  public static readonly Crc128Parameters Standard = new(
    (UInt128)0x87,
    UInt128.Zero,
    false,
    false,
    UInt128.Zero
  );

  public static readonly Crc128Parameters Hpc = new(
    ((UInt128)0xE0000000 << 96) | ((UInt128)0x02008000 << 64) | ((UInt128)0x00800000 << 32) | 0x000000AB,
    UInt128.MaxValue,
    false,
    false,
    UInt128.MaxValue
  );

  public static readonly Crc128Parameters BigData = new(
    ((UInt128)0x00000001 << 96) | ((UInt128)0x01010100 << 64) | ((UInt128)0x00010001 << 32) | 0x00010103,
    UInt128.Zero,
    false,
    false,
    UInt128.Zero
  );
}
