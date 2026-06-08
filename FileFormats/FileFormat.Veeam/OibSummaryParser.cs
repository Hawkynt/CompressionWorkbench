#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace FileFormat.Veeam;

/// <summary>
/// Locates and parses the embedded <c>&lt;OibSummary&gt;</c> XML metadata
/// island that Veeam Backup &amp; Replication writes near the end of an
/// unencrypted Storage file (<c>.vbk</c> / <c>.vib</c> / <c>.vrb</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Strategy.</b>
/// </para>
/// <list type="number">
/// <item>
/// <description>
/// Scan the input byte buffer for ALL occurrences of <c>&lt;OibSummary&gt;</c>
/// and pick the LAST one — this matches the
/// <see href="https://docs.velociraptor.app/exchange/artifacts/pages/windows.veeam.restorepoints.backupfiles/">Velociraptor
/// <c>StartOffsetRule</c></see> from Synacktiv's
/// <see href="https://www.synacktiv.com/en/publications/using-veeam-metadata-for-efficient-extraction-of-backup-artefacts-23">research
/// pipeline</see>: real containers may contain earlier inline copies inside
/// compressed metadata banks; the trailing copy is the authoritative one
/// Veeam writes alongside the closing chunk table.
/// </description>
/// </item>
/// <item>
/// <description>
/// Locate the FIRST <c>&lt;/OibSummary&gt;</c> after that offset, mirroring
/// Velociraptor's <c>EndOffsetRule</c>. The byte range between the two
/// tags (inclusive) is the XML island.
/// </description>
/// </item>
/// <item>
/// <description>
/// Decode the island as UTF-8 plain text and parse it with
/// <see cref="XDocument"/>. Map the documented elements/attributes
/// (<c>Backup/@JobName</c>, <c>Point/@Num</c>,
/// <c>Storage/@PartialPath</c>, <c>OIB/@DisplayName</c>,
/// <c>SourceHost/@Name</c>, <c>Object/@Name</c>, <c>Object/@Id</c>,
/// <c>PrevFileName</c>, <c>BackupVersion</c>, <c>OibFiles/File</c>) onto
/// <see cref="OibSummary"/>.
/// </description>
/// </item>
/// </list>
/// <para>
/// <b>Honest scope.</b> The parser is best-effort: unknown element shapes
/// surface as <c>null</c> fields, never as exceptions. Real disk content
/// stays Stage 0 because the OibSummary XML only documents the chain
/// topology and platform-side identifiers; the actual block bodies are
/// still gated by (a) CBT chain replay across the full <c>.vbm</c>
/// metadata index, (b) the external job-scoped dedup pool, and
/// (c) AES-256 encryption when the job is configured to use it.
/// </para>
/// </remarks>
public static class OibSummaryParser {

  /// <summary>UTF-8 bytes for the <c>&lt;OibSummary&gt;</c> open tag.</summary>
  public static readonly byte[] OpenTag = "<OibSummary>"u8.ToArray();

  /// <summary>UTF-8 bytes for the <c>&lt;/OibSummary&gt;</c> close tag.</summary>
  public static readonly byte[] CloseTag = "</OibSummary>"u8.ToArray();

