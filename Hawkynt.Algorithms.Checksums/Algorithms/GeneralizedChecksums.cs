using System.Collections.Concurrent;
using System.Numerics;

namespace Hawkynt.Algorithms.Checksums;

/// <summary>Selects the complement arithmetic used by <see cref="ComplementChecksum"/>.</summary>
public enum ComplementKind {
  /// <summary>
  /// Selects one's-complement arithmetic.
  /// </summary>
  OnesComplement,
  /// <summary>
  /// Selects two's-complement arithmetic.
  /// </summary>
  TwosComplement
}

/// <summary>Arbitrary-width Adler checksum entry point.</summary>
public static class AdlerGeneralizedChecksumExtensions {
  extension(Adler) {
    /// <summary>
    /// Computes a generalized Adler checksum. The result width may be any power of two (except one bit,
    /// because Adler stores two equal-width accumulators) or any whole-byte width.
    /// </summary>
    /// <remarks>
    /// The two half-width accumulators use the largest prime not greater than <c>2^(width/2)</c>.
    /// This reproduces Adler-16, Adler-32 and Adler-64 exactly while defining the intervening widths.
    /// The returned bytes are big-endian; unused high bits in a sub-byte result are zero.
    /// </remarks>
    public static byte[] Compute(ReadOnlySpan<byte> data, int checksumSizeBits) =>
      GeneralizedChecksumCore.ComputeAdler(data, checksumSizeBits);
  }
}

/// <summary>Arbitrary-width Fletcher checksum entry point.</summary>
public static class FletcherGeneralizedChecksumExtensions {
  extension(Fletcher) {
    /// <summary>
    /// Computes a generalized Fletcher checksum. The result width may be any power of two (except one bit,
    /// because Fletcher stores two equal-width accumulators) or any whole-byte width.
    /// </summary>
    /// <remarks>
    /// The two half-width accumulators use modulus <c>2^(width/2)-1</c>. Input is consumed as bytes,
    /// preserving the source-registry behavior of the existing Fletcher-8/16/32/64 variants.
    /// The returned bytes are big-endian; unused high bits in a sub-byte result are zero.
    /// </remarks>
    public static byte[] Compute(ReadOnlySpan<byte> data, int checksumSizeBits) =>
      GeneralizedChecksumCore.ComputeFletcher(data, checksumSizeBits);
  }
}

/// <summary>Arbitrary-width additive checksum entry point.</summary>
public static class SumGeneralizedChecksumExtensions {
  extension(SumChecksum) {
    /// <summary>
    /// Computes the byte sum modulo <c>2^checksumSizeBits</c>. The width may be any power of two or any
    /// whole-byte width. The returned bytes are big-endian; unused high bits in a sub-byte result are zero.
    /// </summary>
    public static byte[] Compute(ReadOnlySpan<byte> data, int checksumSizeBits) =>
      GeneralizedChecksumCore.ComputeSum(data, checksumSizeBits);
  }
}

/// <summary>Arbitrary-width complement checksum entry point.</summary>
public static class ComplementGeneralizedChecksumExtensions {
  extension(ComplementChecksum) {
    /// <summary>
    /// Computes an arbitrary-width one's- or two's-complement checksum. The width may be any power of two
    /// or any whole-byte width. The returned bytes are big-endian; unused high bits in a sub-byte result are zero.
    /// </summary>
    /// <remarks>
    /// Two's complement negates the byte sum modulo <c>2^width</c>. One's complement interprets the input
    /// as a big-endian bit stream of <c>width</c>-bit words, right-pads the final partial word with zero bits,
    /// performs end-around-carry addition, then complements the result.
    /// </remarks>
    public static byte[] Compute(
      ReadOnlySpan<byte> data,
      int checksumSizeBits,
      ComplementKind kind = ComplementKind.TwosComplement
    ) => GeneralizedChecksumCore.ComputeComplement(data, checksumSizeBits, kind);
  }
}

/// <summary>Arbitrary-width longitudinal parity entry point.</summary>
public static class ParityGeneralizedChecksumExtensions {
  extension(Parity) {
    /// <summary>
    /// Computes longitudinal parity at any power-of-two width or any whole-byte width.
    /// </summary>
    /// <remarks>
    /// For one bit this is ordinary bit parity. Sub-byte words are consumed most-significant-bit first.
    /// Byte-aligned widths XOR equal byte positions in consecutive words, with the final partial word
    /// right-padded with zeros. The returned bytes are big-endian and unused high bits are zero.
    /// </remarks>
    public static byte[] Compute(ReadOnlySpan<byte> data, int checksumSizeBits) =>
      GeneralizedChecksumCore.ComputeParity(data, checksumSizeBits);
  }
}

