#pragma warning disable CS1591

namespace Codec.Musepack;

/// <summary>
/// Builds and holds every Musepack SV7 VLC book, mirroring FFmpeg's
/// <c>mpc7_init_static</c> (<c>libavcodec/mpc7.c</c>): the SCFI, DSCF and HDR books
/// plus the seven quantiser books (each present in two contexts, <c>[i][0]</c> and
/// <c>[i][1]</c>). The quantiser symbol biases come from <c>mpc7_quant_vlc_off</c>.
/// One immutable instance is enough for all decoding.
/// </summary>
internal sealed class Mpc7VlcBooks {

  public Mpc7Vlc Scfi { get; }
  public Mpc7Vlc Dscf { get; }
  public Mpc7Vlc Hdr { get; }
  public Mpc7Vlc[][] Quant { get; } = new Mpc7Vlc[Mpc7Tables.QuantVlcTables][];

  private static readonly Mpc7VlcBooks _Instance = new();
  public static Mpc7VlcBooks Shared => _Instance;

  private Mpc7VlcBooks() {
    this.Scfi = new Mpc7Vlc(Mpc7Tables.Scfi, 0);
    this.Dscf = new Mpc7Vlc(Mpc7Tables.Dscf, -7);
    this.Hdr = new Mpc7Vlc(Mpc7Tables.Hdr, -5);

    var pos = 0; // running offset (in {symbol,length} pairs) into QuantVlcs
    for (var i = 0; i < Mpc7Tables.QuantVlcTables; ++i) {
      this.Quant[i] = new Mpc7Vlc[2];
      for (var j = 0; j < 2; ++j) {
        var size = Mpc7Tables.QuantVlcSizes[i];
        var slice = new byte[size * 2];
        Array.Copy(Mpc7Tables.QuantVlcs, pos * 2, slice, 0, size * 2);
        this.Quant[i][j] = new Mpc7Vlc(slice, Mpc7Tables.QuantVlcOff[i]);
        pos += size;
      }
    }
  }
}
