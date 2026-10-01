namespace Compression.Core.Dictionary.Csc;

/// <summary>
/// Reads and writes the stream format of Fu Siyuan's libcsc (https://github.com/fusiyuan2010/CSC),
/// byte-compatible with the reference <c>csc</c> tool: a 10-byte property header followed by
/// tagged range-coder and bit-coder blocks.
/// </summary>
/// <remarks>
/// libcsc is LZMA-inspired: LZ77 over a window of up to 1 GiB with hash and binary-tree match
/// finders, a lazy or optimal parser, an adaptive binary range coder, and a per-8-KiB analyzer that
/// routes text, x86 code, data tables and incompressible data through dedicated filters or coders.
/// The stream carries no uncompressed length; an end-of-stream block terminates it.
/// </remarks>
public static class CscCodec {
  /// <summary>Compresses <paramref name="input"/> (from its current position to the end) to <paramref name="output"/>.</summary>
  /// <param name="input">The data to compress.</param>
  /// <param name="output">Receives the libcsc stream, property header included.</param>
  /// <param name="options">Encoder tuning; <see langword="null"/> uses the reference tool's defaults.</param>
  public static void Compress(Stream input, Stream output, CscEncoderOptions? options = null) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    options = Validate(options);

    // The reference tool sizes the window to the file; learn the length the same way when possible.
    long? length = null;
    byte[] prefix = [];
    if (input.CanSeek)
      length = Math.Max(0, input.Length - input.Position);
    else {
      prefix = new byte[CscConstants.DefaultRawBlockSize];
      var read = input.ReadAtLeast(prefix, prefix.Length, throwOnEndOfStream: false);
      if (read < prefix.Length) {
        length = read;
        prefix = prefix[..read];
      }
    }

    var settings = CscEncoderSettings.Create(options, length);
    Span<byte> header = stackalloc byte[CscStreamProperties.Size];
    settings.Properties.Write(header);
    output.Write(header);
    new CscEncoder(output, settings).Encode(input, prefix);
  }

  /// <summary>Compresses <paramref name="data"/> into a new libcsc stream.</summary>
  /// <param name="data">The data to compress.</param>
  /// <param name="options">Encoder tuning; <see langword="null"/> uses the reference tool's defaults.</param>
  /// <returns>The libcsc stream, property header included.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> data, CscEncoderOptions? options = null) {
    using var input = new MemoryStream(data.ToArray(), writable: false);
    using var output = new MemoryStream();
    Compress(input, output, options);
    return output.ToArray();
  }

  /// <summary>Decompresses a libcsc stream (property header included) from <paramref name="input"/> to <paramref name="output"/>.</summary>
  /// <param name="input">The libcsc stream.</param>
  /// <param name="output">Receives the original data.</param>
  /// <exception cref="InvalidDataException">The stream is truncated or corrupt.</exception>
  public static void Decompress(Stream input, Stream output) => Decompress(input, output, null);

  /// <summary>Decompresses and reports every block as (block type, decoded length) to <paramref name="blockDecoded"/>.</summary>
  internal static void Decompress(Stream input, Stream output, Action<uint, int>? blockDecoded) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    Span<byte> header = stackalloc byte[CscStreamProperties.Size];
    if (input.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) != header.Length)
      throw new InvalidDataException("CSC stream is shorter than its 10-byte property header.");

    new CscDecoder(input, CscStreamProperties.Read(header)) { BlockDecoded = blockDecoded }.DecodeTo(output);
  }

  /// <summary>Decompresses a complete libcsc stream.</summary>
  /// <param name="data">The libcsc stream, property header included.</param>
  /// <returns>The original data.</returns>
  /// <exception cref="InvalidDataException">The stream is truncated or corrupt.</exception>
  public static byte[] Decompress(ReadOnlySpan<byte> data) {
    using var input = new MemoryStream(data.ToArray(), writable: false);
    using var output = new MemoryStream();
    Decompress(input, output);
    return output.ToArray();
  }

  private static CscEncoderOptions Validate(CscEncoderOptions? options) {
    options ??= CscEncoderOptions.Default;
    ArgumentOutOfRangeException.ThrowIfLessThan(options.Level, 1, nameof(options));
    ArgumentOutOfRangeException.ThrowIfGreaterThan(options.Level, 5, nameof(options));
    ArgumentOutOfRangeException.ThrowIfLessThan(options.DictionarySize, CscEncoderOptions.MinDictionarySize, nameof(options));
    ArgumentOutOfRangeException.ThrowIfGreaterThan(options.DictionarySize, CscEncoderOptions.MaxDictionarySize, nameof(options));
    return options;
  }
}
