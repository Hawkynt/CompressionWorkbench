namespace FileFormat.Rzip;

/// <summary>
/// Constants of the rzip 2.x file layout (<c>docs/RZIP-ON-DISK.md</c>) plus defaults for the
/// encoder-only match-finder settings.
/// </summary>
internal static class RzipConstants {

  /// <summary>Magic bytes: "RZIP".</summary>
  public static ReadOnlySpan<byte> Magic => "RZIP"u8;

  /// <summary>Major version number.</summary>
  public const byte VersionMajor = 2;

  /// <summary>Minor version number.</summary>
  public const byte VersionMinor = 1;

  /// <summary>File header size: magic, version, 64-bit big-endian size, zero padding.</summary>
  public const int HeaderSize = 24;

  /// <summary>Input bytes per chunk: rzip's default level 6 times 100 MiB.</summary>
  public const int ChunkSize = 6 * 100 * 1024 * 1024;

  /// <summary>Streams multiplexed in a chunk: control tokens, then literal bytes.</summary>
  public const int StreamCount = 2;

  /// <summary>Size of a stream block header: type, compressed length, uncompressed length, next.</summary>
  public const int BlockHeaderSize = 13;

  /// <summary>Block type: data stored.</summary>
  public const byte BlockStored = 3;

  /// <summary>Block type: data is one bzip2 stream.</summary>
  public const byte BlockBzip2 = 4;

  /// <summary>bzip2 level rzip uses at its default level 6.</summary>
  public const int Bzip2Level = 9;

  /// <summary>Bytes buffered per stream before a block is written: 100 KiB per bzip2 level.</summary>
  public const int StreamBufferSize = Bzip2Level * 100 * 1024;

  /// <summary>Longest literal run or match one token can carry.</summary>
  public const int MaxTokenLength = 0xFFFF;

  /// <summary>Token type of a literal run (and, with length 0, of the end of a chunk).</summary>
  public const byte TagLiteral = 0;

  /// <summary>Token type of a match.</summary>
  public const byte TagMatch = 1;

  /// <summary>
  /// Default rolling-signature width and shortest run accepted as a match.
  /// This is an encoder setting, not a field in the stream.
  /// </summary>
  public const int MinMatch = 32;

  /// <summary>Multiplier of the polynomial rolling hash used by the encoder.</summary>
  public const uint RollingHashBase = 257;

  /// <summary>Minimum number of candidate positions retained per hash value.</summary>
  public const int HashBucketCapacity = 64;

  /// <summary>Default maximum number of candidate positions examined per input position.</summary>
  public const int CandidateSearchLimit = 32;
}
