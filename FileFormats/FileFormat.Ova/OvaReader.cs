using System.Security.Cryptography;
using System.Text;
using FileFormat.Tar;

namespace FileFormat.Ova;

/// <summary>
/// Walks an OVA (Open Virtualization Appliance) container — an uncompressed
/// POSIX/ustar TAR — and surfaces its members: the <c>.ovf</c> XML descriptor
/// (always the first entry), one or more disk images (<c>.vmdk</c>/<c>.vhd</c>),
/// an optional <c>.mf</c> manifest of checksums and an optional <c>.cert</c>.
/// </summary>
/// <remarks>
/// The reader is forgiving: malformed input degrades to whatever members could
/// be walked before the failure rather than throwing. Manifest verification is
/// opt-in via <see cref="VerifyManifest"/>; it parses each
/// <c>SHA256(file)= &lt;hex&gt;</c> (or SHA1) line and compares against the
/// member's actual bytes.
/// </remarks>
public sealed class OvaReader {
  /// <summary>A single member extracted from the OVA container.</summary>
  /// <param name="Name">The member's name as stored in the TAR.</param>
  /// <param name="Data">The member's raw bytes.</param>
  public sealed record OvaMember(string Name, byte[] Data);

  /// <summary>The result of verifying one manifest line against a member.</summary>
  /// <param name="FileName">The file the manifest line refers to.</param>
  /// <param name="Algorithm">The hash algorithm named in the manifest (e.g. "SHA256").</param>
  /// <param name="Expected">The hex digest recorded in the manifest.</param>
  /// <param name="Actual">The hex digest computed from the member's bytes, or null if the member is absent.</param>
  /// <param name="Matches">True when <paramref name="Actual"/> equals <paramref name="Expected"/> (case-insensitive).</param>
  public sealed record ManifestCheck(string FileName, string Algorithm, string Expected, string? Actual, bool Matches);

  private readonly List<OvaMember> _members;

  /// <summary>True when TAR parsing hit an error before reaching the end of the archive.</summary>
  public bool Partial { get; }

  private OvaReader(List<OvaMember> members, bool partial) {
    this._members = members;
    this.Partial = partial;
  }

  /// <summary>All members in archive order.</summary>
  public IReadOnlyList<OvaMember> Members => this._members;

  /// <summary>The first <c>.ovf</c> member, or null when none is present.</summary>
  public OvaMember? Ovf
    => this._members.FirstOrDefault(m => m.Name.EndsWith(".ovf", StringComparison.OrdinalIgnoreCase));

  /// <summary>The first <c>.mf</c> manifest member, or null when none is present.</summary>
  public OvaMember? Manifest
    => this._members.FirstOrDefault(m => m.Name.EndsWith(".mf", StringComparison.OrdinalIgnoreCase));

  /// <summary>Every disk-image member (<c>.vmdk</c>/<c>.vhd</c>/<c>.img</c>/<c>.vhdx</c>/<c>.iso</c>).</summary>
  public IEnumerable<OvaMember> Disks
    => this._members.Where(m => IsDisk(m.Name));

  /// <summary>True when <paramref name="name"/> has a recognised disk-image extension.</summary>
  public static bool IsDisk(string name)
    => name.EndsWith(".vmdk", StringComparison.OrdinalIgnoreCase)
       || name.EndsWith(".vhd", StringComparison.OrdinalIgnoreCase)
       || name.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase)
       || name.EndsWith(".img", StringComparison.OrdinalIgnoreCase)
       || name.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)
       || name.EndsWith(".raw", StringComparison.OrdinalIgnoreCase);

  /// <summary>
  /// Reads every TAR member of <paramref name="stream"/> into memory. Never
  /// throws on malformed input — sets <see cref="Partial"/> and keeps whatever
  /// was parsed.
  /// </summary>
  public static OvaReader Read(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    var members = new List<OvaMember>();
    var partial = false;
    try {
      if (stream.CanSeek) stream.Position = 0;
      using var r = new TarReader(stream, leaveOpen: true);
      while (r.GetNextEntry() is { } entry) {
        if (entry.IsDirectory) continue;
        using var data = r.GetEntryStream();
        using var ms = new MemoryStream();
        data.CopyTo(ms);
        members.Add(new OvaMember(entry.Name, ms.ToArray()));
      }
    } catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException or FormatException or ArgumentException) {
      partial = true;
    }
    return new OvaReader(members, partial);
  }

  /// <summary>
  /// Parses the <c>.mf</c> manifest (if any) and verifies each listed file's
  /// digest against the corresponding member's bytes. Returns an empty list
  /// when no manifest is present.
  /// </summary>
  public IReadOnlyList<ManifestCheck> VerifyManifest() {
    var manifest = this.Manifest;
    if (manifest == null) return [];

    var checks = new List<ManifestCheck>();
    var text = Encoding.UTF8.GetString(manifest.Data);
    foreach (var line in ParseManifest(text)) {
      var member = this._members.FirstOrDefault(m =>
        string.Equals(m.Name, line.FileName, StringComparison.OrdinalIgnoreCase));
      string? actual = member == null ? null : ComputeHex(line.Algorithm, member.Data);
      var matches = actual != null && string.Equals(actual, line.Expected, StringComparison.OrdinalIgnoreCase);
      checks.Add(new ManifestCheck(line.FileName, line.Algorithm, line.Expected, actual, matches));
    }
    return checks;
  }

  /// <summary>True when a manifest exists and every line it lists verifies.</summary>
  public bool ManifestVerifies() {
    var checks = this.VerifyManifest();
    return checks.Count > 0 && checks.All(c => c.Matches);
  }

  /// <summary>
  /// Parses manifest lines of the form <c>ALGO(file)= hex</c> (whitespace after
  /// the <c>=</c> is optional). Lines that don't match are skipped.
  /// </summary>
  public static IEnumerable<(string Algorithm, string FileName, string Expected)> ParseManifest(string text) {
    foreach (var raw in text.Split('\n')) {
      var line = raw.Trim();
      if (line.Length == 0) continue;
      var open = line.IndexOf('(');
      var close = line.IndexOf(')', open + 1);
      var eq = close < 0 ? -1 : line.IndexOf('=', close + 1);
      if (open <= 0 || close < 0 || eq < 0) continue;
      var algo = line[..open].Trim();
      var file = line[(open + 1)..close].Trim();
      var hex = line[(eq + 1)..].Trim();
      if (algo.Length == 0 || file.Length == 0 || hex.Length == 0) continue;
      yield return (algo, file, hex);
    }
  }

  /// <summary>Computes a lowercase hex digest of <paramref name="data"/> using the named algorithm.</summary>
  internal static string ComputeHex(string algorithm, byte[] data) {
    var hash = algorithm.Replace("-", "").ToUpperInvariant() switch {
      "SHA1" => SHA1.HashData(data),
      "SHA256" => SHA256.HashData(data),
      "SHA512" => SHA512.HashData(data),
      "MD5" => MD5.HashData(data),
      _ => SHA256.HashData(data),
    };
    return Convert.ToHexStringLower(hash);
  }
}
