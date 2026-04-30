#pragma warning disable CS1591
namespace FileFormat.Awb;

internal static class AwbConstants {
  // ASCII "AFS2" — the magic for CRI Audio Wave Banks (despite the .awb extension).
  internal static ReadOnlySpan<byte> Magic => "AFS2"u8;

  internal const int HeaderSize = 16;

  // 32-byte alignment is the canonical CRI default; Capcom titles occasionally use larger.
  internal const uint DefaultAlignment = 0x20;

  // Version 1 with 4-byte offsets and 2-byte cue IDs is the most broadly compatible variant.
  internal const byte DefaultVersion = 0x01;
  internal const byte DefaultOffsetSize = 0x04;
  internal const byte DefaultIdSize = 0x02;
}
