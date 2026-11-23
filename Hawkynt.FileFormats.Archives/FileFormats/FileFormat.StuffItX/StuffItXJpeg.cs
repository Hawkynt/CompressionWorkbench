namespace FileFormat.StuffItX;

/// <summary>
/// StuffIt X method 7: JPEG recompression. Restores the original JPEG file byte for byte.
/// </summary>
/// <remarks>
/// <para>
/// The stream opens with a codec mode and a declared size, both as WZ integers (seven bits per byte,
/// most significant group first, the top bit set on every byte but the last). Mode 0 stores the JPEG
/// as it is. Modes 1 and 2 code everything else under one range decoder. The header bytes up to the
/// first scan use an order-1 adaptive byte model. The coefficients are then re-coded under
/// context-mixing models and turned back into the original Huffman entropy stream:
/// </para>
/// <list type="bullet">
///   <item><description>mode 1: three-component baseline images;</description></item>
///   <item><description>mode 2 with SOF0: baseline images, with restart markers where the file had them;</description></item>
///   <item><description>mode 2 with SOF2: progressive images, every scan in its original order and split.</description></item>
/// </list>
/// <para>
/// After the scan come the trailing markers and literal bytes. A tail of counted literal runs ends
/// the stream, and it must end exactly where the coded data does. A layout the decoder cannot
/// reproduce is refused rather than guessed: other sampling arrangements, component sets, 12-bit
/// precision and the other frame types.
/// </para>
/// <para>Converted from KaitoKit's <c>Codecs/StuffItX/JPEG/*.swift</c> (MIT, see THIRD-PARTY-NOTICE.KaitoKit.txt).</para>
/// </remarks>
internal static class StuffItXJpeg {
  /// <summary>The most coefficient blocks one image may hold (512 MiB of coefficient planes).</summary>
  internal const int MaxBlocks = 2_097_152;

  /// <summary>Restores the JPEG file a method-7 stream holds.</summary>
  /// <param name="data">The coded stream (decrypted).</param>
  /// <param name="size">The size the archive declares for the restored file, or -1 if unknown.</param>
  /// <exception cref="InvalidDataException">The stream is corrupt or truncated.</exception>
  /// <exception cref="NotSupportedException">The stream uses a layout this decoder does not restore.</exception>
  internal static byte[] Decode(byte[] data, long size) {
    var input = new StuffItXJpegInput(data);
    var mode = input.Wz();
    if (mode > 2) throw Unsupported("unsupported jcodec mode");
    var declared = input.Wz();
    if (mode == 0) {
      if (declared != (ulong)(data.Length - input.Position)) throw Malformed("raw JPEG extent mismatch");
      if (size >= 0 && (ulong)size != declared) throw Malformed("output extent mismatch");
      return data.AsSpan(input.Position).ToArray();
    }

    var limit = size >= 0 ? Math.Min(size, Array.MaxLength) : Array.MaxLength;
    var output = new StuffItXJpegOutput(limit);
    var range = new StuffItXJpegRange(input);
    var model = new StuffItXJpegHeaderModel();
    int Get() => model.Byte(range);
    var prefix = StuffItXJpegEnvelope.FirstScan(limit, Get);

    StuffItXJpegBaseline? baseline = null;
    StuffItXJpegMode1? mode1 = null;
    StuffItXJpegProgressive? progressive = null;
    if (prefix.Scan == null && prefix.Frame?.Marker != StuffItXJpegMarker.Sof2)
      output.Append(prefix.Header);
    else if (mode == 2 && prefix.Frame?.Marker == StuffItXJpegMarker.Sof0) {
      baseline = new StuffItXJpegBaseline(prefix, range, output);
      output.Append(prefix.Header);
    } else if (mode == 2)
      progressive = new StuffItXJpegProgressive(prefix, range, model, output);
    else {
      mode1 = new StuffItXJpegMode1(prefix, range, output);
      output.Append(prefix.Header);
    }

    if (baseline != null)
      while (!baseline.Done) baseline.Step();
    if (mode1 != null)
      while (!mode1.Done) mode1.Step();
    if (progressive != null) {
      while (!progressive.Done) progressive.Step();
      output.Append(StuffItXJpegMarker.Prefix);
      output.Append(StuffItXJpegMarker.Eoi);
    } else if (baseline != null || mode1 != null)
      PostScan(output, Get);

    // the tail: counted literal runs, then a zero count
    for (int count; (count = Get()) != 0;)
      for (var i = 0; i < count; ++i) output.Append(Get());
    if (input.Position != data.Length) throw Malformed("JPEG encoded extent mismatch");
    if (size >= 0 && size != output.Count) throw Malformed("output extent mismatch");
    return output.ToArray();
  }

