#pragma warning disable CS1591
using System.Text;

namespace FileFormat.Paq8;

/// <summary>
/// PAQ8 stream compressor. Writes a simplified single-file paq8l container with an
/// arithmetic-coded payload driven by a per-byte bit-tree context model.
/// </summary>
public static class Paq8Stream {

  // ── Container constants ────────────────────────────────────────────────────

  private const string HeaderLine = "paq8l -5\r\n";
  private const byte CtrlZ = 0x1A;

  // ── Public API ─────────────────────────────────────────────────────────────

  /// <summary>Compresses <paramref name="input"/> into the PAQ8 container on <paramref name="output"/>.</summary>
  public static void Compress(Stream input, Stream output) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    var data = ms.ToArray();

    // Header: "paq8l -5\r\n"
    output.Write(Encoding.ASCII.GetBytes(HeaderLine));

    // File entry: "<size>\tdata\r\n"
    output.Write(Encoding.ASCII.GetBytes($"{data.Length}\tdata\r\n"));

    // Header terminator
    output.WriteByte(CtrlZ);

    // Arithmetic-coded payload
    var encoder = new Paq8Encoder(output);
    var model = new BitTreeModel();

    foreach (var b in data)
      model.EncodeByte(encoder, b);

    encoder.Flush();
  }

  /// <summary>Decompresses a PAQ8 container from <paramref name="input"/> into <paramref name="output"/>.</summary>
  public static void Decompress(Stream input, Stream output) {
    // Read header line: "paq8l -N\r\n"
    var headerBytes = new byte[7];
    input.ReadExactly(headerBytes);
    if (headerBytes[0] != 0x70 || headerBytes[1] != 0x61 || headerBytes[2] != 0x71 ||
        headerBytes[3] != 0x38 || headerBytes[4] != 0x6C || headerBytes[5] != 0x20 || headerBytes[6] != 0x2D)
      throw new InvalidDataException("Not a PAQ8 stream.");

    // Skip remainder of header line up to and including 0x1A
    long fileSize = -1;
    var lineBuf = new StringBuilder();
    int ch;
    // We've consumed "paq8l -" — finish the first line (level digit + \r\n)
    // then read subsequent lines until 0x1A
    while ((ch = input.ReadByte()) != -1) {
      if (ch == CtrlZ) break;
      if (ch == '\n') {
        // Process accumulated line
        var line = lineBuf.ToString().Trim('\r', '\n', ' ');
        lineBuf.Clear();
        // File entry lines look like: "<size>\t<name>"
        var tabIdx = line.IndexOf('\t');
        if (tabIdx > 0 && long.TryParse(line[..tabIdx], out var sz))
          fileSize = sz;
      } else {
        lineBuf.Append((char)ch);
      }
    }

    if (fileSize < 0)
      throw new InvalidDataException("PAQ8: could not parse file size from header.");

    // Decode payload
    var decoder = new Paq8Decoder(input);
    var model = new BitTreeModel();
    var result = new byte[fileSize];
    for (long i = 0; i < fileSize; i++)
      result[i] = model.DecodeByte(decoder);

    output.Write(result);
  }

  // ── 32-bit carryless arithmetic coder ──────────────────────────────────────
  // Uses (low, high) representation with 12-bit probability scale (4096).
  // Normalization: while top byte of low == top byte of high, emit low>>24 and shift both.

  private sealed class Paq8Encoder {
    private readonly Stream _stream;
    private uint _low;
    private uint _high = 0xFFFFFFFF;

    public Paq8Encoder(Stream stream) => _stream = stream;

    /// <summary>Encodes one bit. <paramref name="p0"/> is the 12-bit probability that bit==0 (0..4095).</summary>
    public void EncodeBit(int bit, int p0) {
      var range = (ulong)(_high - _low) + 1;
      var mid = _low + (uint)((range * (ulong)p0) >> 12) - 1;
      if (mid < _low) mid = _low; // clamp when p0 is near 0

      if (bit == 0)
        _high = (uint)mid;
      else
        _low = (uint)mid + 1;

      Normalize();
    }

    private void Normalize() {
      while ((_low ^ _high) < 0x01000000u) {
        _stream.WriteByte((byte)(_low >> 24));
        _low <<= 8;
        _high = (_high << 8) | 0xFF;
      }
    }

    public void Flush() {
      // Emit enough bytes to fully determine the value
      for (var i = 0; i < 4; i++) {
        _stream.WriteByte((byte)(_low >> 24));
        _low <<= 8;
      }
    }
  }

  private sealed class Paq8Decoder {
    private readonly Stream _stream;
    private uint _low;
    private uint _high = 0xFFFFFFFF;
    private uint _code;

    public Paq8Decoder(Stream stream) {
      _stream = stream;
      // Prime with 4 bytes
      for (var i = 0; i < 4; i++)
        _code = (_code << 8) | (uint)ReadByte();
    }

    /// <summary>Decodes one bit given a 12-bit probability that bit==0.</summary>
    public int DecodeBit(int p0) {
      var range = (ulong)(_high - _low) + 1;
      var mid = _low + (uint)((range * (ulong)p0) >> 12) - 1;
      if (mid < _low) mid = _low;

      int bit;
      if (_code <= (uint)mid) {
        bit = 0;
        _high = (uint)mid;
      } else {
        bit = 1;
        _low = (uint)mid + 1;
      }

      Normalize();
      return bit;
    }

    private void Normalize() {
      while ((_low ^ _high) < 0x01000000u) {
        _low <<= 8;
        _high = (_high << 8) | 0xFF;
        _code = (_code << 8) | (uint)ReadByte();
      }
    }

    private int ReadByte() {
      var b = _stream.ReadByte();
      return b < 0 ? 0xFF : b;
    }
  }

  // ── Bit-tree context model ─────────────────────────────────────────────────
  // 255 nodes (indexed 1..255) per byte context.
  // Each node stores a 12-bit adaptive probability (p = Pr[next bit == 0] * 4096).
  // Encoding byte b: start at node 1, walk bit-by-bit from MSB to LSB.
  // Update rule: if bit==0: p += (4096-p) >> 5; if bit==1: p -= p >> 5.

  private sealed class BitTreeModel {
    // 255 nodes, 1-indexed (index 0 unused)
    private readonly int[] _prob = new int[256];

    public BitTreeModel() {
      // Initialise to balanced (2048 = 0.5)
      Array.Fill(_prob, 2048);
    }

    public void EncodeByte(Paq8Encoder enc, byte b) {
      var node = 1;
      for (var i = 7; i >= 0; i--) {
        var bit = (b >> i) & 1;
        enc.EncodeBit(bit, _prob[node]);
        Update(node, bit);
        node = (node << 1) | bit;
      }
    }

    public byte DecodeByte(Paq8Decoder dec) {
      var node = 1;
      for (var i = 7; i >= 0; i--) {
        var bit = dec.DecodeBit(_prob[node]);
        Update(node, bit);
        node = (node << 1) | bit;
      }
      // node is now 256 + byte value
      return (byte)(node - 256);
    }

    private void Update(int node, int bit) {
      if (bit == 0)
        _prob[node] += (4096 - _prob[node]) >> 5;
      else
        _prob[node] -= _prob[node] >> 5;
    }
  }
}
