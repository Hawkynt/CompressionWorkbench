using System.Buffers.Binary;

namespace FileFormat.Lzg;

/// <summary>
/// Provides managed LZG compression and decompression compatible with liblzg's
/// 16-byte container header and LZG1 marker-coded data stream.
/// </summary>
public static class LzgStream {

  private const int HeaderSize = 16;
  private const int MaxRunLength = 128;

  private const byte MethodCopy = 0;
  private const byte MethodLzg1 = 1;

  private static ReadOnlySpan<byte> Magic => "LZG"u8;

  private static ReadOnlySpan<byte> LengthDecodeTable =>
    [2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17,
     18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 35, 48, 72, 128];

  private readonly record struct EncoderTuning(int WindowSize, int MaxMatches, int GoodLength);

  private readonly record struct Match(int Length, int Offset, int Savings);

  // The level progression follows liblzg's public level semantics: larger
  // levels widen the history window and spend progressively more work on
  // candidate matches. The values are intentionally managed-code tuning, not
  // a translation of liblzg's search accelerator.
  private static readonly EncoderTuning[] Tunings =
    [
      new(2 * 1024, 16, 35),
      new(4 * 1024, 24, 48),
      new(8 * 1024, 32, 48),
      new(16 * 1024, 48, 72),
      new(32 * 1024, 64, 72),
      new(64 * 1024, 96, 72),
      new(128 * 1024, 144, 128),
      new(256 * 1024, 256, 128),
      new(512 * 1024, 1024, 128),
    ];

  /// <summary>
  /// Compresses <paramref name="input"/> to <paramref name="output"/> with the
  /// liblzg-compatible default encoder settings (level 5, fast match lookup).
  /// </summary>
  public static void Compress(Stream input, Stream output)
    => Compress(input, output, level: 5, fast: true);

  /// <summary>
  /// Compresses <paramref name="input"/> to <paramref name="output"/>.
  /// </summary>
  /// <param name="input">The uncompressed source stream.</param>
  /// <param name="output">The destination stream for encoded LZG data.</param>
  /// <param name="level">Compression effort from 1 (fastest) to 9 (strongest).</param>
  /// <param name="fast">
  /// When <see langword="true"/>, use a three-byte match key; otherwise use
  /// the lower-memory two-byte key. Both produce the same interoperable wire format.
  /// </param>
  public static void Compress(Stream input, Stream output, int level, bool fast) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    level = Math.Clamp(level, 1, 9);

    using var inputBuffer = new MemoryStream();
    input.CopyTo(inputBuffer);
    var data = inputBuffer.ToArray();

    byte[] encoded = data.Length == 0
      ? []
      : CompressLzg1(data, Tunings[level - 1], fast);

    var useCopy = data.Length == 0 || encoded.Length > data.Length;
    var payload = useCopy ? data : encoded;
    var method = useCopy ? MethodCopy : MethodLzg1;

    Span<byte> header = stackalloc byte[HeaderSize];
    Magic.CopyTo(header);
    BinaryPrimitives.WriteUInt32BigEndian(header[3..], checked((uint)data.Length));
    BinaryPrimitives.WriteUInt32BigEndian(header[7..], checked((uint)payload.Length));
    BinaryPrimitives.WriteUInt32BigEndian(header[11..], CalculateChecksum(payload));
    header[15] = method;

