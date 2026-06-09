#pragma warning disable CS1591
namespace Codec.AmrNb;

/// <summary>
/// AMR narrowband coding modes. The eight active speech modes plus the comfort-noise (SID) and
/// untransmitted frame types, matching the 3GPP TS 26.090 / ffmpeg <c>libavcodec/amrnbdata.h</c>
/// <c>enum Mode</c>. The numeric value is the 4-bit Frame Type field stored in the IF1/.amr
/// storage-format mode byte (bits 3..6).
/// </summary>
public enum AmrNbMode {
  /// <summary>4.75 kbit/s.</summary>
  Mr475 = 0,
  /// <summary>5.15 kbit/s.</summary>
  Mr515 = 1,
  /// <summary>5.90 kbit/s.</summary>
  Mr59 = 2,
  /// <summary>6.70 kbit/s.</summary>
  Mr67 = 3,
  /// <summary>7.40 kbit/s.</summary>
  Mr74 = 4,
  /// <summary>7.95 kbit/s.</summary>
  Mr795 = 5,
  /// <summary>10.2 kbit/s.</summary>
  Mr102 = 6,
  /// <summary>12.2 kbit/s.</summary>
  Mr122 = 7,
  /// <summary>Comfort noise (Silence Insertion Descriptor).</summary>
  MrdtxSid = 8,
  /// <summary>No data / untransmitted frame (speech lost or DTX no-transmission).</summary>
  NoData = 15,
}
