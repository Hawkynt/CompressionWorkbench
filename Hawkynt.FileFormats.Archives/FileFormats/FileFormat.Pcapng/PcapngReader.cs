#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Pcapng;

/// <summary>
/// Reader for the pcapng capture format (draft-ietf-opsawg-pcapng). Walks the block stream,
/// surfaces each Enhanced Packet Block (EPB) and Simple Packet Block (SPB) payload, and records
/// per-interface metadata from the Interface Description Blocks (IDBs), including the
/// <c>if_tsresol</c> and <c>if_tsoffset</c> options that say how packet timestamps are counted.
/// </summary>
public sealed class PcapngReader {

  // Block type constants — pcapng §11.
  public const uint BtSectionHeader = 0x0A0D0D0Au;
  public const uint BtInterfaceDescription = 0x00000001u;
  public const uint BtEnhancedPacket = 0x00000006u;
  public const uint BtSimplePacket = 0x00000003u;
  public const uint ByteOrderMagic = 0x1A2B3C4Du;

  /// <summary>IDB option codes (§4.2).</summary>
  public const ushort OptEndOfOpt = 0, OptIfTsResol = 9, OptIfTsOffset = 14;

  /// <summary>
  /// An interface: its link type, snaplen, timestamp units per second (from <c>if_tsresol</c>,
  /// default 10^6) and the <c>if_tsoffset</c> seconds added to every timestamp.
  /// </summary>
  public sealed record Interface(uint LinkType, uint Snaplen, ulong UnitsPerSecond = 1_000_000, long OffsetSeconds = 0) {
    /// <summary>The raw <c>if_tsresol</c> byte, or null when the option was absent.</summary>
    public byte? TsResolOption { get; init; }

    /// <summary>Converts a raw 64-bit timestamp counted in this interface's units.</summary>
    public DateTime ToDateTime(ulong raw) {
      var seconds = raw / this.UnitsPerSecond;
      var fraction = raw % this.UnitsPerSecond;
      var ticks = (long)((UInt128)fraction * TimeSpan.TicksPerSecond / this.UnitsPerSecond);
      return DateTime.UnixEpoch.AddTicks(checked(((long)seconds + this.OffsetSeconds) * TimeSpan.TicksPerSecond + ticks));
    }

    /// <summary>Units per second named by an <c>if_tsresol</c> byte (MSB clear: 10^-n, set: 2^-n).</summary>
    public static ulong UnitsFromTsResol(byte value) {
      var exponent = value & 0x7F;
      if ((value & 0x80) != 0)
        return exponent < 64 ? 1UL << exponent : throw new InvalidDataException($"pcapng: if_tsresol 2^-{exponent} is out of range.");
      if (exponent > 19)
        throw new InvalidDataException($"pcapng: if_tsresol 10^-{exponent} is out of range.");
      var units = 1UL;
      for (var i = 0; i < exponent; ++i) units *= 10;
      return units;
    }
  }

  /// <summary>
  /// Represents a packet.
  /// </summary>
  public sealed record Packet(int InterfaceId, ulong TimestampRaw, byte[] Data, uint OriginalLength) {
    /// <summary>Decodes the timestamp with a power-of-ten resolution (6 = microseconds).</summary>
    public DateTime ToDateTime(int tsResolutionPow10 = 6) {
      var divisor = 1UL;
      for (var i = 0; i < tsResolutionPow10; i++) divisor *= 10;
      return new Interface(0, 0, divisor).ToDateTime(this.TimestampRaw);
    }
  }

  /// <summary>
  /// Represents a capture.
  /// </summary>
  public sealed record Capture(
    bool LittleEndian,
    ushort VersionMajor,
    ushort VersionMinor,
    IReadOnlyList<Interface> Interfaces,
    IReadOnlyList<Packet> Packets);

