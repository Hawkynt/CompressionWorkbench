using System.Buffers.Binary;

namespace FileFormat.Nbi;

/// <summary>
/// Parses a Net Boot Image (<c>.nbi</c>) container. The first 512-byte sector is
/// the loader header: a 16-byte image header (magic <c>0x1B031336</c> little-endian,
/// a flags word whose low byte is the header length in 16-byte blocks, a 4-byte
/// load location and a 4-byte exec address) followed by one or more 16-byte segment
/// descriptors (length, tag, reserved, flags, load address, image length, memory
/// length). Segment payloads are concatenated starting at byte 512.
///
/// <para>Best-effort: any parsing shortfall degrades to a raw payload view rather
/// than throwing, matching the format's thin public specification.</para>
/// </summary>
public sealed class NbiReader {
  /// <summary>Header sector size; segment data begins here.</summary>
  public const int HeaderSectorSize = 512;

  /// <summary>NBI magic value (stored little-endian: 36 13 03 1B).</summary>
  public const uint Magic = 0x1B031336;

  /// <summary>A single segment descriptor decoded from the loader header.</summary>
  /// <param name="Flags">Segment flags (bit 2 marks the last segment).</param>
  /// <param name="LoadAddress">Target load address.</param>
  /// <param name="ImageLength">Bytes of payload for this segment.</param>
  /// <param name="MemoryLength">Bytes reserved in memory (>= ImageLength).</param>
  /// <param name="DataOffset">Absolute offset of this segment's bytes in the file.</param>
  public readonly record struct Segment(
    byte Flags, uint LoadAddress, uint ImageLength, uint MemoryLength, long DataOffset);

  /// <summary>True when the buffer starts with the NBI magic.</summary>
  public bool IsValid { get; }

  /// <summary>Flags word from the image header.</summary>
  public uint Flags { get; }

  /// <summary>Header length in 16-byte blocks (low byte of the flags word).</summary>
  public int HeaderBlocks { get; }

  /// <summary>Load location (offset:segment) as stored.</summary>
  public uint Location { get; }

  /// <summary>Execution address as stored.</summary>
  public uint ExecAddress { get; }

  /// <summary>Parsed segment descriptors (may be empty on malformed input).</summary>
  public IReadOnlyList<Segment> Segments { get; }

  /// <summary>True when every declared segment's bytes lie within the file.</summary>
  public bool SegmentsComplete { get; }

  /// <summary>Bytes from <see cref="HeaderSectorSize"/> to end (the raw payload region).</summary>
  public long PayloadLength { get; }

  /// <summary>Returns true when <paramref name="data"/> begins with the NBI magic.</summary>
  public static bool HasMagic(ReadOnlySpan<byte> data)
    => data.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(data) == Magic;

  /// <summary>Parses the loader header of an in-memory NBI image.</summary>
  public NbiReader(ReadOnlySpan<byte> data) {
    var segments = new List<Segment>();
    this.Segments = segments;

    if (!HasMagic(data)) {
      this.IsValid = false;
      this.SegmentsComplete = false;
      this.PayloadLength = 0;
      return;
    }

    this.IsValid = true;
    this.Flags = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
    this.HeaderBlocks = (int)(this.Flags & 0xFF);
    if (data.Length >= 12) this.Location = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
    if (data.Length >= 16) this.ExecAddress = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
    this.PayloadLength = data.Length > HeaderSectorSize ? data.Length - HeaderSectorSize : 0;

    // Segment descriptors start immediately after the 16-byte image header and
    // are capped by the header block count (or the 512-byte sector, whichever
    // is smaller). Each descriptor is 16 bytes.
    var headerBytes = this.HeaderBlocks > 0
      ? Math.Min(this.HeaderBlocks * 16, HeaderSectorSize)
      : HeaderSectorSize;
    var complete = true;
    long dataCursor = HeaderSectorSize;
    for (var off = 16; off + 16 <= headerBytes && off + 16 <= data.Length; off += 16) {
      var seg = data.Slice(off, 16);
      var flags = seg[3];
      var loadAddr = BinaryPrimitives.ReadUInt32LittleEndian(seg[4..]);
      var imgLen = BinaryPrimitives.ReadUInt32LittleEndian(seg[8..]);
      var memLen = BinaryPrimitives.ReadUInt32LittleEndian(seg[12..]);

      // A wholly zero descriptor terminates the (padded) list.
      if (flags == 0 && loadAddr == 0 && imgLen == 0 && memLen == 0)
        break;

      var segDataOffset = dataCursor;
      if (segDataOffset + imgLen > data.Length)
        complete = false;
      segments.Add(new Segment(flags, loadAddr, imgLen, memLen, segDataOffset));
      dataCursor += imgLen;

      // Flags bit 2 (0x04) marks the final segment in the standard layout.
      if ((flags & 0x04) != 0)
        break;
    }

    this.SegmentsComplete = complete && segments.Count > 0;
  }
}