  /// <summary>The markers and literal bytes after a baseline scan, up to the end of image.</summary>
  private static void PostScan(StuffItXJpegOutput output, Func<int> get) {
    var markers = 0;
    for (;;) {
      var (marker, token) = StuffItXJpegEnvelope.WireToken(get);
      if (marker is not { } m) {
        output.Append(token);
        continue;
      }
      if (++markers > 4096) throw Malformed("JPEG marker limit");
      if (m is StuffItXJpegMarker.Soi or StuffItXJpegMarker.Eoi) {
        output.Append(StuffItXJpegMarker.Prefix);
        output.Append(StuffItXJpegMarker.Eoi);
        return;
      }
      if (m is StuffItXJpegMarker.StuffedZero or StuffItXJpegMarker.Tem or StuffItXJpegMarker.Sof0 or StuffItXJpegMarker.Sof2 or StuffItXJpegMarker.Sos
          || StuffItXJpegMarker.IsRst(m))
        throw Unsupported("unsupported marker after baseline scan");
      output.Append(token);
      int high = get(), low = get(), length = high * 256 + low;
      if (length < 2) throw Malformed("JPEG segment length");
      output.Append(high);
      output.Append(low);
      for (var i = 0; i < length - 2; ++i) output.Append(get());
    }
  }

  internal static InvalidDataException Malformed(string reason) => new($"StuffIt X JPEG: {reason}.");

  internal static NotSupportedException Unsupported(string reason) => new($"StuffIt X JPEG: {reason}.");

  /// <summary>Two's-complement truncation to 16 bits.</summary>
  internal static int I16(int n) => (short)n;

  internal static int BitLength(int n) => n == 0 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)n);

  internal static int Cat3(int n) {
    var a = Math.Abs(n);
    return a < 2 ? 0 : a < 6 ? 1 : 2;
  }

  internal static int Cat4(int n) {
    var a = Math.Abs(n);
    return a < 2 ? 0 : a < 6 ? 1 : a <= 16 ? 2 : 3;
  }

  internal static int Cat6(int n) {
    var a = Math.Abs(n);
    return a < 2 ? 0 : a < 4 ? 1 : a < 10 ? 2 : a < 37 ? 3 : a < 145 ? 4 : 5;
  }

  internal static int Category(int n) {
    var a = Math.Min(Math.Abs(I16(n)), 16383);
    return a < 6 ? a : Math.Min(15, 5 + BitLength(a - 5));
  }

  internal static int Activity(int n) => n == 0 ? 0 : n < 7 ? 1 : n < 13 ? 2 : n < 49 ? 3 : n <= 160 ? 4 : 5;

  internal static int SignClass(int n) => n < 0 ? 0 : n == 0 ? 1 : 2;
}

/// <summary>The JPEG markers (ITU-T T.81 table B.1) the restorer distinguishes: the byte after 0xFF.</summary>
internal static class StuffItXJpegMarker {
  public const int Prefix = 0xFF;
  public const int Fill = 0xFF;
  public const int StuffedZero = 0x00;
  public const int Tem = 0x01;
  public const int Sof0 = 0xC0;
  public const int Sof2 = 0xC2;
  public const int Dht = 0xC4;
  public const int Jpg = 0xC8;
  public const int Dac = 0xCC;
  public const int Rst0 = 0xD0;
  public const int Soi = 0xD8;
  public const int Eoi = 0xD9;
  public const int Sos = 0xDA;
  public const int Dqt = 0xDB;
  public const int Dri = 0xDD;

  public static bool IsRst(int marker) => marker is >= 0xD0 and <= 0xD7;

  /// <summary>SOF0 to SOF15, which share the range with DHT, JPG and DAC.</summary>
  public static bool IsFrame(int marker) => marker is >= 0xC0 and <= 0xCF and not (Dht or Jpg or Dac);
}

/// <summary>The coded bytes: plain reads and WZ integers.</summary>
internal sealed class StuffItXJpegInput(byte[] data) {
  public int Position { get; private set; }

