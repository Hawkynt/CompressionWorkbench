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

  /// <summary>Parse a pcap file in full.</summary>
  public static Capture Read(ReadOnlySpan<byte> data) {
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
    // thiszone (int32) at 8, sigfigs (u32) at 12 — both unused by us.
    var snaplen = ReadU32(data[16..]);
    var linktype = ReadU32(data[20..]);

    var packets = new List<Packet>();
    var pos = 24;
    while (pos + 16 <= data.Length) {
      var tsSec = ReadU32(data[pos..]);
      var tsFrac = ReadU32(data[(pos + 4)..]);
      var inclLen = ReadU32(data[(pos + 8)..]);
      var origLen = ReadU32(data[(pos + 12)..]);
      pos += 16;
      if (inclLen > int.MaxValue) break;
      var len = (int)inclLen;
      if (pos + len > data.Length) break;
      var payload = data.Slice(pos, len).ToArray();
      packets.Add(new Packet(tsSec, tsFrac, origLen, payload));
      pos += len;
    }

    return new Capture {
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
