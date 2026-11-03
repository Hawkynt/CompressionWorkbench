using System.Globalization;
using Compression.Core.Dictionary.Lzma;

namespace FileFormat.FreeArc;

/// <summary>
/// FreeArc compression method strings: <c>name:param:param...</c> stages joined by <c>+</c>, applied
/// left to right when compressing and therefore undone right to left when extracting.
/// </summary>
/// <remarks>
/// Only the stages our own codecs reproduce exactly are decoded: <c>storing</c>, <c>lzma</c>
/// (a raw LZMA1 stream with no properties header; dictionary and lc/lp/pb come from the method
/// string), <c>tor</c> (see <see cref="FreeArcTornado"/>), <c>ppmd</c> (see
/// <see cref="FreeArcPpmd"/>), <c>grzip</c> (see <see cref="FreeArcGrzip"/>), the wrapper
/// <c>4x4</c> and the preprocessors <c>rep</c>, <c>delta</c>, <c>exe</c>, <c>lzp</c> and <c>dict</c>
/// (see <see cref="FreeArcFilters"/>). Everything else — mm, external compressors and encryption —
/// is refused with <see cref="NotSupportedException"/>.
/// </remarks>
public static partial class FreeArcMethods {
  /// <summary>The method name of an uncompressed stage.</summary>
  public const string Storing = "storing";

  private static readonly string[] EncryptionNames = ["aes", "blowfish", "serpent", "twofish"];

  /// <summary>Splits a method string into its stages.</summary>
  public static string[] Stages(string compressor) => compressor.Split('+');

  /// <summary>Gets whether every stage of <paramref name="compressor"/> can be decoded.</summary>
  public static bool IsSupported(string compressor) =>
    Stages(compressor).All(IsDecodable);

  private static bool IsDecodable(string stage) => StageName(stage) switch {
    Storing or "lzma" or "rep" or "delta" or "exe" or "tor" => true,
    "ppmd" => PpmdParameters.TryParse(stage, out _),
    "grzip" => stage.Split(':').Skip(1).All(t => GrzipOption().IsMatch(t.Trim())),
    "lzp" => LzpParameters.TryParse(stage, out _),
    "dict" => TryDictBlockSize(stage, out _),
    "4x4" => FourByFourInner(stage) is { } inner && StageName(inner) != "4x4" && IsDecodable(inner),
    _ => false,
  };

  /// <summary>
  /// The method a <c>4x4</c> stage applies to each chunk: everything after its own options
  /// (<c>bN</c>, <c>tN</c>, <c>iN</c>, <c>rN</c> and bare numbers), or null when none is named.
  /// </summary>
  private static string? FourByFourInner(string stage) {
    var tokens = stage.Split(':');
    for (var i = 1; i < tokens.Length; ++i) {
      var t = tokens[i];
      if (t.Length > 0 && !char.IsAsciiDigit(t[0]) && !(t.Length > 1 && char.IsAsciiDigit(t[1])))
        return string.Join(':', tokens[i..]);
      if (!FourByFourOption().IsMatch(t))
        return null; // neither an option nor a method: FreeArc rejects the whole stage
    }
    return null;
  }

