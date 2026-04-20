#pragma warning disable CS1591
using System.Text;

namespace FileFormat.PackDisk;

/// <summary>
/// Writes PackDisk-family Amiga disk archives (PackDisk/xMash/xDisk/GDC/DCS/MDC).
/// All share the same shape: a 4-byte format magic, 4 bytes of flags, then a
/// sequence of tracks. WORM creation always emits *stored* tracks (raw
/// 5632-byte sectors) -- the reader treats these as uncompressed because no
/// "XPKF" chunk header precedes them. No XPK encoder is needed.
/// </summary>
public sealed class PackDiskWriter {
  public const int TrackSize = 11 * 512;

  /// <summary>Format magic. Use one of the 4-byte ASCII codes the reader recognises.</summary>
  /// <param name="magic">"PDSK", "XMSH", "XDSK", "GDC\0", "DCS\0", or "MDC\0".</param>
  public PackDiskWriter(string magic) {
    ArgumentNullException.ThrowIfNull(magic);
    if (magic.Length != 4)
      throw new ArgumentException("Magic must be exactly 4 ASCII characters.", nameof(magic));
    _magic = Encoding.ASCII.GetBytes(magic);
    if (_magic.Length != 4)
      throw new ArgumentException("Magic must encode to exactly 4 bytes.", nameof(magic));
  }

  private readonly byte[] _magic;
  private readonly List<byte[]> _tracks = [];

  public void AddTrack(ReadOnlySpan<byte> data) {
    var buf = new byte[TrackSize];
    var copyLen = Math.Min(data.Length, TrackSize);
    data[..copyLen].CopyTo(buf);
    _tracks.Add(buf);
  }

  public void WriteTo(Stream output) {
    output.Write(_magic);
    Span<byte> flags = stackalloc byte[4];
    flags.Clear();
    output.Write(flags);
    foreach (var track in _tracks)
      output.Write(track);
  }
}