  public int Byte() => this.Position < data.Length ? data[this.Position++] : throw StuffItXJpeg.Malformed("stream is truncated");

  public ulong Wz() {
    ulong result = 0;
    for (var i = 0; i < 10; ++i) {
      var value = this.Byte();
      if (result > ulong.MaxValue >> 7) throw StuffItXJpeg.Malformed("WZ integer overflow");
      result = (result << 7) | (uint)(value & 127);
      if ((value & 128) == 0) return result;
    }
    throw StuffItXJpeg.Malformed("unterminated WZ integer");
  }
}

/// <summary>The range decoder: a 32-bit code and range, refilled a byte at a time below 2^24.</summary>
internal sealed class StuffItXJpegRange {
  private readonly StuffItXJpegInput _input;
  private uint _code;
  private uint _range = uint.MaxValue;

  public StuffItXJpegRange(StuffItXJpegInput input) {
    this._input = input;
    for (var i = 0; i < 5; ++i) this._code = (this._code << 8) | (uint)input.Byte();
  }

  /// <summary>Decodes one symbol of a frequency table.</summary>
  public int Value(ReadOnlySpan<int> f) {
    long total = 0;
    foreach (var v in f) total += v;
    if (total <= 0 || total > this._range) throw StuffItXJpeg.Malformed("invalid frequency total");
    var unit = this._range / (uint)total;
    long target = this._code / unit;
    long cumulative = 0;
    var symbol = 0;
    for (; symbol < f.Length; ++symbol) {
      if (f[symbol] < 0) throw StuffItXJpeg.Malformed("negative frequency");
      if (target < cumulative + f[symbol]) break;
      cumulative += f[symbol];
    }
    if (symbol == f.Length) throw StuffItXJpeg.Malformed("arithmetic code outside distribution");
    this._code -= (uint)cumulative * unit;
    this._range = (uint)f[symbol] * unit;
    this.Normalize();
    return symbol;
  }

  /// <summary>Decodes one equiprobable bit.</summary>
  public int Bit() {
    var unit = this._range >> 1;
    if (unit == 0 || this._code / unit >= 2) throw StuffItXJpeg.Malformed("arithmetic code outside distribution");
    var symbol = this._code < unit ? 0 : 1;
    this._code -= (uint)symbol * unit;
    this._range = unit;
    this.Normalize();
    return symbol;
  }

  /// <summary>Decodes <paramref name="count"/> equiprobable bits, most significant first unless <paramref name="little"/>.</summary>
  public int Bits(int count, bool little = false) {
    var value = 0;
    for (var i = 0; i < count; ++i) {
      var b = this.Bit();
      value = little ? value | (b << i) : value * 2 + b;
    }
    return value;
  }

  private void Normalize() {
    while (this._range < 1u << 24) {
      this._code = (this._code << 8) | (uint)this._input.Byte();
      this._range <<= 8;
    }
  }
}

/// <summary>The order-1 adaptive model of the header bytes.</summary>
internal sealed class StuffItXJpegHeaderModel {
  private readonly int[] _zero = Enumerable.Repeat(1, 256).ToArray();
  private readonly int[] _one = new int[256 * 256];
  private readonly int[] _frequencies = new int[256];
  private int _previous;

  public int Byte(StuffItXJpegRange decoder) {
    var context = this._one.AsSpan(this._previous * 256, 256);
    for (var i = 0; i < 256; ++i) this._frequencies[i] = this._zero[i] + 8 * context[i];
    var symbol = decoder.Value(this._frequencies);
    Update(context, symbol);
    Update(this._zero, symbol);
    this._previous = symbol;
    return symbol;
  }

  private static void Update(Span<int> row, int symbol) {
    row[symbol] += 8;
    var sum = 0;
    foreach (var v in row) sum += v;
    if (sum <= 500) return;
    for (var i = 0; i < row.Length; ++i) row[i] = (row[i] + 1) / 2;
  }
}

/// <summary>The restored file, growing as it is written, never past its declared size.</summary>
internal sealed class StuffItXJpegOutput(long limit) {
  private byte[] _buffer = new byte[4096];

  public int Count { get; private set; }

