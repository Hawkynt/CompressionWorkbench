#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Pcapng;

/// <summary>
/// Reader for the modern pcapng capture format (RFC draft-tuexen-opsawg-pcapng).
/// Walks the block stream, surfaces each Enhanced Packet Block (EPB) and Simple
/// Packet Block (SPB) payload, and records per-interface metadata extracted from
/// the Section Header Block (SHB) and Interface Description Blocks (IDBs).
/// </summary>
public sealed class PcapngReader {

  // Block type constants — pcapng §11.
  /// <summary>
  /// Defines the bt section header constant value.
  /// </summary>
  public const uint BtSectionHeader = 0x0A0D0D0Au;
  /// <summary>
  /// Defines the bt interface description constant value.
  /// </summary>
  public const uint BtInterfaceDescription = 0x00000001u;
  /// <summary>
  /// Defines the bt enhanced packet constant value.
  /// </summary>
  public const uint BtEnhancedPacket = 0x00000006u;
  /// <summary>
  /// Defines the bt simple packet constant value.
  /// </summary>
  public const uint BtSimplePacket = 0x00000003u;
  /// <summary>
  /// Defines the byte order magic constant value.
  /// </summary>
  public const uint ByteOrderMagic = 0x1A2B3C4Du;

  /// <summary>
  /// Represents an interface.
  /// </summary>
  public sealed record Interface(uint LinkType, uint Snaplen);

  /// <summary>
  /// Represents a packet.
  /// </summary>
  public sealed record Packet(int InterfaceId, ulong TimestampRaw, byte[] Data, uint OriginalLength) {
    /// <summary>Decode the timestamp using the interface's <c>if_tsresol</c> (we
    /// default to 6 → microseconds since the option is not always present).</summary>
    public DateTime ToDateTime(int tsResolutionPow10 = 6) {
      // Most pcapng captures use microsecond resolution. Ticks are 100ns each.
      // Convert raw → seconds × resolution-divisor → ticks.
      var divisor = 1L;
      for (var i = 0; i < tsResolutionPow10; i++) divisor *= 10;
      var seconds = TimestampRaw / (ulong)divisor;
      var fraction = TimestampRaw % (ulong)divisor;
      var ticksPerUnit = TimeSpan.TicksPerSecond / divisor;
      return DateTime.UnixEpoch.AddSeconds(seconds).AddTicks((long)fraction * ticksPerUnit);
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

  internal sealed record PacketLayout(
    int InterfaceId,
    ulong TimestampRaw,
    int DataOffset,
    int DataLength,
    uint OriginalLength) {
    public DateTime ToDateTime(int tsResolutionPow10 = 6) {
      var divisor = 1L;
      for (var i = 0; i < tsResolutionPow10; i++) divisor *= 10;
      var seconds = this.TimestampRaw / (ulong)divisor;
      var fraction = this.TimestampRaw % (ulong)divisor;
      var ticksPerUnit = TimeSpan.TicksPerSecond / divisor;
      return DateTime.UnixEpoch.AddSeconds(seconds).AddTicks((long)fraction * ticksPerUnit);
    }
  }

  internal sealed record CaptureLayout(
    bool LittleEndian,
    ushort VersionMajor,
    ushort VersionMinor,
    IReadOnlyList<Interface> Interfaces,
    IReadOnlyList<PacketLayout> Packets);

  /// <summary>
  /// Reads the value from the supplied input.
  /// </summary>
  public static Capture Read(ReadOnlySpan<byte> data) {
    var layout = ReadLayout(data);
    return new Capture(
      layout.LittleEndian,
      layout.VersionMajor,
      layout.VersionMinor,
      layout.Interfaces,
      MaterializePackets(data, layout.Packets));
  }

  private static List<Packet> MaterializePackets(
      ReadOnlySpan<byte> data, IReadOnlyList<PacketLayout> layouts) {
    var packets = new List<Packet>(layouts.Count);
    foreach (var packet in layouts)
      packets.Add(new Packet(
        packet.InterfaceId,
        packet.TimestampRaw,
        data.Slice(packet.DataOffset, packet.DataLength).ToArray(),
        packet.OriginalLength));
    return packets;
  }

  internal static CaptureLayout ReadLayout(ReadOnlySpan<byte> data) {
    if (data.Length < 12) throw new InvalidDataException("pcapng: file smaller than minimum SHB.");

    var blockType = BinaryPrimitives.ReadUInt32LittleEndian(data);
    if (blockType != BtSectionHeader) throw new InvalidDataException("pcapng: first block is not a Section Header.");

    var bomLe = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
    var bomBe = BinaryPrimitives.ReadUInt32BigEndian(data[8..]);
    bool little;
    if (bomLe == ByteOrderMagic) little = true;
    else if (bomBe == ByteOrderMagic) little = false;
    else throw new InvalidDataException($"pcapng: invalid byte-order magic (got 0x{bomLe:X8}/0x{bomBe:X8})");

    uint ReadU32(ReadOnlySpan<byte> s) =>
      little ? BinaryPrimitives.ReadUInt32LittleEndian(s) : BinaryPrimitives.ReadUInt32BigEndian(s);
    ushort ReadU16(ReadOnlySpan<byte> s) =>
      little ? BinaryPrimitives.ReadUInt16LittleEndian(s) : BinaryPrimitives.ReadUInt16BigEndian(s);

    var pos = 0;
    ushort verMajor = 1, verMinor = 0;
    var interfaces = new List<Interface>();
    var packets = new List<PacketLayout>();

    while (pos + 12 <= data.Length) {
      var bt = ReadU32(data[pos..]);
      var totalLen = ReadU32(data[(pos + 4)..]);
      if (totalLen < 12 || totalLen > int.MaxValue)
        break;

      var blockLength = (int)totalLen;
      if (blockLength > data.Length - pos)
        break;

      var bodyOffset = pos + 8;
      var bodyLength = blockLength - 12;
      var body = data.Slice(bodyOffset, bodyLength);

      switch (bt) {
        case BtSectionHeader:
          if (body.Length >= 8) {
            verMajor = ReadU16(body[4..]);
            verMinor = ReadU16(body[6..]);
          }
          interfaces.Clear();
          break;

        case BtInterfaceDescription:
          if (body.Length >= 8) {
            var linkType = ReadU16(body);
            var snaplen = ReadU32(body[4..]);
            interfaces.Add(new Interface(linkType, snaplen));
          }
          break;

        case BtEnhancedPacket:
          if (body.Length >= 20) {
            var ifId = (int)ReadU32(body);
            var tsHi = ReadU32(body[4..]);
            var tsLo = ReadU32(body[8..]);
            var capLen = ReadU32(body[12..]);
            var origLen = ReadU32(body[16..]);
            if (capLen <= int.MaxValue) {
              var packetLength = (int)capLen;
              if (packetLength <= body.Length - 20)
                packets.Add(new PacketLayout(
                  ifId,
                  ((ulong)tsHi << 32) | tsLo,
                  bodyOffset + 20,
                  packetLength,
                  origLen));
            }
          }
          break;

        case BtSimplePacket:
          if (body.Length >= 4) {
            var origLen = ReadU32(body);
            var available = body.Length - 4;
            var packetLength = (int)Math.Min((uint)available, origLen);
            packets.Add(new PacketLayout(0, 0, bodyOffset + 4, packetLength, origLen));
          }
          break;
      }

      pos += blockLength;
    }

    return new CaptureLayout(little, verMajor, verMinor, interfaces, packets);
  }
}
