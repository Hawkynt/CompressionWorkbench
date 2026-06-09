#pragma warning disable CS1591
namespace Codec.AmrWb;

/// <summary>
/// AMR wideband (G.722.2 / 3GPP TS 26.190) coding modes. Nine active ACELP modes plus the
/// comfort-noise SID, "speech lost" and NO_DATA frame types. The numeric value is the 4-bit Frame
/// Type field stored in the .amr (AMR-WB IF1/MIME) storage mode byte (bits 3..6). Matches ffmpeg
/// <c>libavcodec/amrwbdata.h</c> <c>enum Mode</c>.
/// </summary>
public enum AmrWbMode {
  /// <summary>6.60 kbit/s.</summary>
  Mr660 = 0,
  /// <summary>8.85 kbit/s.</summary>
  Mr885 = 1,
  /// <summary>12.65 kbit/s.</summary>
  Mr1265 = 2,
  /// <summary>14.25 kbit/s.</summary>
  Mr1425 = 3,
  /// <summary>15.85 kbit/s.</summary>
  Mr1585 = 4,
  /// <summary>18.25 kbit/s.</summary>
  Mr1825 = 5,
  /// <summary>19.85 kbit/s.</summary>
  Mr1985 = 6,
  /// <summary>23.05 kbit/s.</summary>
  Mr2305 = 7,
  /// <summary>23.85 kbit/s.</summary>
  Mr2385 = 8,
  /// <summary>Comfort-noise (SID) frame.</summary>
  Sid = 9,
  /// <summary>Speech lost.</summary>
  SpeechLost = 14,
  /// <summary>No data / untransmitted.</summary>
  NoData = 15,
}