  /// <summary>A 4x4 option: block size <c>b</c>, threads <c>t</c>, extra buffers <c>i</c>, ratio <c>r</c>, or a bare number or size.</summary>
  [System.Text.RegularExpressions.GeneratedRegex(@"^[btir]?\d+(\.\d+)?([kmg]b?|b)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
  private static partial System.Text.RegularExpressions.Regex FourByFourOption();

  /// <summary>
  /// A grzip option: the switches <c>s</c>, <c>a</c>, <c>l</c>, <c>d</c>, <c>p</c>, a method
  /// <c>mN</c>, block size <c>bSIZE</c>, LZP length <c>lN</c>, hash bits <c>hN</c>, threads
  /// <c>tN</c>, buffers <c>iN</c>, or a bare number (LZP length) or size (block size). They steer
  /// the compressor only; every block header says how it was coded.
  /// </summary>
  [System.Text.RegularExpressions.GeneratedRegex(@"^([saldp]|[mlhti]\d+|b?\d+(\.\d+)?([kmg]b?|b)?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
  private static partial System.Text.RegularExpressions.Regex GrzipOption();

  /// <summary>Gets whether <paramref name="compressor"/> contains an encryption stage.</summary>
  public static bool IsEncrypted(string compressor) =>
    Stages(compressor).Any(stage => EncryptionNames.Any(e => StageName(stage).StartsWith(e, StringComparison.OrdinalIgnoreCase)));

  /// <summary>Decodes <paramref name="input"/> produced by <paramref name="compressor"/> into exactly <paramref name="outputSize"/> bytes.</summary>
  /// <exception cref="NotSupportedException">A stage is encrypted or uses a codec this library does not implement.</exception>
  /// <exception cref="InvalidDataException">The stream does not decode to the expected size.</exception>
  public static byte[] Decode(string compressor, byte[] input, long outputSize) {
    if (IsEncrypted(compressor))
      throw new NotSupportedException($"FreeArc: encrypted data (method \"{compressor}\") is not supported.");

    var stages = Stages(compressor);
    foreach (var stage in stages)
      if (!IsDecodable(stage))
        throw new NotSupportedException($"FreeArc: compression method \"{StageName(stage)}\" (in \"{compressor}\") is not supported.");

    var data = input;
    for (var i = stages.Length - 1; i >= 0; --i)
      data = DecodeStage(stages[i], data, i == 0 ? outputSize : -1);

    if (data.LongLength != outputSize)
      throw new InvalidDataException($"FreeArc: \"{compressor}\" decoded to {data.LongLength} bytes, expected {outputSize}.");
    return data;
  }

  /// <summary>Encodes <paramref name="input"/> with a single supported stage.</summary>
  public static byte[] Encode(string compressor, ReadOnlySpan<byte> input) {
    var stages = Stages(compressor);
    if (stages.Length != 1)
      throw new NotSupportedException($"FreeArc: writing chained method \"{compressor}\" is not supported.");

    switch (StageName(stages[0])) {
      case Storing:
        return input.ToArray();
      case "lzma": {
        var p = LzmaParameters.Parse(stages[0], input.Length);
        var encoder = new LzmaEncoder((int)Math.Clamp(p.Dictionary, 4096, 1L << 30), p.Lc, p.Lp, p.Pb);
        using var output = new MemoryStream();
        encoder.Encode(output, input, writeEndMarker: true);
        return output.ToArray();
      }
      default:
        throw new NotSupportedException($"FreeArc: writing method \"{compressor}\" is not supported.");
    }
  }

  /// <summary>
  /// Gives a bare <c>lzma</c> stage an explicit dictionary (the next power of two covering the
  /// input, 64 KiB to 64 MiB) so FreeArc's decoder does not reserve its much larger default.
  /// </summary>
  public static string WithDictionary(string compressor, long inputLength) {
    if (StageName(compressor) != "lzma" || compressor.Split(':').Skip(1).Any(t => LzmaParameters.IsSize(t.Trim().ToLowerInvariant())))
      return compressor;
    var dictionary = 1L << 16;
    while (dictionary < inputLength && dictionary < 1L << 26)
      dictionary <<= 1;
    var size = dictionary >= 1L << 20 ? $"{dictionary >> 20}mb" : $"{dictionary >> 10}kb";
    var colon = compressor.IndexOf(':');
    return colon < 0 ? $"lzma:{size}" : $"lzma:{size}{compressor[colon..]}";
  }

  private static string StageName(string stage) {
    var colon = stage.IndexOf(':');
    return (colon < 0 ? stage : stage[..colon]).Trim().ToLowerInvariant();
  }

  private static byte[] DecodeStage(string stage, byte[] input, long outputSize) {
    switch (StageName(stage)) {
      case Storing:
        return input;
      case "rep":
        return FreeArcFilters.DecodeRep(input, outputSize);
      case "delta":
        return FreeArcFilters.DecodeDelta(input);
      case "exe":
        return FreeArcFilters.DecodeExe(input);
      case "tor":
        return FreeArcTornado.Decode(input, outputSize);
      case "ppmd": {
        if (!PpmdParameters.TryParse(stage, out var pp))
          throw new NotSupportedException($"FreeArc: ppmd parameters \"{stage}\" are not supported.");
        return FreeArcPpmd.Decode(input, pp.Order, pp.Memory, pp.Restore, outputSize);
      }
      case "grzip":
        return FreeArcGrzip.Decode(input, outputSize);
      case "dict":
        if (!TryDictBlockSize(stage, out var dictBlock))
          throw new NotSupportedException($"FreeArc: dict parameters \"{stage}\" are not supported.");
        return FreeArcFilters.DecodeDict(input, dictBlock);
      case "lzp": {
        if (!LzpParameters.TryParse(stage, out var lp))
          throw new NotSupportedException($"FreeArc: lzp parameters \"{stage}\" are not supported.");
        return FreeArcFilters.DecodeLzp(input, lp.BlockSize, lp.MinLength, lp.HashBits, lp.Barrier, lp.SmallestLength);
      }
      case "4x4":
        return FreeArcFilters.DecodeFourByFour(input, FourByFourInner(stage)!, DecodeStage);
    }

    var p = LzmaParameters.Parse(stage, outputSize);
    using var source = new MemoryStream(input, writable: false);
    var dictionary = (int)Math.Clamp(Math.Max(p.Dictionary, outputSize), 4096, int.MaxValue);
    return new LzmaDecoder(source, p.Lc, p.Lp, p.Pb, dictionary, outputSize).Decode();
  }

  /// <summary>
  /// ppmd stage parameters, as FreeArc reads them: a bare number is the order, a bare size the
  /// memory, <c>oN</c>/<c>mSIZE</c>/<c>memSIZE</c> name them, <c>r</c> or <c>rN</c> the restore
  /// method (default 0, restart). Chunked streams (<c>cSIZE</c>) are not supported.
  /// </summary>
  private readonly record struct PpmdParameters(int Order, int Memory, int Restore) {
    public static bool TryParse(string stage, out PpmdParameters p) {
      int order = 10, restore = 0;
      long memory = 48L << 20;
      p = default;
      foreach (var raw in stage.Split(':').Skip(1)) {
        var token = raw.Trim().ToLowerInvariant();
        if (token.StartsWith("mem", StringComparison.Ordinal))
          token = token[2..];
        if (token == "r") {
          restore = 1;
          continue;
        }
        if (token.Length > 1 && token[0] is 'm' or 'o' or 'r' or 'c') {
          var value = token[1..];
          switch (token[0]) {
            case 'm' when LzmaParameters.IsSize(value) || int.TryParse(value, out _):
              memory = LzmaParameters.SizeOf(value);
              continue;
            case 'o' when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var o):
              order = o;
              continue;
            case 'r' when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var r):
              restore = r;
              continue;
            case 'c':
              return false;
          }
        }
        if (int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
          order = n;
        else if (LzmaParameters.IsSize(token))
          memory = LzmaParameters.SizeOf(token);
        else
          return false;
      }
      if (order is < 2 or > 128 || memory is < 2048 or > int.MaxValue - 4096 || restore is < 0 or > 2)
        return false;
      p = new(order, (int)memory, restore);
      return true;
    }
  }

  /// <summary>
  /// The block size of a dict stage (default 64 MiB): <c>bSIZE</c> or a bare size. <c>p</c>,
  /// <c>f</c>, <c>cN</c>, <c>lN</c>, <c>mN</c>, <c>sN</c>, <c>rN</c>, <c>N%</c> and a bare number
  /// steer only the encoder.
  /// </summary>
  private static bool TryDictBlockSize(string stage, out int blockSize) {
    long size = 64L << 20;
    blockSize = 0;
    foreach (var raw in stage.Split(':').Skip(1)) {
      var token = raw.Trim().ToLowerInvariant();
      if (token is "p" or "f")
        continue;
      if (token.Length > 1 && token[0] is 'c' or 'l' or 'm' or 's' or 'r') {
        if (!int.TryParse(token.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out _))
          return false;
        continue;
      }
      if (token.Length > 1 && token[0] == 'b') {
        if (!LzmaParameters.IsSize(token[1..]) && !int.TryParse(token.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out _))
          return false;
        size = LzmaParameters.SizeOf(token[1..]);
        continue;
      }
      if (token.EndsWith('%') && int.TryParse(token.AsSpan(0, token.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out _))
        continue;
      if (int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        continue;
      if (!LzmaParameters.IsSize(token))
        return false;
      size = LzmaParameters.SizeOf(token);
    }
    if (size is < 1 or > int.MaxValue)
      return false;
    blockSize = (int)size;
    return true;
  }

  /// <summary>
  /// lzp stage parameters, as FreeArc reads them: <c>bSIZE</c> or a bare size the block size,
  /// <c>lN</c> or a bare number the minimum match length, <c>hN</c> the hash bits, <c>dSIZE</c> the
  /// barrier and <c>sN</c> the minimum length beyond it; <c>N%</c> steers only the encoder.
  /// </summary>
  private readonly record struct LzpParameters(int BlockSize, int MinLength, int HashBits, long Barrier, int SmallestLength) {
    public static bool TryParse(string stage, out LzpParameters p) {
      long blockSize = 8L << 20, barrier = int.MaxValue;
      int minLength = 64, hashBits = 18, smallest = 32;
      p = default;
      foreach (var raw in stage.Split(':').Skip(1)) {
        var token = raw.Trim().ToLowerInvariant();
        if (token.Length == 0)
          return false;
        var value = token[1..];
        bool ok;
        switch (token[0]) {
          case 'b': ok = TryMemory(value, out blockSize); break;
          case 'l': ok = TryInt(value, out minLength); break;
          case 'h': ok = TryInt(value, out hashBits); break;
          case 'd': ok = TryMemory(value, out barrier); break;
          case 's': ok = TryInt(value, out smallest); break;
          default:
            if (token.EndsWith('%'))
              ok = TryInt(token[..^1], out _); // minimum compression ratio: encoder only
            else if (TryInt(token, out var n)) {
              minLength = n;
              ok = true;
            } else
              ok = TryMemory(token, out blockSize);
            break;
        }
        if (!ok)
          return false;
      }
      if (blockSize is < 1 or > int.MaxValue || hashBits is < 1 or > 26 || minLength is < 1 or > 1 << 20 || smallest is < 1 or > 1 << 20 || barrier < 0)
        return false;
      p = new((int)blockSize, minLength, hashBits, barrier, smallest);
      return true;
    }

    private static bool TryInt(string token, out int value)
      => int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static bool TryMemory(string token, out long value) {
      value = 0;
      if (LzmaParameters.IsSize(token) || TryInt(token, out _)) {
        value = LzmaParameters.SizeOf(token);
        return true;
      }
      return false;
    }
  }

  /// <summary>lzma stage parameters: tokens with a size suffix are the dictionary, <c>lcN</c>/<c>lpN</c>/<c>pbN</c> the literal and position bits; mode, match finder, fast bytes and <c>mcN</c> only steer the encoder.</summary>
  private readonly record struct LzmaParameters(long Dictionary, int Lc, int Lp, int Pb) {
    public static LzmaParameters Parse(string stage, long fallbackDictionary) {
      long dictionary = -1;
      int lc = 3, lp = 0, pb = 2;
      foreach (var raw in stage.Split(':').Skip(1)) {
        var token = raw.Trim().ToLowerInvariant();
        if (TryBits(token, "lc", 8, ref lc) || TryBits(token, "lp", 4, ref lp) || TryBits(token, "pb", 4, ref pb))
          continue;
        if (TrySize(token, out var size))
          dictionary = size;
      }

      return new(dictionary > 0 ? dictionary : Math.Max(fallbackDictionary, 1 << 16), lc, lp, pb);
    }

    private static bool TryBits(string token, string prefix, int max, ref int value) {
      if (!token.StartsWith(prefix, StringComparison.Ordinal)
          || !int.TryParse(token.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        return false;
      if (parsed > max)
        throw new InvalidDataException($"FreeArc: lzma parameter \"{token}\" out of range.");
      value = parsed;
      return true;
    }

    public static bool IsSize(string token) => TrySize(token, out _);

    /// <summary>A size token in bytes; a bare number counts in megabytes, as FreeArc's parseMem does.</summary>
    public static long SizeOf(string token)
      => TrySize(token, out var size) ? size : long.Parse(token, NumberStyles.None, CultureInfo.InvariantCulture) << 20;

    private static bool TrySize(string token, out long size) {
      size = 0;
      var (digits, multiplier) = token switch {
        _ when token.EndsWith("gb", StringComparison.Ordinal) => (token[..^2], 1L << 30),
        _ when token.EndsWith("mb", StringComparison.Ordinal) => (token[..^2], 1L << 20),
        _ when token.EndsWith("kb", StringComparison.Ordinal) => (token[..^2], 1L << 10),
        _ when token.EndsWith('g') => (token[..^1], 1L << 30),
        _ when token.EndsWith('m') => (token[..^1], 1L << 20),
        _ when token.EndsWith('k') => (token[..^1], 1L << 10),
        _ when token.EndsWith('b') => (token[..^1], 1L),
        _ => ("", 0L),
      };
      if (multiplier == 0 || !long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        return false;
      size = value * multiplier;
      return true;
    }
  }
}
