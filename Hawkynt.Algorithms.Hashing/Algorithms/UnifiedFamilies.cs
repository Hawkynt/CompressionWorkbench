namespace Hawkynt.Algorithms.Hashing;

/// <summary>RIPEMD family facade over the shared RIPEMD core.</summary>
public static class Ripemd {
  public static IReadOnlyList<HashSizeRange> SupportedHashSizes { get; } = [
    new(128, 160, 32),
    new(256, 320, 64)
  ];

  public static byte[] Compute(ReadOnlySpan<byte> data, int hashSizeBits = 160) {
    if (!SupportedHashSizes.Supports(hashSizeBits))
      throw new ArgumentOutOfRangeException(nameof(hashSizeBits));
    return RipemdCore.Compute(data, hashSizeBits);
  }
}

/// <summary>ECHO SHA-3 candidate family facade over the shared ECHO core.</summary>
public static class Echo {
  public static IReadOnlyList<HashSizeRange> SupportedHashSizes { get; } = [
    new(224, 256, 32),
    new(384, 512, 128)
  ];

  public static byte[] Compute(ReadOnlySpan<byte> data, int hashSizeBits = 256) {
    if (!SupportedHashSizes.Supports(hashSizeBits))
      throw new ArgumentOutOfRangeException(nameof(hashSizeBits));
    return EchoCore.Compute(data, hashSizeBits / 8);
  }
}

/// <summary>Luffa SHA-3 candidate family facade over the shared Luffa core.</summary>
public static class Luffa {
  public static IReadOnlyList<HashSizeRange> SupportedHashSizes { get; } = [
    new(224, 256, 32),
    new(384, 512, 128)
  ];

  public static byte[] Compute(ReadOnlySpan<byte> data, int hashSizeBits = 256) {
    if (!SupportedHashSizes.Supports(hashSizeBits))
      throw new ArgumentOutOfRangeException(nameof(hashSizeBits));
    return hashSizeBits switch {
      224 => LuffaCore.Compute(data, 28, 3),
      256 => LuffaCore.Compute(data, 32, 3),
      384 => LuffaCore.Compute(data, 48, 4),
      512 => LuffaCore.Compute(data, 64, 5),
      _ => throw new ArgumentOutOfRangeException(nameof(hashSizeBits))
    };
  }
}

/// <summary>LSH-256 word-size family; output size selects the standard IV/truncation.</summary>
public static class Lsh256Family {
  public static IReadOnlyList<HashSizeRange> SupportedHashSizes { get; } = [new(224, 256, 32)];

  public static byte[] Compute(ReadOnlySpan<byte> data, int hashSizeBits = 256) => hashSizeBits switch {
    224 => Lsh256Core.Compute(data, Lsh256Core.Iv224, 28),
    256 => Lsh256Core.Compute(data, Lsh256Core.Iv256, 32),
    _ => throw new ArgumentOutOfRangeException(nameof(hashSizeBits))
  };
}

/// <summary>LSH-512 word-size family; output size selects the standard IV/truncation.</summary>
public static class Lsh512Family {
  public static IReadOnlyList<HashSizeRange> SupportedHashSizes { get; } = [new(256, 512, 128)];

  public static byte[] Compute(ReadOnlySpan<byte> data, int hashSizeBits = 512) => hashSizeBits switch {
    256 => Lsh512Core.Compute(data, Lsh512Core.Iv256, 32),
    384 => Lsh512Core.Compute(data, Lsh512Core.Iv384, 48),
    512 => Lsh512Core.Compute(data, Lsh512Core.Iv512, 64),
    _ => throw new ArgumentOutOfRangeException(nameof(hashSizeBits))
  };
}
