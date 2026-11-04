using System.Buffers.Binary;

namespace FileFormat.Pcapng;

/// <summary>Settings of the single interface a pcapng file written by <see cref="PcapngWriter"/> describes.</summary>
/// <param name="LinkType">Link-layer header type (<c>LINKTYPE_*</c>; 1 = Ethernet).</param>
/// <param name="Snaplen">Snapshot length; 0 means unlimited.</param>
/// <param name="Nanosecond">Count timestamps in nanoseconds (<c>if_tsresol</c> = 9) rather than the default microseconds.</param>
public sealed record PcapngSettings(uint LinkType = 1, uint Snaplen = 0, bool Nanosecond = false) {
  /// <summary>Timestamp units per second.</summary>
  public ulong UnitsPerSecond => this.Nanosecond ? 1_000_000_000UL : 1_000_000UL;
}

/// <summary>
/// Writes little-endian pcapng (draft-ietf-opsawg-pcapng): a Section Header Block (version 1.0,
/// section length unspecified), one Interface Description Block, then one Enhanced Packet Block
/// per packet on interface 0.
/// </summary>
public static class PcapngWriter {

  /// <summary>The Section Header Block this writer starts a file with.</summary>
  public static byte[] SectionHeader() {
    var b = new byte[28];
    BinaryPrimitives.WriteUInt32LittleEndian(b, PcapngReader.BtSectionHeader);
    BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), 28);
    BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), PcapngReader.ByteOrderMagic);
    BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(12), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(14), 0);
    BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(16), -1);
    BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(24), 28);
    return b;
  }

  /// <summary>An Interface Description Block; nanosecond interfaces carry <c>if_tsresol</c> = 9 and <c>opt_endofopt</c>.</summary>
  public static byte[] InterfaceDescription(PcapngSettings settings) {
    ArgumentNullException.ThrowIfNull(settings);
    if (settings.LinkType > ushort.MaxValue)
      throw new ArgumentException($"pcapng: link type {settings.LinkType} does not fit the 16-bit IDB field.", nameof(settings));
    var length = settings.Nanosecond ? 32 : 20;
    var b = new byte[length];
    BinaryPrimitives.WriteUInt32LittleEndian(b, PcapngReader.BtInterfaceDescription);
    BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)length);
    BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(8), (ushort)settings.LinkType);
    BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), settings.Snaplen);
    if (settings.Nanosecond) {
      BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(16), PcapngReader.OptIfTsResol);
      BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(18), 1);
      b[20] = 9;
      // b[24..28] = opt_endofopt (code 0, length 0)
    }
    BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(length - 4), (uint)length);
    return b;
  }

  /// <summary>
  /// An Enhanced Packet Block for <paramref name="interfaceId"/>, the timestamp counted in
  /// <paramref name="unitsPerSecond"/> since the Unix epoch.
  /// </summary>
  public static byte[] EnhancedPacket(uint interfaceId, ulong unitsPerSecond, DateTimeOffset timestamp, ReadOnlySpan<byte> data,
      bool littleEndian = true, uint snaplen = 0) {
    if (snaplen != 0 && (uint)data.Length > snaplen)
      throw new ArgumentException($"pcapng: a {data.Length}-byte packet exceeds the interface snaplen of {snaplen}.", nameof(data));
    var raw = RawTimestamp(timestamp, unitsPerSecond);
    var padded = (data.Length + 3) & ~3;
    var length = 32 + padded;
    var b = new byte[length];
    void U32(int at, uint v) {
      if (littleEndian) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), v);
      else BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(at), v);
    }
    U32(0, PcapngReader.BtEnhancedPacket);
    U32(4, (uint)length);
    U32(8, interfaceId);
    U32(12, (uint)(raw >> 32));
    U32(16, (uint)raw);
    U32(20, (uint)data.Length);
    U32(24, (uint)data.Length);
    data.CopyTo(b.AsSpan(28));
    U32(length - 4, (uint)length);
    return b;
  }

  /// <summary>Writes a whole single-interface capture.</summary>
  public static void Write(Stream output, PcapngSettings settings, IEnumerable<(DateTimeOffset Timestamp, byte[] Data)> packets) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(packets);
    output.Write(SectionHeader());
    output.Write(InterfaceDescription(settings));
    foreach (var (ts, data) in packets)
      output.Write(EnhancedPacket(0, settings.UnitsPerSecond, ts, data, snaplen: settings.Snaplen));
  }

  /// <summary>Units since the Unix epoch.</summary>
  /// <exception cref="ArgumentOutOfRangeException">The instant precedes 1970.</exception>
  public static ulong RawTimestamp(DateTimeOffset timestamp, ulong unitsPerSecond) {
    var ticks = timestamp.UtcTicks - DateTime.UnixEpoch.Ticks;
    if (ticks < 0)
      throw new ArgumentOutOfRangeException(nameof(timestamp), timestamp, "pcapng timestamps count from 1970.");
    var seconds = (ulong)(ticks / TimeSpan.TicksPerSecond);
    var rest = (ulong)(ticks % TimeSpan.TicksPerSecond);
    return checked(seconds * unitsPerSecond + (ulong)((UInt128)rest * unitsPerSecond / TimeSpan.TicksPerSecond));
  }
}
