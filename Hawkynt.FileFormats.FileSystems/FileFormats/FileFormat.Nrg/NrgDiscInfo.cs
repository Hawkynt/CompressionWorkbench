namespace FileFormat.Nrg;

/// <summary>Describes one logical NRG session reconstructed from DAO/CUE or ETN metadata.</summary>
public sealed record NrgSessionInfo {
  /// <summary>Gets the one-based session number in physical disc order.</summary>
  public required int SessionNumber { get; init; }

  /// <summary>Gets the optional lead-in LBA from CUE metadata.</summary>
  public int? LeadInLba { get; init; }

  /// <summary>Gets the optional lead-out LBA from CUE/ETN metadata.</summary>
  public int? LeadOutLba { get; init; }

  /// <summary>Gets the optional media catalog number (MCN/EAN-13).</summary>
  public string? Mcn { get; init; }

  /// <summary>Gets the tracks in this session, in physical order.</summary>
  public required IReadOnlyList<NrgTrackInfo> Tracks { get; init; }
}

/// <summary>Describes one NRG track and the exact byte range backing it in the container.</summary>
public sealed record NrgTrackInfo {
  /// <summary>Gets the containing one-based session number.</summary>
  public required int SessionNumber { get; init; }

  /// <summary>Gets the globally numbered Compact Disc track number.</summary>
  public required int TrackNumber { get; init; }

  /// <summary>Gets the raw NRG mode code.</summary>
  public required byte ModeCode { get; init; }

  /// <summary>Gets the mode code as <see cref="NrgTrackMode"/> when it is one of the modes understood by the writer.</summary>
  public NrgTrackMode Mode => (NrgTrackMode)this.ModeCode;

  /// <summary>Gets the stored bytes per sector.</summary>
  public required int SectorSize { get; init; }

  /// <summary>Gets the offset of the 2,048-byte user-data area within each stored sector when one is known.</summary>
  public required int UserDataOffset { get; init; }

  /// <summary>Gets the first byte of the stored pregap/index-0 area.</summary>
  public required long PregapOffset { get; init; }

  /// <summary>Gets the first byte of index 1 / program data.</summary>
  public required long DataOffset { get; init; }

  /// <summary>Gets the exclusive end byte of this track.</summary>
  public required long EndOffset { get; init; }

  /// <summary>Gets the index-0 LBA when present in CUE/ETN metadata.</summary>
  public int? Index0Lba { get; init; }

  /// <summary>Gets the index-1 LBA when present in CUE/ETN metadata.</summary>
  public int? Index1Lba { get; init; }

  /// <summary>Gets the ADR/control byte from CUE metadata when available.</summary>
  public byte? AdrCtl { get; init; }

  /// <summary>Gets the optional 12-character ISRC.</summary>
  public string? Isrc { get; init; }

  /// <summary>Gets whether this track contains CD-DA audio sectors.</summary>
  public bool IsAudio => this.ModeCode is (byte)NrgTrackMode.Audio or (byte)NrgTrackMode.AudioWithSubchannel;

  /// <summary>Gets whether the stored sectors include the 96-byte subchannel area.</summary>
  public bool HasSubchannel => this.ModeCode is
    (byte)NrgTrackMode.Mode1RawWithSubchannel or
    (byte)NrgTrackMode.AudioWithSubchannel or
    (byte)NrgTrackMode.Mode2RawWithSubchannel;

  /// <summary>Gets the number of bytes in the stored pregap.</summary>
  public long PregapLength => Math.Max(0, this.DataOffset - this.PregapOffset);

  /// <summary>Gets the exact number of bytes occupied by index 1/program data.</summary>
  public long StoredLength => Math.Max(0, this.EndOffset - this.DataOffset);

  /// <summary>Gets the number of complete stored sectors in index 1/program data.</summary>
  public long SectorCount => this.SectorSize > 0 ? this.StoredLength / this.SectorSize : 0;

  /// <summary>Gets whether the reader found an ISO 9660 primary volume descriptor on this track.</summary>
  public bool HasIso9660 { get; internal set; }
}