  public void Append(int value) {
    if (this.Count >= limit) throw StuffItXJpeg.Malformed("output runs past its declared size");
    if (this.Count == this._buffer.Length) Array.Resize(ref this._buffer, (int)Math.Min((long)this._buffer.Length * 2, Array.MaxLength));
    this._buffer[this.Count++] = (byte)value;
  }

  public void Append(ReadOnlySpan<byte> bytes) {
    foreach (var b in bytes) this.Append(b);
  }

  public void Append(List<byte> bytes) => this.Append(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(bytes));

  public byte[] ToArray() => this._buffer.AsSpan(0, this.Count).ToArray();
}

internal sealed record StuffItXJpegComponent(int Id, int Horizontal = 1, int Vertical = 1, int Quantization = 0);

internal sealed record StuffItXJpegFrame(int Marker, int Width, int Height, StuffItXJpegComponent[] Components);

internal sealed record StuffItXJpegScan((int Id, int Selector)[] Components, int Ss, int Se, int Ah, int Al);

internal sealed record StuffItXJpegSegment(int Marker, int Offset, int End, int Length = 0);

/// <summary>The header bytes up to (and including) the first scan header, and what they declare.</summary>
internal sealed class StuffItXJpegPrefix {
  public List<byte> Header { get; } = [];
  public StuffItXJpegFrame? Frame { get; set; }
  public StuffItXJpegScan? Scan { get; set; }
  public List<StuffItXJpegSegment> Segments { get; } = [];
  public int Literals { get; set; }

  /// <summary>The body of a segment (after its length).</summary>
  public byte[] Body(StuffItXJpegSegment s) => this.Header.GetRange(s.End - s.Length + 2, s.Length - 2).ToArray();
}

/// <summary>The marker syntax of the header bytes.</summary>
internal static class StuffItXJpegEnvelope {
  /// <summary>One header token: a literal byte, a stuffed FF 00 (dropped), FF FF, or a marker.</summary>
  public static (int? Marker, byte[] Token) WireToken(Func<int> get) {
    var first = get();
    if (first != 255) return (null, [(byte)first]);
    var second = get();
    return second switch {
      0 => (null, []),
      255 => (null, [255, 255]),
      _ => (second, [255, (byte)second]),
    };
  }

  /// <summary>Reads the header bytes up to the first scan header, or to an end of image before it.</summary>
  public static StuffItXJpegPrefix FirstScan(long limit, Func<int> next) {
    var result = new StuffItXJpegPrefix();
    int Get() {
      if (result.Header.Count >= limit) throw StuffItXJpeg.Malformed("header runs past the declared size");
      var value = next();
      result.Header.Add((byte)value);
      return value;
    }

    if (Get() != StuffItXJpegMarker.Prefix || Get() != StuffItXJpegMarker.Soi) throw StuffItXJpeg.Malformed("JPEG SOI required");
    result.Segments.Add(new StuffItXJpegSegment(StuffItXJpegMarker.Soi, 0, 2));
    for (long tokens = 0; tokens < limit; ++tokens) {
      var start = result.Header.Count;
      if (Get() != StuffItXJpegMarker.Prefix) {
        ++result.Literals;
        continue;
      }
      var marker = Get();
      if (marker == 255) {
        result.Literals += 2;
        continue;
      }
      if (marker == 0) {
        result.Header.RemoveRange(result.Header.Count - 2, 2);
        continue;
      }
      if (marker is StuffItXJpegMarker.Soi or StuffItXJpegMarker.Eoi) {
        result.Header[^1] = StuffItXJpegMarker.Eoi;
        result.Segments.Add(new StuffItXJpegSegment(StuffItXJpegMarker.Eoi, start, result.Header.Count));
        return result;
      }
      if (marker is StuffItXJpegMarker.Tem || StuffItXJpegMarker.IsRst(marker)) throw StuffItXJpeg.Unsupported("unsupported marker before first scan");
      var length = Get() * 256 + Get();
      if (length < 2) throw StuffItXJpeg.Malformed("JPEG segment length");
      var body = new int[length - 2];
      for (var i = 0; i < body.Length; ++i) body[i] = Get();
      if (result.Segments.Count >= 4096) throw StuffItXJpeg.Malformed("JPEG segment count");
      result.Segments.Add(new StuffItXJpegSegment(marker, start, result.Header.Count, length));
      if (marker is StuffItXJpegMarker.Sof0 or StuffItXJpegMarker.Sof2)
        result.Frame = ReadFrame(result.Frame, marker, body);
      else if (marker == StuffItXJpegMarker.Sos) {
        result.Scan = ReadScan(result, body);
        return result;
      }
    }
    throw StuffItXJpeg.Malformed("JPEG segment count");
  }

