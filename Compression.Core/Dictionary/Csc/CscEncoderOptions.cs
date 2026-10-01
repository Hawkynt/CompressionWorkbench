namespace Compression.Core.Dictionary.Csc;

/// <summary>
/// Tuning knobs of the libcsc encoder, matching the reference <c>csc</c> tool's switches.
/// </summary>
/// <param name="Level">Effort 1 (fastest) to 5 (strongest); the tool's <c>-m</c>. libcsc's default is 2.</param>
/// <param name="DictionarySize">
/// Requested window in bytes; the tool's <c>-d</c>. Like the tool, the encoder shrinks it to the input
/// length when that is known and adds the 10 KiB libcsc reserves, then clamps to 32 KiB..1 GiB.
/// </param>
/// <param name="DeltaFilter">Lets the analyzer route data tables through the delta coder (<c>-fdelta0</c> turns it off).</param>
/// <param name="TextFilter">Lets the analyzer apply the English word substitution (<c>-ftxt0</c> turns it off).</param>
/// <param name="ExecutableFilter">Lets the analyzer apply the E8/E9 call transform (<c>-fexe0</c> turns it off).</param>
public sealed record CscEncoderOptions(
  int Level = CscEncoderOptions.DefaultLevel,
  long DictionarySize = CscEncoderOptions.DefaultDictionarySize,
  bool DeltaFilter = true,
  bool TextFilter = true,
  bool ExecutableFilter = true) {
  /// <summary>The reference tool's default level.</summary>
  public const int DefaultLevel = 2;

  /// <summary>The reference tool's default requested window (64,000,000 bytes).</summary>
  public const long DefaultDictionarySize = 64_000_000;

  /// <summary>Smallest requested window the reference tool accepts.</summary>
  public const long MinDictionarySize = CscConstants.MinDictionarySize;

  /// <summary>Largest requested window the reference tool accepts (exclusive bound 1 GiB).</summary>
  public const long MaxDictionarySize = CscConstants.MaxDictionarySize - 1;

  /// <summary>The defaults of the reference tool.</summary>
  public static CscEncoderOptions Default { get; } = new();
}
