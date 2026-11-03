using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Dms;

/// <summary>
/// Reads an Amiga DMS archive: verifies the header and track CRCs, decodes every track with
/// one stateful <see cref="DmsDecoder"/> in file order, and checks each decoded track's sum.
/// </summary>
/// <remarks>
/// Layout and decoding follow libxad's DMS client (LGPL-2.1-or-later); see
/// <c>THIRD-PARTY-NOTICE.libxad.txt</c>.
/// </remarks>
public sealed class DmsReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly string? _password;
  private readonly List<DmsTrackHeader> _tracks = [];
  private Dictionary<DmsTrackHeader, byte[]>? _decoded;

  public DmsHeader Header { get; }

  /// <summary>Every track in file order, text tracks included.</summary>
  public IReadOnlyList<DmsTrackHeader> Entries => this._tracks;

  /// <summary>The disk-data tracks (cylinders), in file order.</summary>
  public IEnumerable<DmsTrackHeader> DataTracks => this._tracks.Where(t => !t.IsText);

  /// <exception cref="InvalidDataException">Not a DMS archive, or a header CRC is wrong.</exception>
  public DmsReader(Stream stream, bool leaveOpen = false, string? password = null) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    if (!stream.CanSeek)
      throw new ArgumentException("Stream must be seekable.", nameof(stream));
    this._leaveOpen = leaveOpen;
    this._password = password;

    stream.Position = 0;
    Span<byte> header = stackalloc byte[DmsConstants.FileHeaderSize];
    if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) != header.Length)
      throw new InvalidDataException("DMS: file shorter than the 56-byte header.");
    if (BinaryPrimitives.ReadUInt32BigEndian(header) != DmsConstants.MagicValue)
      throw new InvalidDataException("DMS: \"DMS!\" magic missing.");
    if (DmsCrc.Compute(header[4..54]) != BinaryPrimitives.ReadUInt16BigEndian(header[54..]))
      throw new InvalidDataException("DMS: header CRC mismatch.");
    this.Header = DmsHeader.Parse(header);

    Span<byte> th = stackalloc byte[DmsConstants.TrackHeaderSize];
    while (stream.Length - stream.Position >= DmsConstants.TrackHeaderSize) {
      stream.ReadExactly(th);
      if (BinaryPrimitives.ReadUInt16BigEndian(th) != DmsConstants.TrackSignature ||
          DmsCrc.Compute(th[..18]) != BinaryPrimitives.ReadUInt16BigEndian(th[18..]))
        break; // trailing data, or a second archive appended after this one
      var track = new DmsTrackHeader(
        BinaryPrimitives.ReadInt16BigEndian(th[2..]), BinaryPrimitives.ReadUInt16BigEndian(th[6..]),
        BinaryPrimitives.ReadUInt16BigEndian(th[8..]), BinaryPrimitives.ReadUInt16BigEndian(th[10..]),
        th[12], th[13], BinaryPrimitives.ReadUInt16BigEndian(th[14..]), BinaryPrimitives.ReadUInt16BigEndian(th[16..]),
        stream.Position);
      if (stream.Length - stream.Position < track.PackedSize)
        throw new InvalidDataException($"DMS: track {track.Number} runs past the end of the file.");
      this._tracks.Add(track);
      stream.Seek(track.PackedSize, SeekOrigin.Current);
    }
  }

  /// <summary>Decoded bytes of <paramref name="track"/> (all earlier tracks are decoded first; DMS state is sequential).</summary>
  public byte[] Extract(DmsTrackHeader track) {
    ArgumentNullException.ThrowIfNull(track);
    this._decoded ??= this.DecodeAll();
    return this._decoded[track];
  }

  /// <summary>The disk image: every data track's bytes, in file order (as xDMS writes them).</summary>
  public byte[] ExtractDisk() {
    using var ms = new MemoryStream();
    foreach (var t in this.DataTracks)
      ms.Write(this.Extract(t));
    return ms.ToArray();
  }

  /// <summary>Banner and FILE_ID.DIZ texts, keyed by a file name.</summary>
  public IEnumerable<(string Name, byte[] Data)> TextEntries() {
    var banner = 0;
    foreach (var t in this._tracks.Where(t => t.IsText)) {
      var name = t.Number == DmsConstants.TrackDiz ? "FILE_ID.DIZ" : banner++ == 0 ? "banner.txt" : $"banner{banner}.txt";
      yield return (name, this.Extract(t).AsSpan().TrimEnd((byte)0).ToArray());
    }
  }

  private Dictionary<DmsTrackHeader, byte[]> DecodeAll() {
    if (this.Header.IsEncrypted && this._password is null)
      throw new UnauthorizedAccessException("DMS: the archive is encrypted; a password is required.");
    var decoder = new DmsDecoder();
    var pass = this.Header.IsEncrypted ? DmsCrc.Compute(Encoding.Latin1.GetBytes(this._password!)) : (ushort)0;
    var result = new Dictionary<DmsTrackHeader, byte[]>(ReferenceEqualityComparer.Instance);
    foreach (var t in this._tracks) {
      this._stream.Position = t.DataOffset;
      var packed = new byte[t.PackedSize];
      this._stream.ReadExactly(packed);
      if (this.Header.IsEncrypted) {
        foreach (ref var b in packed.AsSpan()) {
          var original = b;
          b ^= (byte)pass;
          pass = (ushort)((pass >> 1) + original);
        }
      } else if (DmsCrc.Compute(packed) != t.PackedCrc)
        throw new InvalidDataException($"DMS: packed data CRC mismatch on track {t.Number}.");

      var data = decoder.DecodeTrack(packed, t);
      if (DmsCrc.Sum(data) != t.DataChecksum)
        throw this.Header.IsEncrypted
          ? new UnauthorizedAccessException($"DMS: track {t.Number} does not decode; wrong password?")
          : new InvalidDataException($"DMS: checksum mismatch on decoded track {t.Number}.");
      result[t] = data;
    }
    return result;
  }

  public void Dispose() {
    if (!this._leaveOpen)
      this._stream.Dispose();
  }
}