  private static StuffItXJpegFrame ReadFrame(StuffItXJpegFrame? existing, int marker, int[] body) {
    if (existing != null || body.Length < 6 || body.Length != 6 + 3 * body[5]) throw StuffItXJpeg.Malformed("JPEG frame layout");
    var components = new List<StuffItXJpegComponent>();
    for (var i = 6; i < body.Length; i += 3)
      components.Add(new StuffItXJpegComponent(body[i], body[i + 1] >> 4, body[i + 1] & 15, body[i + 2]));
    if (components.Count == 0 || components.Select(c => c.Id).Distinct().Count() != components.Count) throw StuffItXJpeg.Malformed("JPEG frame components");
    if (components.Count > 4) throw StuffItXJpeg.Unsupported("JPEG frame components");
    if (!components.All(c => c.Horizontal is >= 1 and <= 4 && c.Vertical is >= 1 and <= 4)) throw StuffItXJpeg.Malformed("JPEG sampling factors");
    int height = body[1] * 256 + body[2], width = body[3] * 256 + body[4];
    if (width <= 0 || height <= 0) throw StuffItXJpeg.Malformed("JPEG frame dimensions or precision");
    if (body[0] != 8) throw StuffItXJpeg.Unsupported("JPEG frame dimensions or precision");
    return new StuffItXJpegFrame(marker, width, height, [.. components]);
  }

  private static StuffItXJpegScan ReadScan(StuffItXJpegPrefix prefix, int[] body) {
    if (prefix.Frame is not { } frame) {
      // an unsupported frame type, as opposed to a missing one
      if (prefix.Segments.Any(s => StuffItXJpegMarker.IsFrame(s.Marker))) throw StuffItXJpeg.Unsupported("unsupported JPEG frame marker");
      throw StuffItXJpeg.Malformed("JPEG scan layout");
    }
    if (body.Length < 4 || body.Length != 4 + 2 * body[0] || body[0] is < 1 or > 4) throw StuffItXJpeg.Malformed("JPEG scan layout");
    var components = new (int, int)[body[0]];
    for (var i = 0; i < components.Length; ++i) components[i] = (body[1 + 2 * i], body[2 + 2 * i]);
    var ids = components.Select(c => c.Item1).ToArray();
    if (ids.Distinct().Count() != ids.Length || !ids.All(id => frame.Components.Any(c => c.Id == id))) throw StuffItXJpeg.Malformed("JPEG scan component references");
    return new StuffItXJpegScan(components, body[^3], body[^2], body[^1] >> 4, body[^1] & 15);
  }
}

/// <summary>The block layout of a frame: components by id, MCU columns and rows, and the block count.</summary>
internal sealed class StuffItXJpegGeometry {
  public StuffItXJpegComponent[] Components { get; }
  public int Width { get; }
  public int Height { get; }
  public int Blocks { get; }

  /// <summary>
  /// The components in id order and renumbered from the lowest id. A slot is filled as the lowest id
  /// seen so far places it, which is what the coder does for frames listing their components out of order.
  /// </summary>
  public static StuffItXJpegComponent[] ComponentsById(StuffItXJpegFrame frame) {
    var ids = frame.Components.Select(c => c.Id).Order().ToArray();
    if (!(ids is [0] or [1] or [0, 1, 2] or [1, 2, 3])) throw StuffItXJpeg.Unsupported("unsupported component arrangement");
    var origin = 1;
    var slots = Enumerable.Repeat(new StuffItXJpegComponent(0), ids.Length).ToArray();
    foreach (var component in frame.Components) {
      origin = Math.Min(origin, component.Id);
      slots[component.Id - origin] = component;
    }
    for (var i = 0; i < slots.Length; ++i) slots[i] = slots[i] with { Id = origin + i };
    return slots;
  }

