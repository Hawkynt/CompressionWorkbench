namespace Compression.Core.Deflate;

internal sealed partial class DeflateEncoder {
  /// <summary>The run-length-coded tree description of a dynamic block (RFC 1951 §3.2.7).</summary>
  private sealed class DynamicHeader {
    private readonly int _hlit, _hdist, _hclen;
    private readonly int[] _clLengths;
    private readonly ushort[] _clCodes;
    private readonly List<DeflateCodeLengthRuns.Run> _runs;

    public long Bits { get; }

    private DynamicHeader(int hlit, int hdist, int hclen, int[] clLengths, List<DeflateCodeLengthRuns.Run> runs, long bits) {
      this._hlit = hlit;
      this._hdist = hdist;
      this._hclen = hclen;
      this._clLengths = clLengths;
      this._clCodes = BuildCodes(clLengths);
      this._runs = runs;
      this.Bits = bits;
    }

    public static DynamicHeader Create(int[] litLengths, int[] distLengths) {
      var (hlit, hdist) = ZopfliBlockCost.TrimTrees(litLengths, distLengths);
      var combined = new int[hlit + hdist];
      litLengths.AsSpan(0, hlit).CopyTo(combined);
      distLengths.AsSpan(0, hdist).CopyTo(combined.AsSpan(hlit));

      var runs = DeflateCodeLengthRuns.Encode(combined);
      var clFreq = new long[DeflateConstants.CodeLengthAlphabetSize];
      foreach (var run in runs)
        ++clFreq[run.Symbol];

      var clLengths = ZopfliBlockCost.BuildCodeLengths(clFreq, DeflateConstants.MaxCodeLengthBits);
      var order = DeflateConstants.CodeLengthOrder;
      var hclen = DeflateConstants.CodeLengthAlphabetSize;
      while (hclen > 4 && clLengths[order[hclen - 1]] == 0)
        --hclen;

      long bits = 5 + 5 + 4 + 3L * hclen;
      foreach (var run in runs)
        bits += clLengths[run.Symbol] + run.ExtraBits;

      return new(hlit, hdist, hclen, clLengths, runs, bits);
    }

    public void Write(DeflateEncoder encoder) {
      encoder.PutBits((uint)(this._hlit - 257) | ((uint)(this._hdist - 1) << 5) | ((uint)(this._hclen - 4) << 10), 14);
      var order = DeflateConstants.CodeLengthOrder;
      for (var i = 0; i < this._hclen; ++i)
        encoder.PutBits((uint)this._clLengths[order[i]], 3);

      foreach (var run in this._runs) {
        encoder.PutBits(this._clCodes[run.Symbol], this._clLengths[run.Symbol]);
        if (run.ExtraBits > 0)
          encoder.PutBits((uint)run.ExtraValue, run.ExtraBits);
      }
    }
  }
}