  /// <summary>A packet block: where it sits and what it holds.</summary>
  internal sealed record PacketLayout(
    int InterfaceId,
    ulong TimestampRaw,
    int DataOffset,
    int DataLength,
    uint OriginalLength,
    int BlockOffset,
    int BlockLength,
    int SectionIndex,
    Interface? Interface) {
    public DateTime? Timestamp {
      get {
        if (this.Interface is not { } i || this.TimestampRaw == 0 || i.UnitsPerSecond == 0) return null;
        try {
          return i.ToDateTime(this.TimestampRaw);
        } catch (Exception e) when (e is OverflowException or ArgumentOutOfRangeException) {
          return null; // a timestamp beyond DateTime's range lists without one
        }
      }
    }
  }

  /// <summary>A section: where its SHB sits, its declared section length (-1 = unspecified) and its interfaces.</summary>
  internal sealed record SectionLayout(int Offset, int HeaderLength, long DeclaredLength, IReadOnlyList<Interface> Interfaces);

  internal sealed record CaptureLayout(
    bool LittleEndian,
    ushort VersionMajor,
    ushort VersionMinor,
    IReadOnlyList<Interface> Interfaces,
    IReadOnlyList<PacketLayout> Packets) {
    public IReadOnlyList<SectionLayout> Sections { get; init; } = [];
    /// <summary>Offset where block walking stopped; equals the file length for a well-formed capture.</summary>
    public int EndOffset { get; init; }
  }

  /// <summary>
  /// Reads the value from the supplied input.
  /// </summary>
  public static Capture Read(ReadOnlySpan<byte> data) {
    var layout = ReadLayout(data);
    var packets = new List<Packet>(layout.Packets.Count);
    foreach (var packet in layout.Packets)
      packets.Add(new Packet(packet.InterfaceId, packet.TimestampRaw,
        data.Slice(packet.DataOffset, packet.DataLength).ToArray(), packet.OriginalLength));
    return new Capture(layout.LittleEndian, layout.VersionMajor, layout.VersionMinor, layout.Interfaces, packets);
  }

  internal static CaptureLayout ReadLayout(ReadOnlySpan<byte> data) {
    if (data.Length < 12) throw new InvalidDataException("pcapng: file smaller than minimum SHB.");

    var blockType = BinaryPrimitives.ReadUInt32LittleEndian(data);
    if (blockType != BtSectionHeader) throw new InvalidDataException("pcapng: first block is not a Section Header.");

    var little = ByteOrderOf(data[8..]);
    var first = little;
    ushort verMajor = 1, verMinor = 0;
    var interfaces = new List<Interface>();
    var allInterfaces = new List<Interface>();
    var packets = new List<PacketLayout>();
    var sections = new List<SectionLayout>();

    var pos = 0;
    while (pos + 12 <= data.Length) {
      var bt = ReadU32(little, data[pos..]);
      if (bt == BtSectionHeader) {
        // Every section declares its own byte order.
        if (data.Length - pos < 28) break;
        little = ByteOrderOf(data[(pos + 8)..]);
      }
      var totalLen = ReadU32(little, data[(pos + 4)..]);
      if (totalLen < 12 || totalLen % 4 != 0 || totalLen > int.MaxValue || (int)totalLen > data.Length - pos)
        break;
      var blockLength = (int)totalLen;
      if (ReadU32(little, data[(pos + blockLength - 4)..]) != totalLen)
        break;

      var bodyOffset = pos + 8;
      var body = data.Slice(bodyOffset, blockLength - 12);

      switch (bt) {
        case BtSectionHeader:
          if (body.Length >= 16) {
            if (sections.Count == 0) {
              verMajor = ReadU16(little, body[4..]);
              verMinor = ReadU16(little, body[6..]);
            }
            interfaces = [];
            sections.Add(new SectionLayout(pos, blockLength, (long)ReadU64(little, body[8..]), interfaces));
          }
          break;

        case BtInterfaceDescription:
          if (body.Length >= 8) {
            var iface = ParseInterface(little, body);
            interfaces.Add(iface);
            allInterfaces.Add(iface);
          }
          break;

        case BtEnhancedPacket:
          if (body.Length >= 20) {
            var ifId = (int)ReadU32(little, body);
            var tsHi = ReadU32(little, body[4..]);
            var tsLo = ReadU32(little, body[8..]);
            var capLen = ReadU32(little, body[12..]);
            var origLen = ReadU32(little, body[16..]);
            if (capLen <= (uint)(body.Length - 20))
              packets.Add(new PacketLayout(ifId, ((ulong)tsHi << 32) | tsLo, bodyOffset + 20, (int)capLen, origLen,
                pos, blockLength, sections.Count - 1, ifId >= 0 && ifId < interfaces.Count ? interfaces[ifId] : null));
          }
          break;

        case BtSimplePacket:
          if (body.Length >= 4) {
            var origLen = ReadU32(little, body);
            var available = (uint)(body.Length - 4);
            // An SPB's captured length is the smaller of the original length and interface 0's snaplen.
            var snap = interfaces.Count > 0 && interfaces[0].Snaplen != 0 ? interfaces[0].Snaplen : uint.MaxValue;
            var packetLength = (int)Math.Min(available, Math.Min(origLen, snap));
            packets.Add(new PacketLayout(0, 0, bodyOffset + 4, packetLength, origLen,
              pos, blockLength, sections.Count - 1, interfaces.Count > 0 ? interfaces[0] : null));
          }
          break;
      }

      pos += blockLength;
    }

    return new CaptureLayout(first, verMajor, verMinor, allInterfaces, packets) { Sections = sections, EndOffset = pos };
  }

