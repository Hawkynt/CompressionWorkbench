#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Pcap;

/// <summary>
/// Reader for libpcap capture files.  Handles the four global-header magic forms
/// (little/big-endian × microsecond/nanosecond) and decodes each packet record into
/// its link-layer payload bytes.
/// </summary>
/// <remarks>
/// libpcap writes the magic number <c>0xA1B2C3D4</c> (or <c>0xA1B23C4D</c> for nanosecond
/// timestamps) in the byte order of the capturing host, and every later field in that same
/// order (pcap-savefile(5)). A capture taken on x86 therefore starts <c>D4 C3 B2 A1</c> and is
/// little-endian throughout; one from a big-endian host starts <c>A1 B2 C3 D4</c>.
/// </remarks>
public sealed class PcapReader {

  /// <summary>The microsecond magic number as libpcap defines it.</summary>
  public const uint MagicMicro = 0xA1B2C3D4u;
  /// <summary>The nanosecond magic number as libpcap defines it.</summary>
  public const uint MagicNano = 0xA1B23C4Du;

  /// <summary>Size of the global file header.</summary>
  public const int FileHeaderSize = 24;
  /// <summary>Size of each per-packet record header.</summary>
  public const int RecordHeaderSize = 16;

  /// <summary>
  /// Represents a capture.
  /// </summary>
  public sealed class Capture {
    public required ushort VersionMajor { get; init; }
    public required ushort VersionMinor { get; init; }
    public required uint Snaplen { get; init; }
    /// <summary>Link-layer header type (1 = Ethernet, 101 = raw IP, 113 = Linux cooked, etc.).</summary>
    public required uint LinkType { get; init; }
    public required bool LittleEndian { get; init; }
    public required bool Nanosecond { get; init; }
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
    int DataLength) {
    /// <summary>Offset of the record header that precedes the data.</summary>
    public int RecordOffset => this.DataOffset - RecordHeaderSize;
    /// <summary>Length of the whole record, header included.</summary>
    public int RecordLength => this.DataLength + RecordHeaderSize;
  }

  internal sealed class CaptureLayout {
    public required ushort VersionMajor { get; init; }
    public required ushort VersionMinor { get; init; }
    public required uint Snaplen { get; init; }
    public required uint LinkType { get; init; }
    public required bool LittleEndian { get; init; }
    public required bool Nanosecond { get; init; }
    public required IReadOnlyList<PacketLayout> Packets { get; init; }
    /// <summary>Bytes after the last complete record (a truncated record, or junk).</summary>
    public required int TrailingBytes { get; init; }
  }

  /// <summary>Parse a pcap file in full.</summary>
  public static Capture Read(ReadOnlySpan<byte> data) {
    var layout = ReadLayout(data);
    var packets = new List<Packet>(layout.Packets.Count);
    foreach (var packet in layout.Packets)
      packets.Add(new Packet(packet.TimestampSeconds, packet.TimestampFraction, packet.OriginalLength,
        data.Slice(packet.DataOffset, packet.DataLength).ToArray()));
    return new Capture {
      VersionMajor = layout.VersionMajor,
      VersionMinor = layout.VersionMinor,
      Snaplen = layout.Snaplen,
      LinkType = layout.LinkType,
      LittleEndian = layout.LittleEndian,
      Nanosecond = layout.Nanosecond,
      Packets = packets,
    };
  }

  /// <summary>Byte order and timestamp resolution named by the first four bytes, or null when they are no pcap magic.</summary>
  internal static (bool LittleEndian, bool Nanosecond)? ClassifyMagic(ReadOnlySpan<byte> head) {
    if (head.Length < 4) return null;
    var le = BinaryPrimitives.ReadUInt32LittleEndian(head);
    var be = BinaryPrimitives.ReadUInt32BigEndian(head);
    return le switch {
      MagicMicro => (true, false),
      MagicNano => (true, true),
      _ => be switch {
        MagicMicro => (false, false),
        MagicNano => (false, true),
        _ => null,
      },
    };
  }

  internal static CaptureLayout ReadLayout(ReadOnlySpan<byte> data) {
    if (data.Length < FileHeaderSize) throw new InvalidDataException("Truncated pcap: file smaller than global header.");
    var (little, nano) = ClassifyMagic(data)
      ?? throw new InvalidDataException($"Unrecognized pcap magic: {Convert.ToHexString(data[..4])}");

    ushort ReadU16(ReadOnlySpan<byte> s) =>
      little ? BinaryPrimitives.ReadUInt16LittleEndian(s) : BinaryPrimitives.ReadUInt16BigEndian(s);
    uint ReadU32(ReadOnlySpan<byte> s) =>
      little ? BinaryPrimitives.ReadUInt32LittleEndian(s) : BinaryPrimitives.ReadUInt32BigEndian(s);

    var verMajor = ReadU16(data[4..]);
    var verMinor = ReadU16(data[6..]);
    var snaplen = ReadU32(data[16..]);
    // The upper bits of the link-type word carry the FCS-length flag (pcap-savefile(5)); the
    // link type proper is the low 28 bits.
    var linktype = ReadU32(data[20..]);

    var packets = new List<PacketLayout>();
    var pos = FileHeaderSize;
    while (pos + RecordHeaderSize <= data.Length) {
      var tsSec = ReadU32(data[pos..]);
      var tsFrac = ReadU32(data[(pos + 4)..]);
      var inclLen = ReadU32(data[(pos + 8)..]);
      var origLen = ReadU32(data[(pos + 12)..]);
      if (inclLen > int.MaxValue || (int)inclLen > data.Length - pos - RecordHeaderSize)
        break;
      var len = (int)inclLen;
      packets.Add(new PacketLayout(tsSec, tsFrac, origLen, pos + RecordHeaderSize, len));
      pos += RecordHeaderSize + len;
    }

    return new CaptureLayout {
      VersionMajor = verMajor,
      VersionMinor = verMinor,
      Snaplen = snaplen,
      LinkType = linktype,
      LittleEndian = little,
      Nanosecond = nano,
      Packets = packets,
      TrailingBytes = data.Length - pos,
    };
  }
}
