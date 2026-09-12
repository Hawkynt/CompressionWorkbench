#pragma warning disable CS1591
using System.Text;

namespace FileFormat.Oma;

/// <summary>
/// Parsed layout of a Sony OpenMG (.oma / .aa3 / .at3) file. The file starts with an
/// ID3v2-style tag whose identifier is "ea3" (rather than "ID3"); after that tag comes a 96-byte
/// binary "EA3" header that carries the codec id and coding parameters; the coded audio payload
/// follows. This parser reads the syncsafe tag size, extracts a few common text frames, then
/// reads the EA3 header's codec id and packed coding parameters.
/// </summary>
public sealed record OmaHeader(
  int TagSize,
  int Ea3HeaderOffset,
  int PayloadOffset,
  int CodecId,
  string CodecName,
  int CodingParams,
  int SampleRate,
  IReadOnlyList<(string Frame, string Value)> Tags) {

  /// <summary>The ID3v2-style tag identifier used by OpenMG ("ea3").</summary>
  public static readonly byte[] TagMagic = "ea3"u8.ToArray();

  /// <summary>The binary header identifier ("EA3").</summary>
  public static readonly byte[] HeaderMagic = "EA3"u8.ToArray();

  private const int Ea3HeaderLength = 96;

  /// <summary>ATRAC3 sample-rate table indexed by the rate field of the coding parameters.</summary>
  private static readonly int[] Atrac3SampleRates = [32000, 44100, 48000, 88200, 96000, 0, 0, 0];

  /// <summary>Codec id (EA3 header byte 32) → human-readable codec name.</summary>
  public static string CodecName_(int id) => id switch {
    0 => "ATRAC3",
    1 => "ATRAC3plus",
    3 => "MP3",
    4 => "LPCM",
    5 => "WMA",
    6 => "AAC",
    _ => $"unknown ({id})",
  };

  /// <summary>
  /// Parses the OpenMG layout from <paramref name="data"/>, or returns <see langword="null"/>
  /// if the leading "ea3" tag or the post-tag "EA3" header is missing / truncated.
  /// </summary>
  public static OmaHeader? TryParse(ReadOnlySpan<byte> data) {
    if (data.Length < 10 || data[0] != (byte)'e' || data[1] != (byte)'a' || data[2] != (byte)'3')
      return null;

    // ID3v2-style 10-byte tag header: id(3) ver(2) flags(1) syncsafe-size(4).
    var tagSize = (data[6] << 21) | (data[7] << 14) | (data[8] << 7) | data[9];
    var ea3Offset = 10 + tagSize;
    if (ea3Offset + Ea3HeaderLength > data.Length)
      return null;

    // Parse a few common text frames from the tag body (ID3v2.3/2.4 frame layout).
    var tags = ParseTextFrames(data.Slice(10, tagSize));

    // The binary "EA3" header follows the tag.
    if (data[ea3Offset] != (byte)'E' || data[ea3Offset + 1] != (byte)'A' || data[ea3Offset + 2] != (byte)'3')
      return null;

    var codecId = data[ea3Offset + 32];
    var codingParams = (data[ea3Offset + 33] << 16) | (data[ea3Offset + 34] << 8) | data[ea3Offset + 35];

    var sampleRate = 0;
    if (codecId is 0 or 1) {
      var rateIndex = (codingParams >> 13) & 0x7;
      sampleRate = rateIndex < Atrac3SampleRates.Length ? Atrac3SampleRates[rateIndex] : 0;
    }

    return new OmaHeader(
      TagSize: tagSize,
      Ea3HeaderOffset: ea3Offset,
      PayloadOffset: ea3Offset + Ea3HeaderLength,
      CodecId: codecId,
      CodecName: CodecName_(codecId),
      CodingParams: codingParams,
      SampleRate: sampleRate,
      Tags: tags);
  }

  private static List<(string, string)> ParseTextFrames(ReadOnlySpan<byte> body) {
    var result = new List<(string, string)>();
    var pos = 0;
    // Iterate ID3v2 frames: id(4) size(4, big-endian) flags(2) then payload.
    while (pos + 10 <= body.Length) {
      var id = Encoding.ASCII.GetString(body.Slice(pos, 4));
      if (id[0] is < 'A' or > 'Z' && !char.IsDigit(id[0]))
        break; // padding / end of frames
      var size = (body[pos + 4] << 24) | (body[pos + 5] << 16) | (body[pos + 6] << 8) | body[pos + 7];
      pos += 10;
      if (size <= 0 || pos + size > body.Length)
        break;

      if (id is "TIT2" or "TPE1" or "TALB" or "TCON" or "TYER") {
        // Text frames start with a 1-byte encoding marker; we read the rest as Latin-1/UTF-8.
        var text = DecodeText(body.Slice(pos, size));
        result.Add((id, text));
      }
      pos += size;
    }
    return result;
  }

  private static string DecodeText(ReadOnlySpan<byte> frame) {
    if (frame.Length == 0)
      return "";
    var encoding = frame[0];
    var payload = frame.Slice(1);
    var text = encoding switch {
      1 or 2 => Encoding.Unicode.GetString(payload),    // UTF-16
      3 => Encoding.UTF8.GetString(payload),
      _ => Encoding.Latin1.GetString(payload),
    };
    return text.TrimEnd('\0').Trim();
  }
}
