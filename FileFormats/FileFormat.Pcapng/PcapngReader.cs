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
  public const uint BtSectionHeader = 0x0A0D0D0Au;
  public const uint BtInterfaceDescription = 0x00000001u;
  public const uint BtEnhancedPacket = 0x00000006u;
  public const uint BtSimplePacket = 0x00000003u;
  public const uint ByteOrderMagic = 0x1A2B3C4Du;

  public sealed record Interface(uint LinkType, uint Snaplen);

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

  public sealed record Capture(
    bool LittleEndian,
    ushort VersionMajor,
    ushort VersionMinor,
    IReadOnlyList<Interface> Interfaces,
    IReadOnlyList<Packet> Packets);

  public static Capture Read(ReadOnlySpan<byte> data) {
    if (data.Length < 12) throw new InvalidDataException("pcapng: file smaller than minimum SHB.");

    // First block must be a Section Header Block. Detect endianness from BOM.
    var blockType = BinaryPrimitives.ReadUInt32LittleEndian(data);
    if (blockType != BtSectionHeader) throw new InvalidDataException("pcapng: first block is not a Section Header.");

    // The BOM is at offset 8 of the SHB body.
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
    var packets = new List<Packet>();

    while (pos + 12 <= data.Length) {
      var bt = ReadU32(data[pos..]);
      var totalLen = ReadU32(data[(pos + 4)..]);
      if (totalLen < 12 || pos + totalLen > (uint)data.Length) break;

      // Body sits between offset+8 and totalLen-4 (the trailing length copy).
      var body = data.Slice(pos + 8, (int)totalLen - 12);

      switch (bt) {
        case BtSectionHeader:
          if (body.Length >= 8) {
            verMajor = ReadU16(body[4..]);
            verMinor = ReadU16(body[6..]);
          }
          // New section: drop interface table per spec §4.1.
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
            if (20 + capLen <= (uint)body.Length) {
              var packetData = body.Slice(20, (int)capLen).ToArray();
              packets.Add(new Packet(
                InterfaceId: ifId,
                TimestampRaw: ((ulong)tsHi << 32) | tsLo,
                Data: packetData,
                OriginalLength: origLen));
            }
          }
          break;

        case BtSimplePacket:
          if (body.Length >= 4) {
            var origLen = ReadU32(body);
            // SPB has no captured-length field; the captured length equals body.Length-4
            // up to snaplen. Take the smaller.
            var avail = body.Length - 4;
            var capLen = (int)Math.Min((uint)avail, origLen);
            var packetData = body.Slice(4, capLen).ToArray();
            packets.Add(new Packet(InterfaceId: 0, TimestampRaw: 0, Data: packetData, OriginalLength: origLen));
          }
          break;

        // Other blocks (NRB type 4, ISB type 5, custom 0x00000BAD, etc.) are skipped silently.
      }

      pos += (int)totalLen;
    }

    return new Capture(
      LittleEndian: little,
      VersionMajor: verMajor,
      VersionMinor: verMinor,
      Interfaces: interfaces,
      Packets: packets);
  }
}
