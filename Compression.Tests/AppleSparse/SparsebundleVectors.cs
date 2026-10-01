namespace Compression.Tests.AppleSparse;

/// <summary>
/// The hdiutil-written bundles under <c>AppleSparse/ReferenceVectors</c>, embedded file by file
/// and rebuilt here as real bundle directories (provenance in that folder's README).
/// </summary>
internal static class SparsebundleVectors {

  public const string HfsPlus = "hfsplus.sparsebundle";
  public const string Ewfprobe = "dmg-sparsebundle.sparsebundle";

  private const string Prefix = "SparsebundleVectors/";

  /// <summary>Writes the named bundle below <paramref name="parent"/> and returns its root.</summary>
  public static string Materialize(string bundle, string parent) {
    var assembly = typeof(SparsebundleVectors).Assembly;
    var root = Path.Combine(parent, bundle);
    var found = false;
    foreach (var name in assembly.GetManifestResourceNames()) {
      var logical = name.Replace('\\', '/');
      if (!logical.StartsWith(Prefix + bundle + "/", StringComparison.Ordinal)) continue;
      var relative = logical[(Prefix.Length + bundle.Length + 1)..];
      var target = Path.Combine([root, .. relative.Split('/')]);
      Directory.CreateDirectory(Path.GetDirectoryName(target)!);
      using var resource = assembly.GetManifestResourceStream(name)!;
      using var file = File.Create(target);
      resource.CopyTo(file);
      found = true;
    }
    if (!found) throw new InvalidOperationException($"Reference bundle {bundle} is not embedded.");
    // Empty directories do not survive embedding; hdiutil always writes bands/.
    Directory.CreateDirectory(Path.Combine(root, "bands"));
    return root;
  }
}
