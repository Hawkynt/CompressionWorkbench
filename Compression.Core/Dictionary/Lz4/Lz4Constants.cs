namespace Compression.Core.Dictionary.Lz4;

/// <summary>
/// Constants for the LZ4 block compression format.
/// </summary>
public static class Lz4Constants {
  /// <summary>Minimum match length.</summary>
  public const int MinMatch = 4;

  /// <summary>Size of the hash table (64KB, keyed on 4-byte sequences).</summary>
  public const int HashTableBits = 16;

  /// <summary>Hash table size.</summary>
  public const int HashTableSize = 1 << Lz4Constants.HashTableBits;

  /// <summary>Maximum distance for a match (64KB - 1).</summary>
  public const int MaxDistance = 65535;

  /// <summary>Number of bytes always kept as literals at end of input.</summary>
  public const int LastLiterals = 5;

  /// <summary>Minimum input length that supports match finding.</summary>
  public const int MfLimit = Lz4Constants.MinMatch + Lz4Constants.LastLiterals;

  /// <summary>Token high nibble limit before overflow encoding.</summary>
  public const int RunMask = 15;

  /// <summary>LZ4 frame magic number.</summary>
  public const uint FrameMagic = 0x184D2204;

  /// <summary>LZ4 legacy frame magic number.</summary>
  public const uint LegacyMagic = 0x184C2102;

  /// <summary>Maximum block size for the default (4MB) setting.</summary>
  public const int MaxBlockSize = 4 * 1024 * 1024;
}
