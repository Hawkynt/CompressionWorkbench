using System.Buffers.Binary;

namespace FileFormat.Akb;

/// <summary>Reads Square Enix classic AKB and AKB2 audio containers.</summary>
public sealed class AkbReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly byte[] _file;

  public AkbReader(Stream stream, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(stream);
    this._stream = stream;
    this._leaveOpen = leaveOpen;
    this._file = ReadAll(stream);
    if (this._file.Length < 0x10)
      throw new InvalidDataException("AKB file is too small.");

    if (this._file.AsSpan(0, 4).SequenceEqual(AkbConstants.ClassicMagic))
      this.ParseClassic();
    else if (this._file.AsSpan(0, 4).SequenceEqual(AkbConstants.Akb2Magic))
      this.ParseAkb2();
    else
      throw new InvalidDataException("AKB signature is neither 'AKB ' nor 'AKB2'.");
  }

  public AkbContainerKind ContainerKind { get; private set; }
  public byte VersionByte { get; private set; }
  public IReadOnlyList<AkbEntry> Entries { get; private set; } = [];

  /// <summary>Sample rate of the first material, for compatibility with the original reader surface.</summary>
  public uint SampleRate => this.Entries.Count == 0 ? 0 : (uint)this.Entries[0].SampleRate;
  /// <summary>Channel count of the first material.</summary>
  public byte ChannelMode => this.Entries.Count == 0 ? (byte)0 : checked((byte)this.Entries[0].Channels);
  /// <summary>Primary loop start of the first material.</summary>
  public uint LoopStart => this.Entries.Count == 0 ? 0 : this.Entries[0].LoopStart;
  /// <summary>Primary loop end of the first material.</summary>
  public uint LoopEnd => this.Entries.Count == 0 ? 0 : this.Entries[0].LoopEnd;

  /// <summary>Returns the logical encoded payload, decrypting classic-v3 sdlib XOR where required.</summary>
  public byte[] Extract(AkbEntry entry) {
    var result = this.ExtractRaw(entry);
    if (entry.Encrypted)
      AkbConstants.TransformPayload(result);
    return result;
  }

  /// <summary>Returns the exact bytes stored in the AKB payload region.</summary>
  public byte[] ExtractRaw(AkbEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    ValidateRange(entry.Offset, entry.Size, this._file.LongLength, "AKB payload");
    return this._file.AsSpan(checked((int)entry.Offset), checked((int)entry.Size)).ToArray();
  }

  private void ParseClassic() {
    this.ContainerKind = AkbContainerKind.Classic;
    this.VersionByte = this._file[0x04];
    if (this.VersionByte is not (0 or 2 or 3))
      throw new NotSupportedException($"Unsupported classic AKB version {this.VersionByte}.");

    var headerSize = ReadU16(this._file, 0x06);
    ValidateDeclaredFileSize(this._file);
    if (headerSize < 0x1C || headerSize > this._file.Length)
      throw new InvalidDataException($"Classic AKB header size 0x{headerSize:X} is invalid.");

    var codec = ParseCodec(this._file[0x0C], classic: true);
    var channels = this._file[0x0D];
    var sampleRate = ReadU16(this._file, 0x0E);
    var samples = ToUInt(ReadI32(this._file, 0x10));
    var loopStart = ToUInt(ReadI32(this._file, 0x14));
    var loopEnd = ToUInt(ReadI32(this._file, 0x18));

    var extraSize = 0;
    var subheaderSize = 0;
    byte flags = 0;
    if (headerSize >= 0x44) {
      EnsureLength(this._file, 0x44, "classic AKB extended header");
      extraSize = ReadU16(this._file, 0x1C);
      subheaderSize = ReadU16(this._file, 0x28);
      flags = this._file[0x2B];
    }

    var extraOffset = checked(headerSize + subheaderSize);
    var dataOffset = checked(extraOffset + extraSize);
    if (dataOffset > this._file.Length)
      throw new InvalidDataException("Classic AKB payload starts beyond end of file.");

    var extra = extraSize == 0
      ? []
      : SliceChecked(this._file, extraOffset, extraSize, "classic AKB extradata");
    var blockAlign = 0;
    if (codec == AkbCodec.MsAdpcm) {
      if (extra.Length < 0x10)
        throw new InvalidDataException("Classic AKB MS-ADPCM requires 16 bytes of extradata.");
      blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(extra.AsSpan(2, 2));
      samples = ToUInt(BinaryPrimitives.ReadInt32LittleEndian(extra.AsSpan(4, 4)));
      loopStart = ToUInt(BinaryPrimitives.ReadInt32LittleEndian(extra.AsSpan(8, 4)));
      loopEnd = ToUInt(BinaryPrimitives.ReadInt32LittleEndian(extra.AsSpan(12, 4)));
      if (this.VersionByte >= 3 && (flags & AkbConstants.FlagEncrypted) != 0)
        throw new NotSupportedException("Encrypted classic MS-ADPCM is not supported by the known sdlib/vgmstream path.");
    }

    if ((flags & AkbConstants.FlagEncrypted) != 0 && (this.VersionByte < 3 || codec != AkbCodec.OggVorbis))
      throw new NotSupportedException("Known AKB encryption is limited to classic-v3 Ogg Vorbis.");

    var size = this._file.Length - dataOffset;
    this.Entries = [new AkbEntry {
      Name = $"entry_000{Extension(codec)}",
      Offset = dataOffset,
      Size = size,
      SampleCount = samples,
      Flags = flags,
      Codec = codec,
      Channels = channels,
      SampleRate = sampleRate,
      LoopStart = loopStart,
      LoopEnd = loopEnd,
      BlockAlign = blockAlign,
      ExtraData = extra,
      Version = this.VersionByte,
    }];
  }

  private void ParseAkb2() {
    this.ContainerKind = AkbContainerKind.Akb2;
    this.VersionByte = this._file[0x04];
    ValidateDeclaredFileSize(this._file);

    var headerSize = ReadU16(this._file, 0x06);
    var tableCount = this._file[0x0C];
    if (headerSize < 0x10 || tableCount is < 1 or > 2)
      throw new InvalidDataException("AKB2 header/table count is invalid.");

    var tablePointerAt = checked(headerSize + (tableCount - 1) * AkbConstants.Akb2EntrySize + 4);
    EnsureLength(this._file, tablePointerAt + 4, "AKB2 table pointer");
    var tableOffset = checked((int)ReadU32(this._file, tablePointerAt));
    EnsureLength(this._file, tableOffset + 0x10, "AKB2 sound table");
    var tableSize = ReadU16(this._file, tableOffset + 2);
    var count = this._file[tableOffset + 0x0F];
    if (tableSize < 0x10 || count == 0)
      throw new InvalidDataException("AKB2 contains no valid sound-table entries.");

    var entries = new List<AkbEntry>(count);
    for (var index = 0; index < count; ++index) {
      var recordOffset = checked(tableOffset + tableSize + index * AkbConstants.Akb2EntrySize);
      EnsureLength(this._file, recordOffset + 8, "AKB2 sound-table entry");
      var materialRelative = ReadU32(this._file, recordOffset + 4);
      var materialOffsetLong = (long)tableOffset + materialRelative;
      if (materialOffsetLong > int.MaxValue)
        throw new InvalidDataException("AKB2 material offset exceeds supported address space.");
      var materialOffset = (int)materialOffsetLong;
      EnsureLength(this._file, materialOffset + 0x1C, "AKB2 material");

      var codec = ParseCodec(this._file[materialOffset + 1], classic: false);
      var channels = this._file[materialOffset + 2];
      var flags = this._file[materialOffset + 3];
      if ((flags & AkbConstants.FlagEncrypted) != 0)
        throw new NotSupportedException("Encrypted AKB2 materials have not been observed/documented.");

      var materialSize = ReadU16(this._file, materialOffset + 4);
      var sampleRate = ReadU16(this._file, materialOffset + 6);
      var streamSize = ReadU32(this._file, materialOffset + 8);
      var samples = ToUInt(ReadI32(this._file, materialOffset + 0x0C));
      var loopStart = ToUInt(ReadI32(this._file, materialOffset + 0x10));
      var loopEnd = ToUInt(ReadI32(this._file, materialOffset + 0x14));
      var extraSize = ReadU32(this._file, materialOffset + 0x18);
      if (materialSize < 0x1C)
        throw new InvalidDataException("AKB2 material header is smaller than its fixed fields.");

      uint alternateLoopStart = 0;
      uint alternateLoopEnd = 0;
      if (materialSize >= 0x24) {
        EnsureLength(this._file, materialOffset + 0x24, "AKB2 extended material");
        alternateLoopStart = ToUInt(ReadI32(this._file, materialOffset + 0x1C));
        alternateLoopEnd = ToUInt(ReadI32(this._file, materialOffset + 0x20));
      }

      var extraOffset = checked(materialOffset + materialSize);
      if (extraSize > int.MaxValue)
        throw new InvalidDataException("AKB2 extradata is too large.");
      var extra = extraSize == 0
        ? []
        : SliceChecked(this._file, extraOffset, checked((int)extraSize), "AKB2 extradata");
      var dataOffset = checked(extraOffset + (int)extraSize);
      ValidateRange(dataOffset, streamSize, this._file.LongLength, "AKB2 payload");

      var blockAlign = 0;
      if (codec == AkbCodec.MsAdpcm) {
        if (extra.Length < 0x10)
          throw new InvalidDataException("AKB2 MS-ADPCM requires 16 bytes of extradata.");
        blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(extra.AsSpan(2, 2));
        samples = ToUInt(BinaryPrimitives.ReadInt32LittleEndian(extra.AsSpan(4, 4)));
        loopStart = ToUInt(BinaryPrimitives.ReadInt32LittleEndian(extra.AsSpan(8, 4)));
        loopEnd = ToUInt(BinaryPrimitives.ReadInt32LittleEndian(extra.AsSpan(12, 4)));
      }

      entries.Add(new AkbEntry {
        Name = $"entry_{index:D3}{Extension(codec)}",
        Offset = dataOffset,
        Size = streamSize,
        SampleCount = samples,
        Flags = flags,
        Codec = codec,
        Channels = channels,
        SampleRate = sampleRate,
        LoopStart = loopStart,
        LoopEnd = loopEnd,
        AlternateLoopStart = alternateLoopStart,
        AlternateLoopEnd = alternateLoopEnd,
        BlockAlign = blockAlign,
        ExtraData = extra,
        Version = this.VersionByte,
      });
    }

    this.Entries = entries;
  }

  private static AkbCodec ParseCodec(byte value, bool classic) => value switch {
    0x01 when !classic => AkbCodec.Pcm16Le,
    0x02 => AkbCodec.MsAdpcm,
    0x05 => AkbCodec.OggVorbis,
    0x06 when classic => AkbCodec.M4aAac,
    _ => throw new NotSupportedException($"Unsupported {(classic ? "classic AKB" : "AKB2")} codec 0x{value:X2}."),
  };

  internal static string Extension(AkbCodec codec) => codec switch {
    AkbCodec.Pcm16Le => ".pcm",
    AkbCodec.MsAdpcm => ".msadpcm",
    AkbCodec.OggVorbis => ".ogg",
    AkbCodec.M4aAac => ".m4a",
    _ => ".bin",
  };

  private static byte[] ReadAll(Stream input) {
    if (input.CanSeek) input.Position = 0;
    using var memory = new MemoryStream();
    input.CopyTo(memory);
    return memory.ToArray();
  }

  private static void ValidateDeclaredFileSize(byte[] file) {
    var declared = ReadU32(file, 0x08);
    if (declared != file.LongLength)
      throw new InvalidDataException($"AKB declared file size {declared} does not match actual size {file.LongLength}.");
  }

  private static void EnsureLength(byte[] file, int required, string what) {
    if (required < 0 || required > file.Length)
      throw new InvalidDataException($"Truncated {what}.");
  }

  private static byte[] SliceChecked(byte[] file, int offset, int count, string what) {
    ValidateRange(offset, count, file.LongLength, what);
    return file.AsSpan(offset, count).ToArray();
  }

  private static void ValidateRange(long offset, long size, long length, string what) {
    if (offset < 0 || size < 0 || offset > length || size > length - offset)
      throw new InvalidDataException($"{what} range is outside the AKB file.");
  }

  private static ushort ReadU16(byte[] file, int offset) {
    EnsureLength(file, checked(offset + 2), "AKB u16 field");
    return BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(offset, 2));
  }

  private static uint ReadU32(byte[] file, int offset) {
    EnsureLength(file, checked(offset + 4), "AKB u32 field");
    return BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(offset, 4));
  }

  private static int ReadI32(byte[] file, int offset) {
    EnsureLength(file, checked(offset + 4), "AKB i32 field");
    return BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(offset, 4));
  }

  private static uint ToUInt(int value) => value <= 0 ? 0u : checked((uint)value);

  public void Dispose() {
    if (!this._leaveOpen)
      this._stream.Dispose();
  }
}
