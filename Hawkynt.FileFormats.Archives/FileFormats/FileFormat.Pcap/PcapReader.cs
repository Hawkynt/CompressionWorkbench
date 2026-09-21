#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Pcap;

/// <summary>
/// Reader for libpcap capture files.  Handles the four known global-header magic
/// constants (native/swapped × microsecond/nanosecond) and decodes each packet
/// record into its link-layer payload bytes.
/// </summary>
public sealed class PcapReader {

  /// <summary>
  /// Defines the magic native micro constant value.
  /// </summary>
  public const uint MagicNativeMicro = 0xA1B2C3D4u;
  /// <summary>
  /// Defines the magic swap micro constant value.
  /// </summary>
  public const uint MagicSwapMicro   = 0xD4C3B2A1u;
  /// <summary>
  /// Defines the magic native nano constant value.
  /// </summary>
  public const uint MagicNativeNano  = 0xA1B23C4Du;
  /// <summary>
  /// Defines the magic swap nano constant value.
  /// </summary>
  public const uint MagicSwapNano    = 0x4DC3B2A1u;

  /// <summary>
  /// Represents a capture.
  /// </summary>
  public sealed class Capture {
    /// <summary>
    /// Gets or sets the version major.
    /// </summary>
    public required ushort VersionMajor { get; init; }
    /// <summary>
    /// Gets or sets the version minor.
    /// </summary>
    public required ushort VersionMinor { get; init; }
    /// <summary>
    /// Gets or sets the snaplen.
    /// </summary>
    public required uint Snaplen { get; init; }
    /// <summary>Link-layer header type (1 = Ethernet, 101 = raw IP, 113 = Linux cooked, etc.).</summary>
    public required uint LinkType { get; init; }
    /// <summary>
    /// Gets a value indicating whether little endian.
    /// </summary>
    public required bool LittleEndian { get; init; }
    /// <summary>
    /// Gets a value indicating whether nanosecond.
    /// </summary>
    public required bool Nanosecond { get; init; }
    /// <summary>
    /// Gets or sets the packets.
    /// </summary>
    public required IReadOnlyList<Packet> Packets { get; init; }
  }

  /// <summary>
  /// Represents a packet.
  /// </summary>
  public sealed record Packet(uint TimestampSeconds, uint TimestampFraction, uint OriginalLength, byte[] Data);

  internal sealed record PacketLayout(
    uint TimestampSeconds,
    uint TimestampFraction,
    uint OriginalLength,
    int DataOffset,
    int DataLength);

  internal sealed class CaptureLayout {
    public required ushort VersionMajor { get; init; }
    public required ushort VersionMinor { get; init; }
    public required uint Snaplen { get; init; }
    public required uint LinkType { get; init; }
    public required bool LittleEndian { get; init; }
    public required bool Nanosecond { get; init; }
    public required IReadOnlyList<PacketLayout> Packets { get; init; }
  }

  /// <summary>Parse a pcap file in full.</summary>
  public static Capture Read(ReadOnlySpan<byte> data) {
    var layout = ReadLayout(data);
    return new Capture {
      VersionMajor = layout.VersionMajor,
      VersionMinor = layout.VersionMinor,
      Snaplen = layout.Snaplen,
      LinkType = layout.LinkType,
      LittleEndian = layout.LittleEndian,
      Nanosecond = layout.Nanosecond,
      Packets = MaterializePackets(data, layout.Packets),
    };
  }

  private static List<Packet> MaterializePackets(
      ReadOnlySpan<byte> data, IReadOnlyList<PacketLayout> layouts) {
    var packets = new List<Packet>(layouts.Count);
    foreach (var packet in layouts)
      packets.Add(new Packet(
        packet.TimestampSeconds,
        packet.TimestampFraction,
        packet.OriginalLength,
        data.Slice(packet.DataOffset, packet.DataLength).ToArray()));
    return packets;
  }

  internal static CaptureLayout ReadLayout(ReadOnlySpan<byte> data) {
    if (data.Length < 24) throw new InvalidDataException("Truncated pcap: file smaller than global header.");
    var magic = BinaryPrimitives.ReadUInt32LittleEndian(data);
    var (little, nano) = magic switch {
      MagicNativeMicro => (false, false),
      MagicSwapMicro => (true, false),
      MagicNativeNano => (false, true),
      MagicSwapNano => (true, true),
      _ => throw new InvalidDataException($"Unrecognized pcap magic: 0x{magic:X8}"),
    };

    ushort ReadU16(ReadOnlySpan<byte> s) =>
      little ? BinaryPrimitives.ReadUInt16LittleEndian(s) : BinaryPrimitives.ReadUInt16BigEndian(s);
    uint ReadU32(ReadOnlySpan<byte> s) =>
      little ? BinaryPrimitives.ReadUInt32LittleEndian(s) : BinaryPrimitives.ReadUInt32BigEndian(s);

    var verMajor = ReadU16(data[4..]);
    var verMinor = ReadU16(data[6..]);
    var snaplen = ReadU32(data[16..]);
    var linktype = ReadU32(data[20..]);

    var packets = new List<PacketLayout>();
    var pos = 24;
    while (pos + 16 <= data.Length) {
      var tsSec = ReadU32(data[pos..]);
      var tsFrac = ReadU32(data[(pos + 4)..]);
      var inclLen = ReadU32(data[(pos + 8)..]);
      var origLen = ReadU32(data[(pos + 12)..]);
      pos += 16;

      if (inclLen > int.MaxValue)
        break;
      var len = (int)inclLen;
      if (len > data.Length - pos)
        break;

      packets.Add(new PacketLayout(tsSec, tsFrac, origLen, pos, len));
      pos += len;
    }

    return new CaptureLayout {
      VersionMajor = verMajor,
      VersionMinor = verMinor,
      Snaplen = snaplen,
      LinkType = linktype,
      LittleEndian = little,
      Nanosecond = nano,
      Packets = packets,
    };
  }
}
