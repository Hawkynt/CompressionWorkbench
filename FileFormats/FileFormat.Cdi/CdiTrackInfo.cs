namespace FileFormat.Cdi;

/// <summary>Optical track kind stored in a DiscJuggler CDI descriptor.</summary>
public enum CdiTrackMode : byte {
  /// <summary>2352-byte CD-DA audio sectors.</summary>
  Audio = 0,
  /// <summary>Mode 1 data track.</summary>
  Mode1 = 1,
  /// <summary>Mode 2 / CD-XA data track.</summary>
  Mode2 = 2,
}

/// <summary>How DiscJuggler stored each sector of a track in the CDI body.</summary>
public enum CdiReadMode : uint {
  /// <summary>Cooked Mode 1, 2048 bytes per sector.</summary>
  Mode1_2048 = 0,
  /// <summary>Cooked Mode 2, 2336 bytes per sector.</summary>
  Mode2_2336 = 1,
  /// <summary>Raw 2352-byte sector.</summary>
  Raw2352 = 2,
  /// <summary>Raw 2352-byte sector followed by 16 bytes of P/Q subchannel data.</summary>
  Raw2352_Q16 = 3,
  /// <summary>Raw 2352-byte sector followed by 96 bytes of P-W subchannel data.</summary>
  Raw2352_Pw96 = 4,
}

/// <summary>
/// One physical track described by a DiscJuggler CDI session table. File offsets
/// refer to the CDI body before the trailing descriptor. <see cref="FileOffset"/>
/// points at index 0/pregap; <see cref="DataOffset"/> points at index 1, the first
/// post-pregap sector.
/// </summary>
public sealed record CdiTrackInfo(
  int SessionNumber,
  int TrackNumber,
  CdiTrackMode Mode,
  CdiReadMode ReadMode,
  int PregapSectors,
  int SectorCount,
  int DataSectorCount,
  int StartLba,
  int StoredSectorSize,
  int Control,
  long FileOffset,
  long DataOffset
) {
  /// <summary>Whether this is a filesystem/data track rather than CD-DA audio.</summary>
  public bool IsData => this.Mode is not CdiTrackMode.Audio;

  /// <summary>Disc LBA immediately after this track's post-pregap data.</summary>
  public long EndLbaExclusive => (long)this.StartLba + this.DataSectorCount;
}