  private static Interface ParseInterface(bool little, ReadOnlySpan<byte> body) {
    var linkType = ReadU16(little, body);
    var snaplen = ReadU32(little, body[4..]);
    var units = 1_000_000UL;
    long offset = 0;
    byte? tsresol = null;
    var opts = body[8..];
    while (opts.Length >= 4) {
      var code = ReadU16(little, opts);
      var len = ReadU16(little, opts[2..]);
      if (code == OptEndOfOpt || len > opts.Length - 4) break;
      var value = opts.Slice(4, len);
      if (code == OptIfTsResol && len >= 1) {
        tsresol = value[0];
        try {
          units = Interface.UnitsFromTsResol(value[0]);
        } catch (InvalidDataException) {
          units = 0; // unusable resolution: packets of this interface list without timestamps
        }
      } else if (code == OptIfTsOffset && len >= 8)
        offset = (long)ReadU64(little, value);
      opts = opts[Math.Min(opts.Length, 4 + ((len + 3) & ~3))..];
    }
    return new Interface(linkType, snaplen, units, offset) { TsResolOption = tsresol };
  }

  private static bool ByteOrderOf(ReadOnlySpan<byte> bom) {
    if (BinaryPrimitives.ReadUInt32LittleEndian(bom) == ByteOrderMagic) return true;
    if (BinaryPrimitives.ReadUInt32BigEndian(bom) == ByteOrderMagic) return false;
    throw new InvalidDataException($"pcapng: invalid byte-order magic {Convert.ToHexString(bom[..4])}.");
  }

  internal static uint ReadU32(bool little, ReadOnlySpan<byte> s) =>
    little ? BinaryPrimitives.ReadUInt32LittleEndian(s) : BinaryPrimitives.ReadUInt32BigEndian(s);
  internal static ushort ReadU16(bool little, ReadOnlySpan<byte> s) =>
    little ? BinaryPrimitives.ReadUInt16LittleEndian(s) : BinaryPrimitives.ReadUInt16BigEndian(s);
  internal static ulong ReadU64(bool little, ReadOnlySpan<byte> s) =>
    little ? BinaryPrimitives.ReadUInt64LittleEndian(s) : BinaryPrimitives.ReadUInt64BigEndian(s);
}
