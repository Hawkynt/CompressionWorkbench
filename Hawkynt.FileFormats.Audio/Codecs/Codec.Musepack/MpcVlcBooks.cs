#pragma warning disable CS1591

namespace Codec.Musepack;

/// <summary>
/// Builds and holds every Musepack SV8 VLC book, advancing through the shared
/// symbol pools exactly as FFmpeg's <c>mpc8_init_static</c> walks its <c>*syms</c>
/// cursors (the symbol biases for the q3/q4 and q5..q8 books are applied here).
/// One instance is enough for all decoding; the books are immutable.
/// </summary>
internal sealed class MpcVlcBooks {

  public MpcVlc Band { get; }
  public MpcVlc Q1 { get; }
  public MpcVlc Q9Up { get; }
  public MpcVlc[] Scfi { get; } = new MpcVlc[2];
  public MpcVlc[] Dscf { get; } = new MpcVlc[2];
  public MpcVlc[] Res { get; } = new MpcVlc[2];
  public MpcVlc[] Q2 { get; } = new MpcVlc[2];
  public MpcVlc[] Q3 { get; } = new MpcVlc[2]; // index 0 → q3, index 1 → q4
  public MpcVlc[][] Quant { get; } = { new MpcVlc[2], new MpcVlc[2], new MpcVlc[2], new MpcVlc[2] }; // [res-5][cnt>thres]

  private static readonly MpcVlcBooks _Instance = new();
  public static MpcVlcBooks Shared => _Instance;

  private MpcVlcBooks() {
    // band uses the dedicated bands symbol pool.
    this.Band = new MpcVlc(MpcHuffTables.BandsLenCounts, MpcHuffTables.BandsSyms, 0, 0);

    // The q* books all draw from the shared QSyms pool in init order.
    var qPos = 0;
    this.Q1 = TakeQ(ref qPos, MpcHuffTables.Q1LenCounts, 0);
    this.Q9Up = TakeQ(ref qPos, MpcHuffTables.Q9UpLenCounts, 0);

    var scfiPos = 0;
    var dscfPos = 0;
    var resPos = 0;
    for (var i = 0; i < 2; ++i) {
      this.Scfi[i] = Take(ref scfiPos, MpcHuffTables.ScfiLenCounts[i], MpcHuffTables.ScfiSyms, 0);
      this.Dscf[i] = Take(ref dscfPos, MpcHuffTables.DscfLenCounts[i], MpcHuffTables.DscfSyms, 0);
      this.Res[i] = Take(ref resPos, MpcHuffTables.ResLenCounts[i], MpcHuffTables.ResSyms, 0);
      this.Q2[i] = TakeQ(ref qPos, MpcHuffTables.Q2LenCounts[i], 0);
      this.Q3[i] = TakeQ(ref qPos, MpcHuffTables.Q34LenCounts[i], -48 - 16 * i);
      for (var j = 0; j < 4; ++j)
        this.Quant[j][i] = TakeQ(ref qPos, MpcHuffTables.Q58LenCounts[i][j], -((8 << j) - 1));
    }
  }

  private static MpcVlc TakeQ(ref int pos, byte[] lenCounts, int offset) {
    var vlc = new MpcVlc(lenCounts, MpcHuffTables.QSyms, pos, offset);
    pos += vlc.SymbolCount;
    return vlc;
  }

  private static MpcVlc Take(ref int pos, byte[] lenCounts, byte[] syms, int offset) {
    var vlc = new MpcVlc(lenCounts, syms, pos, offset);
    pos += vlc.SymbolCount;
    return vlc;
  }
}
