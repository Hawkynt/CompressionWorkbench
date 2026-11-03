using System.Runtime.InteropServices;

namespace FileFormat.Dar;

/// <summary>
/// dar's "CRC" (<c>docs/DAR-ON-DISK.md</c>, section 2.3) over data that arrives in pieces: every
/// byte XOR-ed into a ring of <c>width</c> bytes. Bytes are folded eight at a time into an
/// accumulator whose length is a multiple of both eight and the width, so the position modulo the
/// width is kept and a multi-gigabyte input costs a fraction of a byte-at-a-time loop.
/// </summary>
internal sealed class DarChecksum {
  private readonly int _width;
  private readonly byte[] _accumulator;

  public DarChecksum(int width) {
    ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
    this._width = width;
    var gcd = width;
    for (var b = 8; b != 0;)
      (gcd, b) = (b, gcd % b);
    this._accumulator = new byte[width / gcd * 8];
  }

  /// <summary>Bytes fed so far.</summary>
  public long Length { get; private set; }

  public void Update(ReadOnlySpan<byte> data) {
    var acc = this._accumulator;
    var at = (int)(this.Length % acc.Length);
    this.Length += data.Length;
    // Head: byte by byte up to the accumulator boundary.
    while (at != 0 && !data.IsEmpty) {
      acc[at] ^= data[0];
      data = data[1..];
      if (++at == acc.Length)
        at = 0;
    }
    // Body: whole accumulator lengths, eight bytes at a time.
    var lanes = MemoryMarshal.Cast<byte, ulong>(acc.AsSpan());
    while (data.Length >= acc.Length) {
      var words = MemoryMarshal.Cast<byte, ulong>(data[..acc.Length]);
      for (var i = 0; i < words.Length; ++i)
        lanes[i] ^= words[i];
      data = data[acc.Length..];
    }
    // Tail.
    for (var i = 0; i < data.Length; ++i)
      acc[i] ^= data[i];
  }

  /// <summary>The ring as dar stores it.</summary>
  public byte[] Value {
    get {
      var ring = new byte[this._width];
      for (var i = 0; i < this._accumulator.Length; ++i)
        ring[i % this._width] ^= this._accumulator[i];
      return ring;
    }
  }
}
