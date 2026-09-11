namespace FileFormat.Akb;

/// <summary>AKB container generation.</summary>
public enum AkbContainerKind {
  /// <summary>Classic <c>"AKB "</c> single-material container.</summary>
  Classic,
  /// <summary><c>"AKB2"</c> table/material container.</summary>
  Akb2,
}

/// <summary>Codec identifiers stored in Square Enix AKB material headers.</summary>
public enum AkbCodec : byte {
  /// <summary>Little-endian signed PCM16; observed in AKB2.</summary>
  Pcm16Le = 0x01,
  /// <summary>Microsoft ADPCM.</summary>
  MsAdpcm = 0x02,
  /// <summary>Complete Ogg Vorbis stream.</summary>
  OggVorbis = 0x05,
  /// <summary>Complete M4A/MP4 AAC stream; observed in classic AKB.</summary>
  M4aAac = 0x06,
}

/// <summary>One logical AKB material and its on-disk metadata.</summary>
public sealed class AkbEntry {
  public string Name { get; init; } = "";
  public long Offset { get; init; }
  public long Size { get; init; }
  public uint SampleCount { get; init; }
  public uint Flags { get; init; }
  public AkbCodec Codec { get; init; }
  public int Channels { get; init; }
  public int SampleRate { get; init; }
  public uint LoopStart { get; init; }
  public uint LoopEnd { get; init; }
  public uint AlternateLoopStart { get; init; }
  public uint AlternateLoopEnd { get; init; }
  public int BlockAlign { get; init; }
  public byte[] ExtraData { get; init; } = [];
  public byte Version { get; init; }
  public bool Encrypted => (this.Flags & AkbConstants.FlagEncrypted) != 0;
}

/// <summary>Logical material supplied to <see cref="AkbWriter"/>.</summary>
public sealed record AkbWriteEntry(
  string Name,
  byte[] Data,
  AkbCodec Codec,
  int SampleRate,
  int Channels,
  uint SampleCount = 0,
  uint LoopStart = 0,
  uint LoopEnd = 0,
  uint AlternateLoopStart = 0,
  uint AlternateLoopEnd = 0,
  int BlockAlign = 0,
  uint Flags = 0
);