    output.Write(header);
    output.Write(payload);
  }

  /// <summary>
  /// Decompresses one complete LZG stream from <paramref name="input"/> to
  /// <paramref name="output"/>.
  /// </summary>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    Span<byte> header = stackalloc byte[HeaderSize];
    if (input.ReadAtLeast(header, HeaderSize, throwOnEndOfStream: false) != HeaderSize)
      throw new InvalidDataException("Stream too short for LZG header.");

    if (!header[..3].SequenceEqual(Magic))
      throw new InvalidDataException("Invalid LZG magic.");

    var decodedSize = BinaryPrimitives.ReadUInt32BigEndian(header[3..7]);
    var encodedSize = BinaryPrimitives.ReadUInt32BigEndian(header[7..11]);
    var expectedChecksum = BinaryPrimitives.ReadUInt32BigEndian(header[11..15]);
    var method = header[15];

    if (decodedSize > int.MaxValue || encodedSize > int.MaxValue)
      throw new InvalidDataException("LZG stream is too large for the managed buffer implementation.");

    var payload = new byte[(int)encodedSize];
    if (payload.Length > 0
        && input.ReadAtLeast(payload, payload.Length, throwOnEndOfStream: false) != payload.Length)
      throw new InvalidDataException("Unexpected end of LZG stream.");

    if (input.ReadByte() >= 0)
      throw new InvalidDataException("LZG stream contains trailing bytes beyond its declared encoded size.");

    if (CalculateChecksum(payload) != expectedChecksum)
      throw new InvalidDataException("LZG checksum mismatch.");

    byte[] decoded;
    switch (method) {
      case MethodCopy:
        if (decodedSize != encodedSize)
          throw new InvalidDataException("LZG copy method requires equal encoded and decoded sizes.");
        decoded = payload;
        break;

      case MethodLzg1:
        decoded = DecompressLzg1(payload, (int)decodedSize);
        break;

      default:
        throw new InvalidDataException($"Unknown LZG method: {method}.");
    }

    output.Write(decoded);
  }

  private static byte[] CompressLzg1(byte[] data, EncoderTuning tuning, bool fast) {
    var markers = DetermineMarkers(data);
    var isMarker = new bool[256];
    foreach (var marker in markers)
      isMarker[marker] = true;

    var result = new List<byte>(data.Length) {
      markers[0],
      markers[1],
      markers[2],
      markers[3],
    };

    var previous = new int[data.Length];
    Array.Fill(previous, -1);
    var heads = new Dictionary<int, int>(Math.Min(data.Length, 1 << 16));

    var position = 0;
    while (position < data.Length) {
      var match = FindMatch(data, position, isMarker[data[position]] ? 2 : 1, tuning, fast, heads, previous);

      if (match.Length > 0) {
        EmitMatch(result, markers, match.Length, match.Offset);
        for (var i = 0; i < match.Length; ++i)
          InsertPosition(data, position + i, fast, heads, previous);
        position += match.Length;
        continue;
      }

      InsertPosition(data, position, fast, heads, previous);
      var symbol = data[position++];
      result.Add(symbol);
      if (isMarker[symbol])
        result.Add(0);
    }

    return result.ToArray();
  }

  private static Match FindMatch(
      byte[] data,
      int position,
      int symbolCost,
      EncoderTuning tuning,
      bool fast,
      Dictionary<int, int> heads,
      int[] previous) {

    if (position + 2 >= data.Length)
      return default;

    var key = MatchKey(data, position, fast);
    if (!heads.TryGetValue(key, out var candidate))
      return default;

    var minimumPosition = Math.Max(0, position - tuning.WindowSize);
    var maximumLength = Math.Min(MaxRunLength, data.Length - position);
    var remainingMatches = tuning.MaxMatches;
    var best = default(Match);

    while (candidate >= minimumPosition && remainingMatches-- > 0) {
      var length = CommonPrefixLength(data, candidate, position, maximumLength);
      length = QuantizeLength(length);

      if (length >= 3) {
        var offset = position - candidate;
        var tokenCost = TokenCost(length, offset);
        var savings = length + symbolCost - 1 - tokenCost;

        if (savings > 0
            && (savings > best.Savings
                || savings == best.Savings && length > best.Length
                || savings == best.Savings && length == best.Length && offset < best.Offset)) {
          best = new(length, offset, savings);
          if (length >= tuning.GoodLength)
            break;
        }
      }

      candidate = previous[candidate];
    }

    return best;
  }

  private static int CommonPrefixLength(byte[] data, int left, int right, int maximumLength) {
    var length = 0;
    while (length < maximumLength && data[left + length] == data[right + length])
      ++length;
    return length;
  }

  private static void InsertPosition(
      byte[] data,
      int position,
      bool fast,
      Dictionary<int, int> heads,
      int[] previous) {

    if (position + 2 >= data.Length)
      return;

    var key = MatchKey(data, position, fast);
    if (heads.TryGetValue(key, out var head))
      previous[position] = head;
    heads[key] = position;
  }

  private static int MatchKey(byte[] data, int position, bool fast)
    => fast
      ? (data[position] << 16) | (data[position + 1] << 8) | data[position + 2]
      : (data[position] << 8) | data[position + 1];

  private static void EmitMatch(List<byte> output, byte[] markers, int length, int offset) {
    var lengthCode = EncodeLength(length);

    if (offset <= 8) {
      output.Add(markers[3]);
      output.Add((byte)(((offset - 1) << 5) | lengthCode));
      return;
    }

    if (length <= 6 && offset <= 71) {
      output.Add(markers[2]);
      output.Add((byte)(((length - 3) << 6) | (offset - 8)));
      return;
    }

    if (offset < 2056) {
      var adjusted = offset - 8;
      output.Add(markers[1]);
      output.Add((byte)(((adjusted >> 3) & 0xE0) | lengthCode));
      output.Add((byte)adjusted);
      return;
    }

    var distant = offset - 2056;
    output.Add(markers[0]);
    output.Add((byte)(((distant >> 11) & 0xE0) | lengthCode));
    output.Add((byte)(distant >> 8));
    output.Add((byte)distant);
  }

  private static int TokenCost(int length, int offset)
    => offset <= 8 || length <= 6 && offset <= 71
      ? 2
      : offset < 2056
        ? 3
        : 4;

  private static byte EncodeLength(int length)
    => length switch {
      <= 29 => checked((byte)(length - 2)),
      35 => 28,
      48 => 29,
      72 => 30,
      128 => 31,
      _ => throw new InvalidOperationException($"Length {length} is not representable by LZG1."),
    };

  private static int QuantizeLength(int length)
    => length switch {
      < 3 => 0,
      <= 29 => length,
      < 35 => 29,
      < 48 => 35,
      < 72 => 48,
      < 128 => 72,
      _ => 128,
    };

  private static byte[] DetermineMarkers(ReadOnlySpan<byte> data) {
    Span<int> histogram = stackalloc int[256];
    histogram.Clear();
    foreach (var value in data)
      ++histogram[value];

    var markers = new byte[4];
    Span<bool> selected = stackalloc bool[256];
    selected.Clear();

    for (var markerIndex = 0; markerIndex < markers.Length; ++markerIndex) {
      var bestSymbol = 0;
      var bestCount = int.MaxValue;

      for (var symbol = 0; symbol < histogram.Length; ++symbol) {
        if (selected[symbol] || histogram[symbol] >= bestCount)
          continue;

        bestSymbol = symbol;
        bestCount = histogram[symbol];
      }

      markers[markerIndex] = (byte)bestSymbol;
      selected[bestSymbol] = true;
    }

    return markers;
  }

  private static byte[] DecompressLzg1(byte[] payload, int decodedSize) {
    if (payload.Length < 4)
      throw new InvalidDataException("LZG1 payload is too short for its marker table.");

    var marker1 = payload[0];
    var marker2 = payload[1];
    var marker3 = payload[2];
    var marker4 = payload[3];

    var isMarker = new bool[256];
    isMarker[marker1] = true;
    isMarker[marker2] = true;
    isMarker[marker3] = true;
    isMarker[marker4] = true;

    var result = new byte[decodedSize];
    var source = 4;
    var destination = 0;

    while (source < payload.Length) {
      var symbol = payload[source++];

      if (!isMarker[symbol]) {
        if (destination >= result.Length)
          throw new InvalidDataException("LZG1 payload expands beyond its declared decoded size.");
        result[destination++] = symbol;
        continue;
      }

      if (source >= payload.Length)
        throw new InvalidDataException("Unexpected end of LZG1 stream after marker.");

      var b = payload[source++];
      if (b == 0) {
        if (destination >= result.Length)
          throw new InvalidDataException("LZG1 payload expands beyond its declared decoded size.");
        result[destination++] = symbol;
        continue;
      }

      int length;
      int offset;

      if (symbol == marker1) {
        if (source + 2 > payload.Length)
          throw new InvalidDataException("Unexpected end of LZG1 distant-copy token.");
        length = LengthDecodeTable[b & 0x1F];
        offset = ((b & 0xE0) << 11) | (payload[source++] << 8) | payload[source++];
        offset += 2056;
      } else if (symbol == marker2) {
        if (source >= payload.Length)
          throw new InvalidDataException("Unexpected end of LZG1 medium-copy token.");
        length = LengthDecodeTable[b & 0x1F];
        offset = ((b & 0xE0) << 3) | payload[source++];
        offset += 8;
      } else if (symbol == marker3) {
        length = (b >> 6) + 3;
        offset = (b & 0x3F) + 8;
      } else {
        length = LengthDecodeTable[b & 0x1F];
        offset = (b >> 5) + 1;
      }

      if (offset <= 0 || offset > destination)
        throw new InvalidDataException($"LZG1 invalid offset {offset} at decoded position {destination}.");
      if (length > result.Length - destination)
        throw new InvalidDataException("LZG1 match expands beyond its declared decoded size.");

      for (var i = 0; i < length; ++i) {
        result[destination] = result[destination - offset];
        ++destination;
      }
    }

    if (destination != decodedSize)
      throw new InvalidDataException($"LZG1 decoded size mismatch: expected {decodedSize}, got {destination}.");

    return result;
  }

  private static uint CalculateChecksum(ReadOnlySpan<byte> data) {
    uint a = 1;
    uint b = 0;

    foreach (var value in data) {
      a = (a + value) & 0xFFFF;
      b = (b + a) & 0xFFFF;
    }

    return (b << 16) | a;
  }
}
