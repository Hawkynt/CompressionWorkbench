namespace FileFormat.Pbp;

internal static class PbpConstants {
  /// <summary>4-byte PBP magic: 0x00 'P' 'B' 'P'.</summary>
  internal static readonly byte[] Magic = [0x00, 0x50, 0x42, 0x50];

  /// <summary>Default version written on Create (1.0).</summary>
  internal const uint DefaultVersion = 0x00010000u;

  /// <summary>Header is 4 bytes magic + 4 bytes version + 8*4 bytes section offsets = 40 bytes.</summary>
  internal const int HeaderSize = 40;

  /// <summary>Number of fixed sections in a PBP file.</summary>
  internal const int SectionCount = 8;

  /// <summary>Fixed section names in the order their offsets appear in the header.</summary>
  internal static readonly string[] SectionNames = [
    "PARAM.SFO",
    "ICON0.PNG",
    "ICON1.PMF",
    "PIC0.PNG",
    "PIC1.PNG",
    "SND0.AT3",
    "DATA.PSP",
    "DATA.PSAR",
  ];
}