internal static class GeneralizedChecksumCore {
  // ReadOnlySpan<byte>.Length cannot exceed int.MaxValue. Even if every input byte is 0xFF,
  // the second Adler/Fletcher accumulator is therefore below 2^69. For half-widths >= 72,
  // neither a Fletcher modulus nor Adler's prime (which is > 2^(halfWidth-1)) can affect the state.
  private const int ModuloFreeHalfWidth = 72;

  // Consecutive-prime Miller-Rabin witnesses are deterministic well beyond 2^68. 68 bits is the
  // largest Adler half-width for which modular reduction can ever matter for a ReadOnlySpan<byte>.
  private static readonly int[] AdlerPrimeWitnesses = [2, 3, 5, 7, 11, 13, 17, 19, 23, 29, 31, 37];
  private static readonly ConcurrentDictionary<int, BigInteger> AdlerModuli = new();

  internal static byte[] ComputeAdler(ReadOnlySpan<byte> data, int checksumSizeBits) {
    var outputBytes = ValidateSize(checksumSizeBits, requireEvenWidth: true);
    var halfBits = checksumSizeBits / 2;
    BigInteger a = BigInteger.One;
    BigInteger b = BigInteger.Zero;

    if (halfBits >= ModuloFreeHalfWidth) {
      foreach (var value in data) {
        a += value;
        b += a;
      }
    } else {
      var modulus = AdlerModuli.GetOrAdd(halfBits, static bits => FindAdlerModulus(bits));
      foreach (var value in data) {
        a = (a + value) % modulus;
        b = (b + a) % modulus;
      }
    }

    return ToFixedBigEndian((b << halfBits) | a, outputBytes);
  }

  internal static byte[] ComputeFletcher(ReadOnlySpan<byte> data, int checksumSizeBits) {
    var outputBytes = ValidateSize(checksumSizeBits, requireEvenWidth: true);
    var halfBits = checksumSizeBits / 2;
    BigInteger sum1 = BigInteger.Zero;
    BigInteger sum2 = BigInteger.Zero;

    if (halfBits >= ModuloFreeHalfWidth) {
      foreach (var value in data) {
        sum1 += value;
        sum2 += sum1;
      }
    } else {
      var modulus = (BigInteger.One << halfBits) - BigInteger.One;
      foreach (var value in data) {
        sum1 = (sum1 + value) % modulus;
        sum2 = (sum2 + sum1) % modulus;
      }
    }

    return ToFixedBigEndian((sum2 << halfBits) | sum1, outputBytes);
  }

  internal static byte[] ComputeSum(ReadOnlySpan<byte> data, int checksumSizeBits) {
    var outputBytes = ValidateSize(checksumSizeBits);
    BigInteger sum = BigInteger.Zero;
    foreach (var value in data)
      sum += value;

    return ToFixedBigEndian(sum & CreateMask(checksumSizeBits), outputBytes);
  }

