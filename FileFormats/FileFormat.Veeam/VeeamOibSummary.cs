#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace FileFormat.Veeam;

/// <summary>
/// Reverse-engineered (community DFIR research) reader for the trailing
/// <c>&lt;OibSummary&gt;</c> XML metadata island embedded in unencrypted Veeam
/// Backup &amp; Replication storage files (.vbk / .vib / .vrb).
/// </summary>
/// <remarks>
/// <para>
/// Veeam VBK/VIB/VRB are proprietary archive containers whose binary chunk
/// framing (metadata-bank pairs + compressed data blocks) is not publicly
/// documented and has resisted public community reverse engineering — Veeam
/// has stated this on their own R&amp;D forums.
/// </para>
/// <para>
/// The single publicly reverse-engineered surface is the trailing XML island:
/// in <em>unencrypted</em> backups the OibSummary metadata is stored as a
/// literal UTF-8 byte sequence near the end of the file (encrypted backups
/// obfuscate it). The well-known recovery strategy (Synacktiv / Velociraptor
/// <c>Windows.Veeam.RestorePoints.BackupFiles</c>) is to find the <em>last</em>
/// occurrence of the byte sequence <c>&lt;OibSummary&gt;</c> in the file and
/// the next occurrence of <c>&lt;/OibSummary&gt;</c> after it, treating the
/// span between them as the OibSummary XML document.
/// </para>
/// <para>
/// This reader implements exactly that strategy: a bounded reverse scan from
/// EOF in 64 KiB pages for the open tag, then a bounded forward scan capped at
/// 20 MiB for the close tag. No assumption is made about file magic, header
/// layout, or chunk framing — none of which are public.
/// </para>
/// </remarks>
public static class VeeamOibSummary {

  /// <summary>UTF-8 opening tag marker.</summary>
  internal static readonly byte[] OpenTag = "<OibSummary>"u8.ToArray();

  /// <summary>UTF-8 closing tag marker.</summary>
  internal static readonly byte[] CloseTag = "</OibSummary>"u8.ToArray();

  /// <summary>Reverse-scan page size for locating the opening tag.</summary>
  private const int ScanPage = 64 * 1024;

  /// <summary>Maximum forward distance from open tag to scan for close tag (matches public DFIR tooling).</summary>
  private const int MaxXmlSpan = 20 * 1024 * 1024;

  /// <summary>
  /// Attempts to locate and extract the trailing OibSummary XML document.
  /// Returns the UTF-8 byte span (including both tags) when found, otherwise <c>null</c>.
  /// </summary>
  public static byte[]? TryExtract(Stream stream) {
    if (stream is not { CanSeek: true, CanRead: true }) return null;
    if (stream.Length < OpenTag.Length + CloseTag.Length) return null;

    var openOffset = FindLastOpenTag(stream);
    if (openOffset < 0) return null;

    var closeOffset = FindCloseTag(stream, openOffset + OpenTag.Length);
    if (closeOffset < 0) return null;

    var totalLength = closeOffset - openOffset + CloseTag.Length;
    if (totalLength <= 0 || totalLength > MaxXmlSpan) return null;

    stream.Position = openOffset;
    var buf = new byte[totalLength];
    var read = 0;
    while (read < buf.Length) {
      var n = stream.Read(buf, read, buf.Length - read);
      if (n <= 0) break;
      read += n;
    }
    return read == buf.Length ? buf : null;
  }

  /// <summary>
  /// Attempts to parse the extracted OibSummary XML into a structured summary.
  /// </summary>
  public static VeeamOibSummaryInfo? TryParse(byte[]? xmlBytes) {
    if (xmlBytes is null || xmlBytes.Length == 0) return null;
    try {
      var settings = new XmlReaderSettings {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
      };
      using var ms = new MemoryStream(xmlBytes, writable: false);
      using var reader = XmlReader.Create(ms, settings);
      var doc = XDocument.Load(reader);
      var root = doc.Root;
      if (root is null) return null;

      var oib = root.Element("OIB");
      var info = new VeeamOibSummaryInfo {
        DisplayName = GetString(oib, "DisplayName"),
        VmName = GetString(oib, "VmName"),
        OibType = GetString(oib, "OibType"),
        BackupType = GetString(oib, "Type"),
        Algorithm = GetString(oib, "Algorithm"),
        CreationTimeUtc = GetString(oib, "CreationTimeUtc") ?? GetString(oib, "CreationTime"),
        CompletionTimeUtc = GetString(oib, "CompletionTimeUtc") ?? GetString(oib, "CompletionTime"),
        IsCorrupted = GetBoolFlag(oib, "IsCorrupted"),
        SourceHostName = GetString(root.Element("SourceHost"), "Name"),
        SourceHostInstanceId = GetString(root.Element("SourceHost"), "InstanceId"),
        JobName = GetString(root.Element("Backup"), "JobName")
                 ?? GetString(root.Element("Backup"), "Name"),
        PolicyName = GetString(root.Element("Backup"), "PolicyName"),
        IsEncrypted = GetBoolFlag(root.Element("Backup"), "Encrypted")
                     ?? GetBoolFlag(root.Element("Backup"), "IsEncrypted"),
        ObjectName = GetString(root.Element("Object"), "Name"),
        ObjectId = GetString(root.Element("Object"), "Id") ?? GetString(root.Element("Object"), "ObjectId"),
        PointNumber = GetInt(root.Element("Point"), "Number"),
        PointType = GetString(root.Element("Point"), "Type"),
        PrevFileName = (string?)root.Element("PrevFileName"),
        StoragePartialPath = GetString(root.Element("Storage"), "PartialPath")
                            ?? (string?)root.Element("Storage")?.Element("PartialPath"),
      };

      var files = root.Element("OibFiles");
      if (files is not null) {
        foreach (var f in files.Elements("File")) {
          var name = (string?)f.Attribute("Name") ?? (string?)f.Element("Name");
          var sizeStr = (string?)f.Attribute("Size") ?? (string?)f.Element("Size");
          if (string.IsNullOrEmpty(name)) continue;
          long size = 0;
          if (!string.IsNullOrEmpty(sizeStr))
            long.TryParse(sizeStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out size);
          info.Files.Add(new VeeamOibFile(name!, size));
        }
      }

      return info;
    } catch (XmlException) {
      return null;
    } catch (InvalidOperationException) {
      return null;
    }
  }

