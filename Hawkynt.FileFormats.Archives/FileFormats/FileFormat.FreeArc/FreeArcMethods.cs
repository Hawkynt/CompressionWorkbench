using System.Globalization;
using Compression.Core.Dictionary.Lzma;

namespace FileFormat.FreeArc;

/// <summary>
/// FreeArc compression method strings: <c>name:param:param...</c> stages joined by <c>+</c>, applied
/// left to right when compressing and therefore undone right to left when extracting.
/// </summary>
/// <remarks>
/// Only the stages our own codecs reproduce exactly are decoded: <c>storing</c> and <c>lzma</c>
/// (a raw LZMA1 stream with no properties header; dictionary and lc/lp/pb come from the method
/// string). Everything else — tornado, lzp, ppmd, grzip, rep, dict, delta, exe, mm, external
/// compressors and encryption — is refused with <see cref="NotSupportedException"/>.
/// </remarks>
public static class FreeArcMethods {
  /// <summary>The method name of an uncompressed stage.</summary>
  public const string Storing = "storing";

  private static readonly string[] EncryptionNames = ["aes", "blowfish", "serpent", "twofish"];

  /// <summary>Splits a method string into its stages.</summary>
  public static string[] Stages(string compressor) => compressor.Split('+');

  /// <summary>Gets whether every stage of <paramref name="compressor"/> can be decoded.</summary>
  public static bool IsSupported(string compressor) =>
    Stages(compressor).All(stage => StageName(stage) is Storing or "lzma");

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
      if (StageName(stage) is not (Storing or "lzma"))
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
    if (StageName(stage) == Storing)
      return input;

    var p = LzmaParameters.Parse(stage, outputSize);
    using var source = new MemoryStream(input, writable: false);
    var dictionary = (int)Math.Clamp(Math.Max(p.Dictionary, outputSize), 4096, int.MaxValue);
    return new LzmaDecoder(source, p.Lc, p.Lp, p.Pb, dictionary, outputSize).Decode();
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
