using System.Text;

namespace FileFormat.Zpaq;

/// <summary>
/// Writes ZPAQ blocks: locator tag, block header (COMP + HCOMP), segments with filename,
/// comment, the PCOMP program, the coded data and the SHA-1 trailer.
/// </summary>
/// <remarks>Converted from libzpaq 7.15 (Matt Mahoney, public domain), class <c>Compressor</c>.</remarks>
internal sealed class ZpaqCompressor {
  private enum Phase { Init, Block1, Segment1, Block2, Segment2 }

  private readonly ZpaqlMachine _z = new();
  private readonly ZpaqlMachine _pz = new();
  private readonly ZpaqEncoder _enc;
  private Phase _state = Phase.Init;

  /// <summary>The 13-byte locator tag that lets a reader find blocks after arbitrary leading data.</summary>
  internal static ReadOnlySpan<byte> Tag => [0x37, 0x6b, 0x53, 0x74, 0xa0, 0x31, 0x83, 0xd3, 0x8c, 0xb2, 0x28, 0xb0, 0xd3];

  /// <summary>Creates a compressor writing to <paramref name="output"/>.</summary>
  public ZpaqCompressor(IZpaqWriter output) => this._enc = new(this._z) { Out = output };

  /// <summary>Writes the locator tag.</summary>
  public void WriteTag() => this._enc.Out.Write(Tag);

  /// <summary>Starts a block from ZPAQL source <paramref name="config"/>; returns the PCOMP command name.</summary>
  public string StartBlock(string config, int[]? args) {
    var compiler = new ZpaqCompiler(config, args, this._z, this._pz);
    var output = this._enc.Out;
    output.Put('z');
    output.Put('P');
    output.Put('Q');
    output.Put(1 + (this._z.Header[6] == 0 ? 1 : 0));
    output.Put(1);
    this._z.Write(output, false);
    this._state = Phase.Block1;
    return compiler.PcompCommand;
  }

  /// <summary>Starts a segment carrying <paramref name="filename"/> and <paramref name="comment"/>.</summary>
  public void StartSegment(string? filename, ReadOnlySpan<byte> comment) {
    var output = this._enc.Out;
    output.Put(1);
    if (!string.IsNullOrEmpty(filename))
      output.Write(Encoding.UTF8.GetBytes(filename));
    output.Put(0);
    output.Write(comment);
    output.Put(0);
    output.Put(0);
    this._state = this._state == Phase.Block1 ? Phase.Segment1 : Phase.Segment2;
  }

  /// <summary>Codes all bytes from <paramref name="input"/>.</summary>
  public void Compress(IZpaqReader input) {
    if (this._state == Phase.Segment1)
      this.PostProcess();
    Span<byte> buf = stackalloc byte[1 << 14];
    int nr;
    while ((nr = input.Read(buf)) > 0)
      for (var i = 0; i < nr; ++i)
        this._enc.Compress(buf[i]);
  }

  /// <summary>Ends the segment, storing <paramref name="sha1"/> when given.</summary>
  public void EndSegment(byte[]? sha1) {
    if (this._state == Phase.Segment1)
      this.PostProcess();
    this._enc.Compress(-1);
    var output = this._enc.Out;
    output.Put(0);
    output.Put(0);
    output.Put(0);
    output.Put(0);
    if (sha1 != null) {
      output.Put(253);
      output.Write(sha1);
    } else
      output.Put(254);
    this._state = Phase.Block2;
  }

  /// <summary>Ends the block.</summary>
  public void EndBlock() {
    this._enc.Out.Put(255);
    this._state = Phase.Init;
  }

  /// <summary>Initialises coding and emits the PCOMP program (or the pass-through marker) at the start of the first segment.</summary>
  private void PostProcess() {
    this._enc.Init();
    var len = this._pz.Hend - this._pz.Hbegin;
    if (len > 0) {
      this._enc.Compress(1);
      this._enc.Compress(len & 255);
      this._enc.Compress((len >> 8) & 255);
      for (var i = 0; i < len; ++i)
        this._enc.Compress(this._pz.Header[this._pz.Hbegin + i]);
    } else
      this._enc.Compress(0);
    this._state = Phase.Segment2;
  }
}
