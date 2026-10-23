namespace Compression.Core.Dictionary.Lzh;

/// <summary>
/// The move-to-front byte history PMarc's <c>-pm1-</c> and <c>-pm2-</c> code literals against: a
/// literal is sent as how far back along this ring of all 256 byte values it lies from the last byte
/// written, and every byte written moves to the front.
/// </summary>
/// <remarks>
/// The ring starts as 0x20-0x7F, 0x00-0x1F, 0xA0-0xDF, 0x80-0x9F, 0xE0-0xFF. Sourcing: converted from
/// lhasa's <c>pma_common.c</c> (ISC, Simon Howard); see THIRD-PARTY-NOTICE.lhasa.txt beside this file.
/// </remarks>
internal sealed class PmaHistory {
  private readonly byte[] _prev = new byte[256];
  private readonly byte[] _next = new byte[256];
  private byte _head;

  public PmaHistory() {
    for (var i = 0; i < 256; ++i) {
      this._prev[i] = (byte)(i + 1);
      this._next[i] = (byte)(i - 1);
    }
    this._head = 0x20;
    this.Link(0x7F, 0x00);
    this.Link(0x1F, 0xA0);
    this.Link(0xDF, 0x80);
    this.Link(0x9F, 0xE0);
    this.Link(0xFF, 0x20);
  }

  private void Link(byte from, byte to) {
    this._prev[from] = to;
    this._next[to] = from;
  }

  /// <summary>Returns the byte <paramref name="count"/> steps back from the most recent one.</summary>
  public byte Find(int count) {
    var code = this._head;
    if (count < 128)
      for (var i = 0; i < count; ++i)
        code = this._prev[code];
    else
      for (var i = 0; i < 256 - count; ++i)
        code = this._next[code];
    return code;
  }

  /// <summary>Moves <paramref name="b"/> to the front.</summary>
  public void Update(byte b) {
    if (this._head == b)
      return;
    this._next[this._prev[b]] = this._next[b];
    this._prev[this._next[b]] = this._prev[b];
    var oldHead = this._head;
    this._prev[b] = oldHead;
    this._next[b] = this._next[oldHead];
    this._prev[this._next[oldHead]] = b;
    this._next[oldHead] = b;
    this._head = b;
  }
}
