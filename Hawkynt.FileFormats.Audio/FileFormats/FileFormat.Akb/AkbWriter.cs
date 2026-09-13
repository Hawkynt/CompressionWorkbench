using System.Buffers.Binary;

namespace FileFormat.Akb;

/// <summary>Writes real Square Enix classic AKB and AKB2 containers.</summary>
public sealed class AkbWriter : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<AkbWriteEntry> _entries = [];
  private bool _written;

  public AkbWriter(Stream stream, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanWrite)
      throw new ArgumentException("AKB output stream must be writable.", nameof(stream));
    this._stream = stream;
    this._leaveOpen = leaveOpen;
  }

  public AkbContainerKind ContainerKind { get; set; } = AkbContainerKind.Akb2;
  public byte ClassicVersion { get; set; } = 2;
  public bool Encrypt { get; set; }

  // Compatibility properties retained from the original private writer surface.
  public uint SampleRate { get; set; } = 44_100;
  public byte ChannelMode { get; set; } = 2;
  public uint LoopStart { get; set; }
  public uint LoopEnd { get; set; }
  public byte VersionByte { get => this.ClassicVersion; set => this.ClassicVersion = value; }

  public IReadOnlyList<AkbWriteEntry> Entries => this._entries;

  /// <summary>
  /// Compatibility entry adder. Complete Ogg/M4A streams are detected by signature;
  /// all other bytes are treated as PCM16LE. New code should use <see cref="AddEncodedEntry"/>.
  /// </summary>
  public void AddEntry(string name, byte[] data, uint sampleCount = 0, uint flags = 0) {
    ArgumentNullException.ThrowIfNull(data);
    var codec = LooksLikeOgg(data) ? AkbCodec.OggVorbis
      : LooksLikeM4a(data) ? AkbCodec.M4aAac
      : AkbCodec.Pcm16Le;
    this.AddEncodedEntry(new AkbWriteEntry(
      name, data, codec, checked((int)this.SampleRate), this.ChannelMode,
      sampleCount, this.LoopStart, this.LoopEnd, Flags: flags));
  }

  public void AddEncodedEntry(AkbWriteEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (this._written)
      throw new InvalidOperationException("AKB writer has already emitted its container.");
    ValidateEntry(entry, this.ContainerKind, this.ClassicVersion, this.Encrypt);
    this._entries.Add(entry);
  }

  /// <summary>Serializes all queued materials to the target stream.</summary>
  public void Write() {
    if (this._written) return;
    if (this._entries.Count == 0)
      throw new InvalidOperationException("Cannot write an AKB without an audio material.");

    var bytes = this.ContainerKind switch {
      AkbContainerKind.Classic => this.BuildClassic(),
      AkbContainerKind.Akb2 => this.BuildAkb2(),
      _ => throw new ArgumentOutOfRangeException(),
    };

    if (this._stream.CanSeek) {
      this._stream.Position = 0;
      this._stream.SetLength(0);
    }
    this._stream.Write(bytes);
    this._written = true;
  }

  private byte[] BuildClassic() {
    if (this._entries.Count != 1)
      throw new NotSupportedException("Classic AKB stores exactly one material; use AKB2 for multiple materials.");

    var entry = this._entries[0];
    ValidateEntry(entry, AkbContainerKind.Classic, this.ClassicVersion, this.Encrypt);
    var extra = BuildExtra(entry, classic: true);
    var total = checked(AkbConstants.ClassicHeaderSize + extra.Length + entry.Data.Length);
    var result = new byte[total];
    var span = result.AsSpan();

    AkbConstants.ClassicMagic.CopyTo(span);
    span[0x04] = this.ClassicVersion;
    span[0x05] = 1; // observed sdlib convention; unused by vgmstream
    AkbConstants.WriteUInt16(span, 0x06, AkbConstants.ClassicHeaderSize);
    AkbConstants.WriteUInt32(span, 0x08, total);
    span[0x0C] = (byte)entry.Codec;
    span[0x0D] = checked((byte)entry.Channels);
    AkbConstants.WriteUInt16(span, 0x0E, entry.SampleRate);
    AkbConstants.WriteInt32(span, 0x10, entry.SampleCount);
    AkbConstants.WriteInt32(span, 0x14, entry.LoopStart);
    AkbConstants.WriteInt32(span, 0x18, entry.LoopEnd);
    AkbConstants.WriteUInt16(span, 0x1C, extra.Length);
    AkbConstants.WriteUInt16(span, 0x28, 0); // no sub-header

    var flags = checked((byte)(entry.Flags & 0xFF));
    if (this.Encrypt) flags |= AkbConstants.FlagEncrypted;
    span[0x2B] = flags;

    extra.AsSpan().CopyTo(span[AkbConstants.ClassicHeaderSize..]);
    var payloadOffset = AkbConstants.ClassicHeaderSize + extra.Length;
    var payload = entry.Data.AsSpan();
    payload.CopyTo(span[payloadOffset..]);
    if (this.Encrypt)
      AkbConstants.TransformPayload(span.Slice(payloadOffset, entry.Data.Length));
    return result;
  }

  private byte[] BuildAkb2() {
    if (this._entries.Count > byte.MaxValue)
      throw new NotSupportedException("AKB2 sound-table count is one byte; at most 255 materials can be written.");
    foreach (var entry in this._entries)
      ValidateEntry(entry, AkbContainerKind.Akb2, this.ClassicVersion, encrypt: false);

    var count = this._entries.Count;
    var recordsEnd = checked(AkbConstants.Akb2TableOffset + AkbConstants.Akb2TableSize + count * AkbConstants.Akb2EntrySize);
    var cursor = AkbConstants.Align16(checked(recordsEnd + AkbConstants.Akb2PostEntryTrailerSize));
    var materialOffsets = new int[count];
    var extras = new byte[count][];
    for (var i = 0; i < count; ++i) {
      var entry = this._entries[i];
      extras[i] = BuildExtra(entry, classic: false);
      materialOffsets[i] = cursor;
      cursor = checked(cursor + AkbConstants.Akb2MaterialSize + extras[i].Length + entry.Data.Length);
      if (i + 1 < count)
        cursor = AkbConstants.Align16(cursor);
    }

    var result = new byte[cursor];
    var span = result.AsSpan();
    AkbConstants.Akb2Magic.CopyTo(span);

    // Canonical table/header constants used by Square Enix's FFIX AKB2 layout and Memoria.
    BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0x04, 4), 0x00100000);
    AkbConstants.WriteUInt32(span, 0x08, result.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0x0C, 4), 0x00000001);
    BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0x10, 4), 0x00100000);
    BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0x14, 4), AkbConstants.Akb2TableOffset);

    var table = AkbConstants.Akb2TableOffset;
    BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(table + 0x00, 4), 0x00300101);
    BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(table + 0x04, 4), 0x3F800000);
    BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(table + 0x0C, 4), 0x0080007C | ((uint)count << 24));
    BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(table + 0x10, 4), 0x000000FF);
    BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(table + 0x14, 4), 0x00000001);
    BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(table + 0x1C, 4), 0x41F00000);

    var recordBase = table + AkbConstants.Akb2TableSize;
    for (var i = 0; i < count; ++i) {
      var record = recordBase + i * AkbConstants.Akb2EntrySize;
      BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(record + 0x00, 4), 0x00100000);
      AkbConstants.WriteUInt32(span, record + 0x04, materialOffsets[i] - table);
    }

    // The canonical single-material layout places these 0x80 bytes at 0x60..0xDF.
    // Moving them after the N entry records keeps the same table/material relationship for N > 1.
    var trailer = recordBase + count * AkbConstants.Akb2EntrySize;
    BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(trailer + 0x00, 4), 0x00000002);
    for (var section = 0; section < 4; ++section)
      BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(trailer + 0x1C + section * 0x18, 4), 0x00004040);

    for (var i = 0; i < count; ++i) {
      var entry = this._entries[i];
      var material = materialOffsets[i];
      var extra = extras[i];
      var flags = checked((byte)(entry.Flags & 0x07));

      AkbConstants.WriteUInt16(span, material + 0x00, (byte)entry.Codec << 8);
      AkbConstants.WriteUInt16(span, material + 0x02, entry.Channels | (flags << 8));
      AkbConstants.WriteUInt16(span, material + 0x04, AkbConstants.Akb2MaterialSize);
      AkbConstants.WriteUInt16(span, material + 0x06, entry.SampleRate);
      AkbConstants.WriteUInt32(span, material + 0x08, entry.Data.Length);
      AkbConstants.WriteInt32(span, material + 0x0C, entry.SampleCount);
      AkbConstants.WriteInt32(span, material + 0x10, entry.LoopStart);
      AkbConstants.WriteInt32(span, material + 0x14, entry.LoopEnd);
      AkbConstants.WriteUInt32(span, material + 0x18, extra.Length);
      AkbConstants.WriteInt32(span, material + 0x1C, entry.AlternateLoopStart);
      AkbConstants.WriteInt32(span, material + 0x20, entry.AlternateLoopEnd);
      for (var f = 0; f < 4; ++f)
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(material + 0x26 + f * 4, 4), 0x3F800000);

      extra.AsSpan().CopyTo(span[(material + AkbConstants.Akb2MaterialSize)..]);
      entry.Data.AsSpan().CopyTo(span[(material + AkbConstants.Akb2MaterialSize + extra.Length)..]);
    }

    return result;
  }

  private static byte[] BuildExtra(AkbWriteEntry entry, bool classic) {
    if (entry.Codec == AkbCodec.MsAdpcm) {
      var extra = new byte[AkbConstants.MsAdpcmExtraSize];
      AkbConstants.WriteUInt16(extra, 0x02, entry.BlockAlign);
      AkbConstants.WriteInt32(extra, 0x04, entry.SampleCount);
      AkbConstants.WriteInt32(extra, 0x08, entry.LoopStart);
      AkbConstants.WriteInt32(extra, 0x0C, entry.LoopEnd);
      return extra;
    }

    // Memoria's canonical AKB2 Ogg header carries 16 zero bytes of extradata,
    // which places a single material's Ogg payload at absolute offset 0x130 (304).
    if (!classic && entry.Codec == AkbCodec.OggVorbis)
      return new byte[AkbConstants.OggExtraSize];

    return [];
  }

  internal static void ValidateEntry(AkbWriteEntry entry, AkbContainerKind kind, byte classicVersion, bool encrypt) {
    if (entry.Data is null)
      throw new ArgumentException("AKB material data cannot be null.", nameof(entry));
    if (entry.SampleRate is <= 0 or > ushort.MaxValue)
      throw new ArgumentOutOfRangeException(nameof(entry), "AKB stores sample rate as an unsigned 16-bit value.");
    if (entry.Channels is < 1 or > byte.MaxValue)
      throw new ArgumentOutOfRangeException(nameof(entry), "AKB channel count must fit one byte.");
    if (entry.LoopEnd != 0 && entry.LoopEnd <= entry.LoopStart)
      throw new ArgumentException("AKB LoopEnd must be greater than LoopStart when looping is enabled.", nameof(entry));
    if (entry.AlternateLoopEnd != 0 && entry.AlternateLoopEnd <= entry.AlternateLoopStart)
      throw new ArgumentException("AKB alternate LoopEnd must be greater than alternate LoopStart.", nameof(entry));
    if (entry.SampleCount != 0) {
      if (entry.LoopStart > entry.SampleCount || entry.LoopEnd > (entry.SampleCount == uint.MaxValue ? uint.MaxValue : entry.SampleCount + 1u))
        throw new ArgumentException("AKB loop points exceed the material sample count.", nameof(entry));
      if (entry.AlternateLoopStart > entry.SampleCount || entry.AlternateLoopEnd > (entry.SampleCount == uint.MaxValue ? uint.MaxValue : entry.SampleCount + 1u))
        throw new ArgumentException("AKB alternate loop points exceed the material sample count.", nameof(entry));
    }

    if (kind == AkbContainerKind.Classic) {
      if (classicVersion is not (0 or 2 or 3))
        throw new NotSupportedException($"Classic AKB version {classicVersion} is not one of the observed versions 0, 2 or 3.");
      if (entry.Codec == AkbCodec.Pcm16Le)
        throw new NotSupportedException("PCM16LE is documented for AKB2, not a production classic-AKB profile.");
      if (entry.Codec == AkbCodec.MsAdpcm && classicVersion == 0)
        throw new NotSupportedException("Classic-v0 has no documented MS-ADPCM extradata profile; use v2/v3 or AKB2.");
      if (entry.Codec is not (AkbCodec.MsAdpcm or AkbCodec.OggVorbis or AkbCodec.M4aAac))
        throw new NotSupportedException($"Codec {entry.Codec} is not supported by classic AKB.");
    } else {
      if (entry.Codec is not (AkbCodec.Pcm16Le or AkbCodec.MsAdpcm or AkbCodec.OggVorbis))
        throw new NotSupportedException($"Codec {entry.Codec} is not supported by AKB2.");
      if (encrypt)
        throw new NotSupportedException("AKB2 encryption is not documented/observed.");
    }

    if (entry.Codec == AkbCodec.Pcm16Le && entry.Data.Length % checked(entry.Channels * 2) != 0)
      throw new ArgumentException("AKB2 PCM16LE payload must contain an integral number of interleaved frames.", nameof(entry));

    if (entry.Codec == AkbCodec.MsAdpcm) {
      var minimum = checked(7 * entry.Channels);
      if (entry.Channels is not (1 or 2))
        throw new NotSupportedException("The managed Microsoft ADPCM codec supports mono or stereo.");
      if (entry.BlockAlign < minimum)
        throw new ArgumentException($"MS-ADPCM block align {entry.BlockAlign} is smaller than the {minimum}-byte channel header.", nameof(entry));
      if (entry.Data.Length % entry.BlockAlign != 0)
        throw new ArgumentException("AKB MS-ADPCM payload must consist of complete blocks.", nameof(entry));
    }

    if (encrypt && (kind != AkbContainerKind.Classic || classicVersion < 3 || entry.Codec != AkbCodec.OggVorbis))
      throw new NotSupportedException("Known sdlib AKB XOR encryption is limited to classic-v3 Ogg Vorbis.");
  }

  private static bool LooksLikeOgg(ReadOnlySpan<byte> data)
    => data.Length >= 4 && data[..4].SequenceEqual("OggS"u8);

  private static bool LooksLikeM4a(ReadOnlySpan<byte> data)
    => data.Length >= 12 && data.Slice(4, 4).SequenceEqual("ftyp"u8);

  public void Dispose() {
    try {
      if (!this._written && this._entries.Count > 0)
        this.Write();
    } finally {
      if (!this._leaveOpen)
        this._stream.Dispose();
    }
  }
}