  internal static byte[] ComputeComplement(ReadOnlySpan<byte> data, int checksumSizeBits, ComplementKind kind) {
    var outputBytes = ValidateSize(checksumSizeBits);
    return kind switch {
      ComplementKind.OnesComplement => ComputeOnesComplement(data, checksumSizeBits, outputBytes),
      ComplementKind.TwosComplement => ComputeTwosComplement(data, checksumSizeBits, outputBytes),
      _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
  }

  internal static byte[] ComputeParity(ReadOnlySpan<byte> data, int checksumSizeBits) {
    var outputBytes = ValidateSize(checksumSizeBits);

    if (checksumSizeBits < 8) {
      var value = ComputeSubByteParity(data, checksumSizeBits);
      return [(byte)value];
    }

    var result = new byte[outputBytes];
    for (var i = 0; i < data.Length; ++i)
      result[i % outputBytes] ^= data[i];
    return result;
  }

  private static byte[] ComputeTwosComplement(ReadOnlySpan<byte> data, int checksumSizeBits, int outputBytes) {
    BigInteger sum = BigInteger.Zero;
    foreach (var value in data)
      sum += value;

    var mask = CreateMask(checksumSizeBits);
    return ToFixedBigEndian((-sum) & mask, outputBytes);
  }

  private static byte[] ComputeOnesComplement(ReadOnlySpan<byte> data, int checksumSizeBits, int outputBytes) {
    var mask = CreateMask(checksumSizeBits);
    BigInteger sum = BigInteger.Zero;
    BigInteger word = BigInteger.Zero;
    var wordBits = 0;

    foreach (var value in data) {
      for (var bit = 7; bit >= 0; --bit) {
        word = (word << 1) | ((value >> bit) & 1);
        ++wordBits;
        if (wordBits != checksumSizeBits)
          continue;

        sum = FoldOnesComplement(sum + word, checksumSizeBits, mask);
        word = BigInteger.Zero;
        wordBits = 0;
      }
    }

    if (wordBits != 0) {
      word <<= checksumSizeBits - wordBits;
      sum = FoldOnesComplement(sum + word, checksumSizeBits, mask);
    }

    return ToFixedBigEndian(mask ^ sum, outputBytes);
  }

  private static BigInteger FoldOnesComplement(BigInteger value, int checksumSizeBits, BigInteger mask) {
    while (value > mask)
      value = (value & mask) + (value >> checksumSizeBits);
    return value;
  }

  private static int ComputeSubByteParity(ReadOnlySpan<byte> data, int checksumSizeBits) {
    var result = 0;
    var word = 0;
    var wordBits = 0;

    foreach (var value in data) {
      for (var bit = 7; bit >= 0; --bit) {
        word = (word << 1) | ((value >> bit) & 1);
        ++wordBits;
        if (wordBits != checksumSizeBits)
          continue;

        result ^= word;
        word = 0;
        wordBits = 0;
      }
    }

    if (wordBits != 0)
      result ^= word << (checksumSizeBits - wordBits);

    return result;
  }

  private static BigInteger CreateMask(int checksumSizeBits) =>
    (BigInteger.One << checksumSizeBits) - BigInteger.One;

  private static int ValidateSize(int checksumSizeBits, bool requireEvenWidth = false) {
    var isPowerOfTwo = checksumSizeBits > 0 && (checksumSizeBits & (checksumSizeBits - 1)) == 0;
    var isWholeByte = checksumSizeBits >= 8 && (checksumSizeBits & 7) == 0;
    if ((!isPowerOfTwo && !isWholeByte) || (requireEvenWidth && (checksumSizeBits & 1) != 0))
      throw new ArgumentOutOfRangeException(
        nameof(checksumSizeBits),
        requireEvenWidth
          ? "Checksum width must be an even power of two or a positive multiple of 8 bits."
          : "Checksum width must be a power of two or a positive multiple of 8 bits."
      );

    return ((checksumSizeBits - 1) / 8) + 1;
  }

  private static byte[] ToFixedBigEndian(BigInteger value, int outputBytes) {
    var result = new byte[outputBytes];
    var byteCount = value.GetByteCount(isUnsigned: true);
    if (byteCount > outputBytes)
      throw new InvalidOperationException("Checksum value exceeds the requested output width.");

    if (!value.TryWriteBytes(result.AsSpan(outputBytes - byteCount), out var bytesWritten, isUnsigned: true, isBigEndian: true) || bytesWritten != byteCount)
      throw new InvalidOperationException("Unable to serialize checksum value.");

    return result;
  }

  private static BigInteger FindAdlerModulus(int halfBits) {
    if (halfBits == 1)
      return 2;

    var candidate = (BigInteger.One << halfBits) - BigInteger.One;
    while (!IsPrime(candidate))
      candidate -= 2;
    return candidate;
  }

  private static bool IsPrime(BigInteger value) {
    if (value < 2)
      return false;

    foreach (var prime in AdlerPrimeWitnesses) {
      if (value == prime)
        return true;
      if (value % prime == 0)
        return false;
    }

    var d = value - BigInteger.One;
    var s = 0;
    while (d.IsEven) {
      d >>= 1;
      ++s;
    }

    foreach (var witness in AdlerPrimeWitnesses) {
      var a = new BigInteger(witness);
      if (a >= value)
        continue;

      var x = BigInteger.ModPow(a, d, value);
      if (x.IsOne || x == value - BigInteger.One)
        continue;

      var passed = false;
      for (var round = 1; round < s; ++round) {
        x = BigInteger.Remainder(x * x, value);
        if (x != value - BigInteger.One)
          continue;
        passed = true;
        break;
      }

      if (!passed)
        return false;
    }

    return true;
  }
}