  /// <summary>
  /// Attempts to locate and parse the trailing OibSummary XML island inside
  /// <paramref name="data"/>. Returns <c>null</c> when no usable XML is
  /// found (encrypted containers, truncated files, or pre-Synacktiv writer
  /// versions that did not embed the trailer).
  /// </summary>
  public static OibSummary? TryParse(ReadOnlySpan<byte> data) {
    var openIdx = LastIndexOf(data, OpenTag);
    if (openIdx < 0) return null;

    var afterOpen = openIdx + OpenTag.Length;
    if (afterOpen >= data.Length) return null;

    var closeRelative = IndexOf(data.Slice(afterOpen), CloseTag);
    if (closeRelative < 0) return null;

    var closeIdx = afterOpen + closeRelative;
    var islandLength = closeIdx + CloseTag.Length - openIdx;
    var islandBytes = data.Slice(openIdx, islandLength);

    string rawXml;
    XDocument doc;
    try {
      rawXml = Encoding.UTF8.GetString(islandBytes);
      doc = XDocument.Parse(rawXml, LoadOptions.PreserveWhitespace);
    } catch {
      // Trailer may carry stray control bytes the XML decoder rejects, or
      // a writer-specific encoding. Surface as "not parseable" rather than
      // throwing — Stage-0 detection still works, Stage-1 just degrades.
      return null;
    }

    var root = doc.Root;
    if (root == null || !string.Equals(root.Name.LocalName, "OibSummary", StringComparison.Ordinal))
      return null;

    var backup = ChildOrNull(root, "Backup");
    var point = ChildOrNull(root, "Point");
    var storage = ChildOrNull(root, "Storage");
    var oib = ChildOrNull(root, "OIB") ?? ChildOrNull(root, "Oib");
    var obj = ChildOrNull(root, "Object");
    var srcHost = ChildOrNull(root, "SourceHost");
    var tgtHost = ChildOrNull(root, "TargetHost");

    return new OibSummary {
      JobName = AttrOrNull(backup, "JobName"),
      PolicyName = AttrOrNull(backup, "PolicyName"),
      BackupTypeCode = AttrIntOrNull(backup, "Type"),
      EncryptionCode = AttrIntOrNull(backup, "Encryption"),
      EncryptionStateCode = AttrIntOrNull(backup, "EncryptionState"),
      RestorePointNumber = AttrIntOrNull(point, "Num"),
      RestorePointTypeCode = AttrIntOrNull(point, "Type"),
      CreationTime = AttrOrNull(point, "CreationTime"),
      CreationTimeUtc = AttrOrNull(point, "CreationTimeUtc"),
      StoragePartialPath = AttrOrNull(storage, "PartialPath"),
      OibDisplayName = AttrOrNull(oib, "DisplayName"),
      OibVmName = AttrOrNull(oib, "VmName"),
      OibState = AttrOrNull(oib, "State"),
      OibType = AttrOrNull(oib, "Type"),
      OibAlgorithm = AttrOrNull(oib, "Algorithm"),
      OibHealthStatus = AttrOrNull(oib, "HealthStatus"),
      OibCreationTimeUtc = AttrOrNull(oib, "CreationTimeUtc"),
      OibCompletionTimeUtc = AttrOrNull(oib, "CompletionTimeUtc"),
      OibApproxSize = AttrLongOrNull(oib, "ApproxSize"),
      OibEffectiveMemoryMb = AttrLongOrNull(oib, "EffectiveMemoryMb"),
      OibAuxDataRaw = AttrOrNull(oib, "AuxData"),
      OibHasIndex = AttrOrNull(oib, "HasIndex"),
      OibHasExchange = AttrOrNull(oib, "HasExchange"),
      OibHasSharePoint = AttrOrNull(oib, "HasSharePoint"),
      OibHasSql = AttrOrNull(oib, "HasSql"),
      OibHasAd = AttrOrNull(oib, "HasAd"),
      OibHasOracle = AttrOrNull(oib, "HasOracle"),
      OibHasPostgreSql = AttrOrNull(oib, "HasPostgreSql"),
      OibHasVeeamArchiver = AttrOrNull(oib, "HasVeeamArchiver"),
      OibIsCorrupted = AttrOrNull(oib, "IsCorrupted"),
      OibIsRecheckCorrupted = AttrOrNull(oib, "IsRecheckCorrupted"),
      OibIsConsistent = AttrOrNull(oib, "IsConsistent"),
      OibIsPartialActiveFull = AttrOrNull(oib, "IsPartialActiveFull"),
      OibProductVersion = AttrOrNull(oib, "ProductVersion"),
      OibProductVersionFlags = AttrOrNull(oib, "ProductVersionFlags"),
      OibProductIsRentalLicense = AttrOrNull(oib, "ProductIsRentalLicense"),
      ObjectName = AttrOrNull(obj, "Name"),
      ObjectId = AttrOrNull(obj, "Id"),
      ObjectIdNew = AttrOrNull(obj, "ObjectId"),
      ObjectViType = AttrOrNull(obj, "ViType"),
      SourceHostName = AttrOrNull(srcHost, "Name"),
      SourceHostInstanceId = AttrOrNull(srcHost, "HostInstanceId"),
      TargetHostName = AttrOrNull(tgtHost, "Name"),
      PrevFileName = ChildTextOrNull(root, "PrevFileName"),
      BackupVersion = ChildTextOrNull(root, "BackupVersion"),
      OibFiles = ParseOibFiles(root),
      XmlOffset = openIdx,
      XmlLength = islandLength,
      RawXml = rawXml,
    };
  }