  public StuffItXJpegGeometry(StuffItXJpegFrame frame) {
    this.Components = ComponentsById(frame);
    int h = this.Components[0].Horizontal, v = this.Components[0].Vertical;
    if (h is < 1 or > 2 || v is < 1 or > 2 || !this.Components.Skip(1).All(c => c.Horizontal == 1 && c.Vertical == 1)
        || (this.Components.Length == 1 && (h != 1 || v != 1)))
      throw StuffItXJpeg.Unsupported("unsupported sampling arrangement");
    this.Width = (frame.Width + 8 * h - 1) / (8 * h);
    this.Height = (frame.Height + 8 * v - 1) / (8 * v);
    var blocks = (long)this.Width * this.Height * (h * v + this.Components.Length - 1);
    if (blocks > StuffItXJpeg.MaxBlocks) throw StuffItXJpeg.Malformed("too many coefficient blocks");
    this.Blocks = (int)blocks;
  }
}

/// <summary>A Huffman table of a DHT segment, as codes and lengths per symbol.</summary>
internal sealed class StuffItXJpegHuffman {
  public readonly int[] Codes = new int[256];
  public readonly int[] Lengths = new int[256];

  public StuffItXJpegHuffman(int[] counts, int[] values) {
    if (counts.Length != 16 || counts.Sum() != values.Length || values.Distinct().Count() != values.Length) throw StuffItXJpeg.Malformed("ambiguous Huffman table");
    int code = 0, cursor = 0;
    for (var length = 1; length <= 16; ++length) {
      for (var n = 0; n < counts[length - 1]; ++n) {
        if (code >= (1 << length) - 1) throw StuffItXJpeg.Malformed("invalid JPEG Huffman code space");
        var value = values[cursor++];
        if (value is < 0 or > 255) throw StuffItXJpeg.Malformed("Huffman symbol range");
        this.Codes[value] = code++;
        this.Lengths[value] = length;
      }
      code <<= 1;
    }
  }

  public void Write(StuffItXJpegEntropyWriter bits, int symbol) {
    if (symbol is < 0 or > 255 || this.Lengths[symbol] == 0) throw StuffItXJpeg.Malformed("missing Huffman symbol");
    bits.Put(this.Codes[symbol], this.Lengths[symbol]);
  }
}

/// <summary>The DQT, DHT and DRI state, and the predictor profile each component's quantization picks.</summary>
internal sealed class StuffItXJpegTableSet {
  public Dictionary<int, int[]> Quantization { get; private init; } = [];
  public Dictionary<int, StuffItXJpegHuffman> Huffman { get; private init; } = [];
  public int Restart { get; private set; }
  public int[] FrameProfiles { get; private set; } = [];

  private StuffItXJpegTableSet() { }

  public StuffItXJpegTableSet(StuffItXJpegPrefix prefix) {
    foreach (var s in prefix.Segments) {
      if (s.Length <= 0) continue;
      this.Segment(s.Marker, prefix.Body(s));
      if (s.Marker is StuffItXJpegMarker.Sof0 or StuffItXJpegMarker.Sof2 && prefix.Frame is { } frame)
        this.FrameProfiles = StuffItXJpegGeometry.ComponentsById(frame).Select(c => {
          var value = this.Quantization.ContainsKey(c.Quantization) ? this.Scaled(c.Quantization)[2] : 0;
          return value < 12 ? 0 : value < 48 ? 1 : 5;
        }).ToArray();
    }
  }

  /// <summary>A snapshot that later segments do not change.</summary>
  public StuffItXJpegTableSet Copy() => new() {
    Quantization = new(this.Quantization), Huffman = new(this.Huffman), Restart = this.Restart, FrameProfiles = this.FrameProfiles,
  };

  /// <summary>The DHT key (class &lt;&lt; 4 | id) of a scan selector's DC table.</summary>
  public static int DcTableKey(int selector) => selector >> 4;

  /// <summary>The DHT key of a scan selector's AC table.</summary>
  public static int AcTableKey(int selector) => 16 + (selector & 15);

