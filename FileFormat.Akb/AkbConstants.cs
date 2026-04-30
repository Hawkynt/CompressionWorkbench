#pragma warning disable CS1591
namespace FileFormat.Akb;

internal static class AkbConstants {
  // The "1" is part of the 4-byte ASCII magic — AKB v1 and v2 files both use "AKB1" here;
  // the version is differentiated by the VersionByte field at offset 6.
  internal static readonly byte[] Magic = "AKB1"u8.ToArray();
  internal const int MagicLength = 4;

  // Standard 40-byte AKB header — see AkbReader/AkbWriter for field layout.
  internal const int HeaderSize = 40;

  // Per-entry table record (DataOffset, DataSize, SampleCount, Flags) — all UInt32 LE.
  internal const int EntryRecordSize = 16;

  internal const byte VersionV1 = 0x01;
  internal const byte VersionV2 = 0x02;

  internal const byte ChannelMono = 0x01;
  internal const byte ChannelStereo = 0x02;

  // Bit 0 of the per-entry Flags word marks a looping sample.
  internal const uint EntryFlagLooping = 0x00000001u;

  internal const string MetadataEntryName = "metadata.ini";
}
