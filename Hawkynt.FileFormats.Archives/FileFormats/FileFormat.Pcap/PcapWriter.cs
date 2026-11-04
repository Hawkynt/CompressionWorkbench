using System.Buffers.Binary;

namespace FileFormat.Pcap;

/// <summary>The global-header settings of a pcap file this library writes.</summary>
/// <param name="LinkType">Link-layer header type (<c>LINKTYPE_*</c>; 1 = Ethernet).</param>
/// <param name="Snaplen">Snapshot length; no packet may be longer.</param>
/// <param name="Nanosecond">Nanosecond (<c>0xA1B23C4D</c>) rather than microsecond timestamps.</param>
/// <param name="LittleEndian">Byte order of every header field.</param>
public sealed record PcapSettings(uint LinkType = 1, uint Snaplen = PcapSettings.DefaultSnaplen, bool Nanosecond = false, bool LittleEndian = true) {
  /// <summary>libpcap's <c>MAXIMUM_SNAPLEN</c>, the snaplen tcpdump writes by default.</summary>
  public const uint DefaultSnaplen = 262144;
}

/// <summary>
/// Writes libpcap capture files (format 2.4, pcap-savefile(5)): a 24-byte global header, then
/// per packet a 16-byte record header (seconds, sub-second fraction, captured length, original
/// length) followed by the captured bytes.
/// </summary>
public static class PcapWriter {

  /// <summary>Builds the 24-byte global header.</summary>
  public static byte[] FileHeader(PcapSettings settings) {
    ArgumentNullException.ThrowIfNull(settings);
    var h = new byte[PcapReader.FileHeaderSize];
    var o = new Order(settings.LittleEndian);
    o.U32(h, 0, settings.Nanosecond ? PcapReader.MagicNano : PcapReader.MagicMicro);
    o.U16(h, 4, 2);
    o.U16(h, 6, 4);
    // thiszone and sigfigs stay zero, as every modern writer leaves them.
    o.U32(h, 16, settings.Snaplen);
    o.U32(h, 20, settings.LinkType);
    return h;
  }

  /// <summary>Builds one packet record (header plus data).</summary>
  /// <exception cref="ArgumentException">The packet is longer than the snaplen, which libpcap readers reject.</exception>
  public static byte[] Record(PcapSettings settings, DateTimeOffset timestamp, ReadOnlySpan<byte> data, uint? originalLength = null) {
    ArgumentNullException.ThrowIfNull(settings);
    if ((uint)data.Length > settings.Snaplen)
      throw new ArgumentException($"pcap: a {data.Length}-byte packet exceeds the snaplen of {settings.Snaplen}.", nameof(data));
    var (seconds, fraction) = SplitTimestamp(timestamp, settings.Nanosecond);
    var r = new byte[PcapReader.RecordHeaderSize + data.Length];
    var o = new Order(settings.LittleEndian);
    o.U32(r, 0, seconds);
    o.U32(r, 4, fraction);
    o.U32(r, 8, (uint)data.Length);
    o.U32(r, 12, Math.Max(originalLength ?? (uint)data.Length, (uint)data.Length));
    data.CopyTo(r.AsSpan(PcapReader.RecordHeaderSize));
    return r;
  }

  /// <summary>Writes a whole capture.</summary>
  public static void Write(Stream output, PcapSettings settings, IEnumerable<(DateTimeOffset Timestamp, byte[] Data)> packets) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(packets);
    output.Write(FileHeader(settings));
    foreach (var (ts, data) in packets)
      output.Write(Record(settings, ts, data));
  }

  /// <summary>Seconds since the Unix epoch and the micro- or nanosecond remainder.</summary>
  /// <exception cref="ArgumentOutOfRangeException">The instant lies outside the unsigned 32-bit second range pcap stores.</exception>
  public static (uint Seconds, uint Fraction) SplitTimestamp(DateTimeOffset timestamp, bool nanosecond) {
    var ticks = timestamp.UtcTicks - DateTime.UnixEpoch.Ticks;
    if (ticks < 0 || ticks / TimeSpan.TicksPerSecond > uint.MaxValue)
      throw new ArgumentOutOfRangeException(nameof(timestamp), timestamp, "pcap timestamps are unsigned 32-bit seconds since 1970.");
    var seconds = (uint)(ticks / TimeSpan.TicksPerSecond);
    var rest = ticks % TimeSpan.TicksPerSecond;
    return (seconds, nanosecond ? (uint)(rest * 100) : (uint)(rest / 10));
  }

  private readonly struct Order(bool little) {
    public void U16(byte[] b, int at, ushort v) {
      if (little) BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at), v);
      else BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(at), v);
    }

    public void U32(byte[] b, int at, uint v) {
      if (little) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), v);
      else BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(at), v);
    }
  }
}
