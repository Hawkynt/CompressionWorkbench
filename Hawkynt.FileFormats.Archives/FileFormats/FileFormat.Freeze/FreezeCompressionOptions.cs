namespace FileFormat.Freeze;

/// <summary>Controls the interoperable Freeze encoder's compatibility target, match search and position coding.</summary>
public sealed record FreezeCompressionOptions {
  /// <summary>Wire-format generation to emit. The default remains Freeze 2.x.</summary>
  public FreezeCompatibility TargetCompatibility { get; init; } = FreezeCompatibility.Freeze2x;

  /// <summary>Match-selection strategy. Historical Freeze uses delayed parsing unless <c>-g</c> is requested.</summary>
  public FreezeParsingStrategy Parsing { get; init; } = FreezeParsingStrategy.Lazy;

  /// <summary>Maximum number of hash-chain candidates examined per input position.</summary>
  public int SearchDepth { get; init; } = DefaultSearchDepth;

  /// <summary>How the per-stream static position Huffman table is chosen.</summary>
  public FreezePositionTableMode PositionTable { get; init; } = FreezePositionTableMode.Default;

  /// <summary>Default match-search depth used by the managed writer.</summary>
  public const int DefaultSearchDepth = 128;
}

/// <summary>Wire-format generation targeted by the encoder.</summary>
public enum FreezeCompatibility {
  /// <summary>Freeze 2.x: 1F 9F magic, 8 KiB ring, 256-byte matches and per-stream position table header.</summary>
  Freeze2x,

  /// <summary>Freeze 1.x: 1F 9E magic, 4 KiB ring, 60-byte matches and fixed historical position table.</summary>
  Freeze1x,
}

/// <summary>Strategy used to choose between an immediately available match and one starting a byte later.</summary>
public enum FreezeParsingStrategy {
  /// <summary>Use the historical delayed parser: prefer an equally long or longer match one byte later.</summary>
  Lazy,

  /// <summary>Emit the longest match available at the current position, corresponding to historical <c>freeze -g</c>.</summary>
  Greedy,
}

/// <summary>Controls the static Huffman table used for the upper bits of match positions.</summary>
public enum FreezePositionTableMode {
  /// <summary>Use the compatibility target's historical position table.</summary>
  Default,

  /// <summary>Derive a valid per-input table. Available only to Freeze 2.x, whose stream header carries the table.</summary>
  Optimized,
}