  // ── Private helpers ────────────────────────────────────────────────

  private static long FindLastOpenTag(Stream stream) {
    var length = stream.Length;
    var tag = OpenTag;
    var tagLen = tag.Length;
    var pageBuf = new byte[ScanPage + tagLen]; // overlap to catch tags split across pages
    var pos = length;

    while (pos > 0) {
      var pageLen = (int)Math.Min(ScanPage, pos);
      var readStart = pos - pageLen;
      // include overlap forward so a tag straddling [readStart .. readStart+pageLen+tagLen) is found
      var overlap = (int)Math.Min(tagLen - 1, length - (readStart + pageLen));
      var bufLen = pageLen + overlap;

      stream.Position = readStart;
      var read = 0;
      while (read < bufLen) {
        var n = stream.Read(pageBuf, read, bufLen - read);
        if (n <= 0) break;
        read += n;
      }
      if (read < tagLen) {
        pos = readStart;
        continue;
      }

      // scan this page from the right
      var lastIdx = IndexOfLast(pageBuf.AsSpan(0, read), tag);
      if (lastIdx >= 0) return readStart + lastIdx;

      pos = readStart;
    }
    return -1;
  }

  private static long FindCloseTag(Stream stream, long fromOffset) {
    var length = stream.Length;
    var max = Math.Min(length, fromOffset + MaxXmlSpan);
    var tag = CloseTag;
    var tagLen = tag.Length;
    var pageBuf = new byte[ScanPage + tagLen];
    var pos = fromOffset;

    while (pos < max) {
      var pageLen = (int)Math.Min(ScanPage, max - pos);
      var overlap = (int)Math.Min(tagLen - 1, length - (pos + pageLen));
      var bufLen = pageLen + overlap;

      stream.Position = pos;
      var read = 0;
      while (read < bufLen) {
        var n = stream.Read(pageBuf, read, bufLen - read);
        if (n <= 0) break;
        read += n;
      }
      if (read < tagLen) return -1;

      var idx = IndexOf(pageBuf.AsSpan(0, read), tag);
      if (idx >= 0) return pos + idx;

      pos += pageLen;
    }
    return -1;
  }

  private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    => haystack.IndexOf(needle);

  private static int IndexOfLast(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle) {
    var lastIdx = -1;
    var start = 0;
    while (true) {
      var idx = haystack[start..].IndexOf(needle);
      if (idx < 0) return lastIdx;
      lastIdx = start + idx;
      start = lastIdx + 1;
      if (start > haystack.Length - needle.Length) return lastIdx;
    }
  }

  private static string? GetString(XElement? parent, string name) {
    if (parent is null) return null;
    var attr = parent.Attribute(name)?.Value;
    if (!string.IsNullOrEmpty(attr)) return attr;
    var child = parent.Element(name)?.Value;
    return string.IsNullOrEmpty(child) ? null : child;
  }

  private static int? GetInt(XElement? parent, string name) {
    var s = GetString(parent, name);
    if (string.IsNullOrEmpty(s)) return null;
    return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
  }

  private static bool? GetBoolFlag(XElement? parent, string name) {
    var s = GetString(parent, name);
    if (string.IsNullOrEmpty(s)) return null;
    if (bool.TryParse(s, out var b)) return b;
    if (s == "1") return true;
    if (s == "0") return false;
    return null;
  }
}

/// <summary>Structured view of the OibSummary XML metadata island.</summary>
public sealed class VeeamOibSummaryInfo {
  public string? DisplayName { get; init; }
  public string? VmName { get; init; }
  public string? OibType { get; init; }
  public string? BackupType { get; init; }
  public string? Algorithm { get; init; }
  public string? CreationTimeUtc { get; init; }
  public string? CompletionTimeUtc { get; init; }
  public bool? IsCorrupted { get; init; }
  public string? SourceHostName { get; init; }
  public string? SourceHostInstanceId { get; init; }
  public string? JobName { get; init; }
  public string? PolicyName { get; init; }
  public bool? IsEncrypted { get; init; }
  public string? ObjectName { get; init; }
  public string? ObjectId { get; init; }
  public int? PointNumber { get; init; }
  public string? PointType { get; init; }
  public string? PrevFileName { get; init; }
  public string? StoragePartialPath { get; init; }
  public List<VeeamOibFile> Files { get; } = [];
}

/// <summary>A guest file recorded in the <c>OibFiles</c> list (name + uncompressed size).</summary>
public sealed record VeeamOibFile(string Name, long Size);
