using System.Text;

namespace FileFormat.Zpaq;

/// <summary>
/// Walks a ZPAQ stream block by block and segment by segment: locate a block, read its
/// segments' filename and comment, decode the data, read the trailing SHA-1.
/// </summary>
/// <remarks>Converted from libzpaq 7.15 (Matt Mahoney, public domain), class <c>Decompresser</c>.</remarks>
internal sealed class ZpaqDecompresser {
  private enum Expect { Block, Filename, Comment, Data, SegmentEnd }
  private enum DecodeState { FirstSegment, Segment, Skip }

  private readonly ZpaqlMachine _z = new();
  private readonly ZpaqDecoder _dec;
  private readonly ZpaqPostProcessor _pp = new();
  private Expect _state = Expect.Block;
  private DecodeState _decodeState = DecodeState.FirstSegment;

  /// <summary>Creates a decompresser reading from <paramref name="input"/>.</summary>
  public ZpaqDecompresser(IZpaqReader input) {
    this._dec = new(this._z) { In = input };
  }

  /// <summary>Bytes read ahead of the logical position.</summary>
  public int Buffered => this._dec.Buffered;

  /// <summary>Level byte (1 or 2) of the current block.</summary>
  public int Level { get; private set; }

  /// <summary>Approximate memory the current block needs to decode.</summary>
  public double Memory => this._z.Memory();

  /// <summary>Discards read-ahead after the caller repositioned the underlying input.</summary>
  public void DiscardBuffer() => this._dec.DiscardBuffer();

  /// <summary>Scans for the next block header and reads it; <see langword="false"/> at end of input.</summary>
  public bool FindBlock() {
    uint h1 = 0x3D49B113, h2 = 0x29EB7F93, h3 = 0x2614BE13, h4 = 0x3828EB13;
    int c;
    while ((c = this._dec.Get()) != -1) {
      h1 = h1 * 12 + (uint)c;
      h2 = h2 * 20 + (uint)c;
      h3 = h3 * 28 + (uint)c;
      h4 = h4 * 44 + (uint)c;
      if (h1 == 0xB16B88F1 && h2 == 0xFF5376F1 && h3 == 0x72AC5BF1 && h4 == 0x2F909AF1)
        break;
    }
    if (c == -1)
      return false;

    if ((c = this._dec.Get()) != 1 && c != 2)
      ZpaqError.Throw("unsupported ZPAQ level");
    if (this._dec.Get() != 1)
      ZpaqError.Throw("unsupported ZPAQL type");
    this._z.Read(this._dec);
    if (c == 1 && this._z.Header.Length > 6 && this._z.Header[6] == 0)
      ZpaqError.Throw("ZPAQ level 1 requires at least 1 component");
    this.Level = c;
    this._state = Expect.Filename;
    this._decodeState = DecodeState.FirstSegment;
    return true;
  }

  /// <summary>Reads the next segment header; returns its filename, or <see langword="null"/> at end of block.</summary>
  public string? FindFilename() {
    var c = this._dec.Get();
    if (c == 1) {
      var name = new List<byte>();
      while (true) {
        c = this._dec.Get();
        if (c == -1)
          ZpaqError.Throw("unexpected EOF");
        if (c == 0) {
          this._state = Expect.Comment;
          return Encoding.UTF8.GetString(name.ToArray());
        }
        name.Add((byte)c);
      }
    }
    if (c == 255) {
      this._state = Expect.Block;
      return null;
    }
    ZpaqError.Throw("missing segment or end of block");
    return null;
  }

  /// <summary>Reads the comment of the current segment as raw bytes.</summary>
  public byte[] ReadComment() {
    this._state = Expect.Data;
    var comment = new List<byte>();
    while (true) {
      var c = this._dec.Get();
      if (c == -1)
        ZpaqError.Throw("unexpected EOF");
      if (c == 0)
        break;
      comment.Add((byte)c);
    }
    if (this._dec.Get() != 0)
      ZpaqError.Throw("missing reserved byte");
    return [.. comment];
  }

  /// <summary>Sets where decoded (post-processed) bytes go; <see langword="null"/> discards them.</summary>
  public void SetOutput(IZpaqWriter? output) => this._pp.Z.Output = output;

  /// <summary>Sets the hash that sees every output byte, or <see langword="null"/>.</summary>
  public void SetSha1(ZpaqSha1? sha1) => this._pp.Z.Sha1 = sha1;

  /// <summary>Decodes the whole current segment.</summary>
  public void Decompress() {
    if (this._decodeState == DecodeState.Skip)
      ZpaqError.Throw("decompression after skipped segment");
    if (this._decodeState == DecodeState.FirstSegment) {
      this._dec.Init();
      this._pp.Init(this._z.Header[4], this._z.Header[5]);
      this._decodeState = DecodeState.Segment;
    }
    while ((this._pp.State & 3) != 1)
      this._pp.Write(this._dec.Decompress());
    while (true) {
      var c = this._dec.Decompress();
      this._pp.Write(c);
      if (c == -1) {
        this._state = Expect.SegmentEnd;
        return;
      }
    }
  }

  /// <summary>Finishes the segment, skipping undecoded data; returns the stored SHA-1 or <see langword="null"/>.</summary>
  public byte[]? ReadSegmentEnd() {
    var c = 0;
    if (this._state == Expect.Data) {
      c = this._dec.Skip();
      this._decodeState = DecodeState.Skip;
    } else if (this._state == Expect.SegmentEnd)
      c = this._dec.Get();
    this._state = Expect.Filename;

    if (c == 254)
      return null;
    if (c != 253)
      ZpaqError.Throw("missing end of segment marker");
    var hash = new byte[20];
    for (var i = 0; i < 20; ++i) {
      c = this._dec.Get();
      if (c < 0)
        ZpaqError.Throw("unexpected EOF");
      hash[i] = (byte)c;
    }
    return hash;
  }
}