  private static IReadOnlyList<OibFileEntry> ParseOibFiles(XElement root) {
    var oibFiles = ChildOrNull(root, "OibFiles");
    if (oibFiles == null) return [];
    var list = new List<OibFileEntry>();
    foreach (var fileEl in oibFiles.Elements()) {
      if (!string.Equals(fileEl.Name.LocalName, "File", StringComparison.Ordinal))
        continue;
      var platform = new Dictionary<string, string>(StringComparer.Ordinal);
      var platformEl = ChildOrNull(fileEl, "PlatformDetails");
      if (platformEl != null)
        foreach (var a in platformEl.Attributes())
          platform[a.Name.LocalName] = a.Value;
      list.Add(new OibFileEntry {
        Name = AttrOrNull(fileEl, "Name"),
        Size = AttrLongOrNull(fileEl, "Size"),
        PlatformDetails = platform,
      });
    }
    return list;
  }

  private static XElement? ChildOrNull(XElement? parent, string localName) {
    if (parent == null) return null;
    foreach (var child in parent.Elements())
      if (string.Equals(child.Name.LocalName, localName, StringComparison.Ordinal))
        return child;
    return null;
  }

  private static string? AttrOrNull(XElement? el, string name) {
    if (el == null) return null;
    var a = el.Attribute(name);
    return a?.Value;
  }

  private static int? AttrIntOrNull(XElement? el, string name) {
    var raw = AttrOrNull(el, name);
    if (raw == null) return null;
    return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
  }

  private static long? AttrLongOrNull(XElement? el, string name) {
    var raw = AttrOrNull(el, name);
    if (raw == null) return null;
    return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
  }

  private static string? ChildTextOrNull(XElement parent, string localName) {
    var el = ChildOrNull(parent, localName);
    if (el == null) return null;
    var v = el.Value;
    return string.IsNullOrEmpty(v) ? null : v;
  }

  // Two simple in-buffer byte scanners. We deliberately keep these
  // dependency-free instead of pulling in MemoryExtensions.IndexOf on
  // ReadOnlySpan<byte> with a multi-byte needle — older targets without
  // SearchValues<byte> would silently fall back to char-only overloads.
  private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle) {
    if (needle.Length == 0 || haystack.Length < needle.Length) return -1;
    var last = haystack.Length - needle.Length;
    for (var i = 0; i <= last; ++i) {
      var match = true;
      for (var j = 0; j < needle.Length; ++j)
        if (haystack[i + j] != needle[j]) { match = false; break; }
      if (match) return i;
    }
    return -1;
  }

  private static int LastIndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle) {
    if (needle.Length == 0 || haystack.Length < needle.Length) return -1;
    for (var i = haystack.Length - needle.Length; i >= 0; --i) {
      var match = true;
      for (var j = 0; j < needle.Length; ++j)
        if (haystack[i + j] != needle[j]) { match = false; break; }
      if (match) return i;
    }
    return -1;
  }
}
