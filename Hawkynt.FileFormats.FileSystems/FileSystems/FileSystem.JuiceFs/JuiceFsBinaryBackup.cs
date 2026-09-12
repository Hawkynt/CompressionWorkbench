#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileSystem.JuiceFs;

internal static class JuiceFsBinaryBackup {
  private static readonly string[] SegmentNames = [
    "unknown", "format", "counter", "node", "edge", "chunk", "slice-ref", "symlink",
    "sustained", "deleted-file", "xattr", "acl", "stat", "quota", "parent", "change-log",
  ];

  public static bool TryParse(byte[] data, List<JuiceFsEntry> entries, out uint version, out int segmentCount) {
    version = 0;
    segmentCount = 0;
    if (data.Length < 16)
      return false;

    var footerLength = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(data.Length - sizeof(ulong), sizeof(ulong)));
    if (footerLength > int.MaxValue || footerLength + sizeof(uint) + sizeof(ulong) > (ulong)data.Length)
      return false;
    var footerStart = data.Length - sizeof(ulong) - (int)footerLength;
    var eosOffset = footerStart - sizeof(uint);
    if (eosOffset < 0 || BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(eosOffset, sizeof(uint))) != JuiceFsReader.BakMagic)
      return false;

    var footer = data.AsSpan(footerStart, (int)footerLength);
    if (!TryReadFooterIdentity(footer, out var magic, out version)
        || magic != JuiceFsReader.BakMagic || version != JuiceFsReader.BakVersion)
      return false;

    var segments = new List<Segment>();
    var position = 0;
    while (position < eosOffset) {
      if (eosOffset - position < sizeof(uint) + sizeof(ulong))
        return false;
      var type = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position, sizeof(uint)));
      position += sizeof(uint);
      if (type is 0 or > 15)
        return false;
      var length = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(position, sizeof(ulong)));
      position += sizeof(ulong);
      if (length > int.MaxValue || (ulong)(eosOffset - position) < length)
        return false;
      segments.Add(new Segment(segments.Count, type, position, (int)length));
      position += (int)length;
    }
    if (position != eosOffset || !segments.Any(segment => segment.Type == 1))
      return false;

    segmentCount = segments.Count;
    JuiceFsJsonBackup.AddGenerated(entries, "metadata.ini", BuildMetadata(version, segments));
    foreach (var segment in segments)
      JuiceFsJsonBackup.AddSource(entries, $"segments/{segment.Index:D4}-{SegmentNames[(int)segment.Type]}.pb", segment.Offset, segment.Length);
    JuiceFsJsonBackup.AddSource(entries, "footer.pb", footerStart, (int)footerLength);
    JuiceFsJsonBackup.AddSource(entries, "juicefs-backup.bin", 0, data.LongLength);
    return true;
  }

  private static byte[] BuildMetadata(uint version, IReadOnlyList<Segment> segments) {
    var builder = new StringBuilder();
    builder.AppendLine("parse_status=ok");
    builder.AppendLine("backup_kind=binary");
    builder.AppendLine("format=JuiceFS segmented protobuf metadata backup");
    builder.Append("bak_magic=0x").AppendLine(JuiceFsReader.BakMagic.ToString("X8", CultureInfo.InvariantCulture));
    builder.Append("bak_version=").AppendLine(version.ToString(CultureInfo.InvariantCulture));
    builder.Append("segments=").AppendLine(segments.Count.ToString(CultureInfo.InvariantCulture));
    foreach (var group in segments.GroupBy(segment => segment.Type).OrderBy(group => group.Key))
      builder.Append("segments_").Append(SegmentNames[(int)group.Key].Replace('-', '_')).Append('=')
        .AppendLine(group.Count().ToString(CultureInfo.InvariantCulture));
    JuiceFsJsonBackup.AppendLimitations(builder, "no representation-only transform is applied to compact protobuf backups");
    return Encoding.UTF8.GetBytes(builder.ToString());
  }

  private static bool TryReadFooterIdentity(ReadOnlySpan<byte> footer, out uint magic, out uint version) {
    magic = 0;
    version = 0;
    var offset = 0;
    while (offset < footer.Length) {
      if (!TryReadVarint(footer, ref offset, out var key)) return false;
      var field = key >> 3;
      if (field == 0) return false;
      switch ((int)(key & 7)) {
        case 0:
          if (!TryReadVarint(footer, ref offset, out var value) || value > uint.MaxValue) return false;
          if (field == 1) magic = (uint)value;
          else if (field == 2) version = (uint)value;
          break;
        case 1:
          if (footer.Length - offset < sizeof(ulong)) return false;
          offset += sizeof(ulong);
          break;
        case 2:
          if (!TryReadVarint(footer, ref offset, out var length) || length > int.MaxValue || footer.Length - offset < (int)length)
            return false;
          offset += (int)length;
          break;
        case 5:
          if (footer.Length - offset < sizeof(uint)) return false;
          offset += sizeof(uint);
          break;
        default:
          return false;
      }
    }
    return magic != 0 && version != 0;
  }

  private static bool TryReadVarint(ReadOnlySpan<byte> data, ref int offset, out ulong value) {
    value = 0;
    for (var shift = 0; shift < 64 && offset < data.Length; shift += 7) {
      var b = data[offset++];
      value |= (ulong)(b & 0x7F) << shift;
      if ((b & 0x80) == 0) return true;
    }
    return false;
  }

  private readonly record struct Segment(int Index, uint Type, int Offset, int Length);
}