  public void Segment(int marker, byte[] body) {
    var position = 0;
    int Byte() => position < body.Length ? body[position++] : throw StuffItXJpeg.Malformed("segment is truncated");
    switch (marker) {
      case StuffItXJpegMarker.Dqt:
        while (position < body.Length) {
          var key = Byte();
          if ((key & 15) > 3 || key >> 4 > 1) throw StuffItXJpeg.Malformed("quantization table selector");
          if (key >> 4 != 0) throw StuffItXJpeg.Unsupported("only eight-bit quantization tables supported");
          var table = new int[64];
          foreach (var p in StuffItXJpegTables.Zigzag) table[p] = Byte();
          if (table.Any(v => v <= 0)) throw StuffItXJpeg.Malformed("zero quantization value");
          this.Quantization[key] = table;
        }
        break;
      case StuffItXJpegMarker.Dht:
        while (position < body.Length) {
          var key = Byte();
          if (key is not (0 or 1 or 2 or 3 or 16 or 17 or 18 or 19)) throw StuffItXJpeg.Malformed("Huffman table selector");
          var counts = new int[16];
          for (var i = 0; i < 16; ++i) counts[i] = Byte();
          var values = new int[counts.Sum()];
          for (var i = 0; i < values.Length; ++i) values[i] = Byte();
          this.Huffman[key] = new StuffItXJpegHuffman(counts, values);
        }
        break;
      case StuffItXJpegMarker.Dri:
        if (body.Length != 2) throw StuffItXJpeg.Malformed("restart interval length");
        this.Restart = body[0] * 256 + body[1];
        break;
    }
  }

  /// <summary>
  /// A quantization table scaled by the DCT factors: value × (scale[x] × scale[y]) × 8 + 0.5 in
  /// double precision, in exactly that order, then truncated.
  /// </summary>
  public int[] Scaled(int key) {
    if (!this.Quantization.TryGetValue(key, out var q)) throw StuffItXJpeg.Malformed("undefined quantization table");
    var result = new int[64];
    for (var i = 0; i < 64; ++i)
      result[i] = (int)((double)q[i] * (StuffItXJpegTables.Scale[i % 8] * StuffItXJpegTables.Scale[i / 8]) * 8.0 + 0.5);
    return result;
  }

  /// <summary>The scaled table of a component, refused when a value falls outside 1..32767.</summary>
  public int[] ScaledChecked(int key) {
    var q = this.Scaled(key);
    if (q.Any(v => v is <= 0 or > 32767)) throw StuffItXJpeg.Malformed("scaled quantization table");
    return q;
  }
}

/// <summary>The baseline entropy coder: bits most significant first, FF bytes stuffed with 00.</summary>
internal sealed class StuffItXJpegEntropyWriter(StuffItXJpegOutput output) {
  private int _value;
  private int _count;

  public StuffItXJpegOutput Output => output;

  public void Put(int v, int n) {
    if (n is < 0 or > 16 || v < 0 || v >= 1 << n) throw StuffItXJpeg.Malformed("entropy bit value");
    this._value = (this._value << n) | v;
    this._count += n;
    while (this._count >= 8) {
      this._count -= 8;
      var b = (this._value >> this._count) & 255;
      output.Append(b);
      if (b == 255) output.Append(0);
    }
    this._value &= (1 << this._count) - 1;
  }

  /// <summary>Pads to a byte boundary with one bits, or with bits from <paramref name="padding"/>.</summary>
  public void Finish(Func<int>? padding = null) {
    var n = (8 - this._count) % 8;
    for (var i = 0; i < n; ++i) this.Put(padding?.Invoke() ?? 1, 1);
  }

  /// <summary>Codes one block of quantized coefficients (natural order) under its DC and AC tables.</summary>
  public void Block(int[] co, int offset, int previousDc, StuffItXJpegHuffman dc, StuffItXJpegHuffman ac) {
    var difference = co[offset] - previousDc;
    var size = StuffItXJpeg.BitLength(Math.Abs(difference));
    if (size > 11) throw StuffItXJpeg.Malformed("baseline DC range");
    dc.Write(this, size);
    if (size != 0) this.Put(difference > 0 ? difference : difference + (1 << size) - 1, size);
    var run = 0;
    for (var i = 1; i < 64; ++i) {
      var v = co[offset + StuffItXJpegTables.Zigzag[i]];
      if (v == 0) {
        ++run;
        continue;
      }
      for (; run >= 16; run -= 16) ac.Write(this, 240);
      size = StuffItXJpeg.BitLength(Math.Abs(v));
      if (size > 10) throw StuffItXJpeg.Malformed("baseline AC range");
      ac.Write(this, run * 16 + size);
      this.Put(v > 0 ? v : v + (1 << size) - 1, size);
      run = 0;
    }
    if (run != 0) ac.Write(this, 0);
  }
}
