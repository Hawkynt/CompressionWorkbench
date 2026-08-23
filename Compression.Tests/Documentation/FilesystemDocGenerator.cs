#pragma warning disable CS1591
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using Compression.Registry;

namespace Compression.Tests.Documentation;

/// <summary>
/// Builds one page per filesystem out of what the implementation itself says.
/// </summary>
/// <remarks>
/// <para>The alternative — writing the pages by hand — produces documentation
/// that is correct on the day it is written and quietly wrong afterwards. Every
/// fact here is read back from the descriptor at runtime or from the XML
/// documentation the compiler emits for it, so a page cannot claim a verb the
/// format does not offer or a parameter it does not take.</para>
///
/// <para>The prose comes from the doc comments on the descriptor, the reader
/// and the writer, which is where the layout is described and the references
/// are cited. Improving a page means improving those comments, which is where
/// someone reading the code will look anyway.</para>
/// </remarks>
public static class FilesystemDocGenerator {

  /// <summary>The verbs a page reports, and the capability behind each.</summary>
  private static readonly (string Verb, Type Marker, string What)[] Verbs = [
    ("list / extract", typeof(IArchiveFormatOperations), "read the volume and copy files out of it"),
    ("create", typeof(IArchiveCreatable), "write a fresh volume holding the given files"),
    ("add / remove", typeof(IArchiveModifiable), "change a volume in place"),
    ("defragment", typeof(IArchiveDefragmentable), "lay the volume out again"),
    ("wipe free space", typeof(IWipeEmpty), "zero what no file holds"),
    ("shrink", typeof(IArchiveShrinkable), "reduce the volume to what it needs"),
    ("optimise layout", typeof(ILayoutOptimizable), "re-lay the volume at a chosen geometry"),
    ("report layout", typeof(IFilesystemExtentMap), "say where every byte belongs"),
    ("move blocks", typeof(IFilesystemBlockMover), "relocate a run and repoint what names it"),
    ("move metadata", typeof(IFilesystemMetadataMover), "relocate the volume's own structures"),
  ];

  /// <summary>Every filesystem descriptor, in a stable order.</summary>
  public static IReadOnlyList<IFormatDescriptor> Filesystems() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    return FormatRegistry.All
      .Where(d => d.GetType().Assembly.GetName().Name?.StartsWith("FileSystem.", StringComparison.Ordinal) == true)
      .OrderBy(d => d.Id, StringComparer.Ordinal)
      .ToList();
  }

  /// <summary>The index of every page, with what each format can do.</summary>
  public static string Index() {
    var page = new StringBuilder();
    page.AppendLine("# Filesystems");
    page.AppendLine();
    page.AppendLine("One page per filesystem, generated from the implementation: the verbs it");
    page.AppendLine("offers, how a volume is laid out, what parameters it takes and where the");
    page.AppendLine("format is documented. A test regenerates them and fails on drift, so a page");
    page.AppendLine("cannot claim something the code does not do.");
    page.AppendLine();
    page.AppendLine("| Filesystem | Defragments by | Wipes | Edits in place |");
    page.AppendLine("|---|---|---|---|");

    foreach (var descriptor in Filesystems()) {
      var ops = FormatRegistry.GetArchiveOps(descriptor.Id);
      var how = ops is not IArchiveDefragmentable ? "—"
              : MoverFor(descriptor) is { } mover ? "moving (`" + mover.Name + "`)"
              : "rebuilding";
      page.Append("| [").Append(descriptor.DisplayName).Append("](").Append(descriptor.Id).Append(".md) | ")
          .Append(how).Append(" | ").Append(ops is IWipeEmpty ? "yes" : "no")
          .Append(" | ").Append(ops is IArchiveModifiable ? "yes" : "no").AppendLine(" |");
    }

    page.AppendLine();
    return page.ToString().ReplaceLineEndings("\n");
  }

  /// <summary>The page for one filesystem.</summary>
  public static string Page(IFormatDescriptor descriptor) {
    ArgumentNullException.ThrowIfNull(descriptor);
    var ops = FormatRegistry.GetArchiveOps(descriptor.Id);
    var docs = XmlDocs.For(descriptor.GetType().Assembly);
    var page = new StringBuilder();

    page.Append("# ").Append(descriptor.DisplayName).Append(" (`").Append(descriptor.Id).AppendLine("`)");
    page.AppendLine();
    page.AppendLine(descriptor.Description);
    page.AppendLine();
    page.AppendLine("> Generated from the implementation. Edit the doc comments on the descriptor,");
    page.AppendLine("> reader or writer rather than this file; a test regenerates it and fails on drift.");
    page.AppendLine();

    AppendAtAGlance(page, descriptor);
    AppendDetection(page, descriptor);
    AppendVerbs(page, descriptor, ops);
    AppendLayout(page, descriptor, docs);
    AppendParameters(page, ops);
    AppendMethods(page, descriptor);
    AppendReferences(page, descriptor, docs);

    return page.ToString().ReplaceLineEndings("\n");
  }

  private static void AppendAtAGlance(StringBuilder page, IFormatDescriptor descriptor) {
    page.AppendLine("## At a glance");
    page.AppendLine();
    page.AppendLine("| | |");
    page.AppendLine("|---|---|");
    page.Append("| Category | ").Append(descriptor.Category).AppendLine(" |");
    page.Append("| Family | ").Append(descriptor.Family).AppendLine(" |");
    page.Append("| Default extension | `").Append(descriptor.DefaultExtension).AppendLine("` |");
    var extensions = descriptor.Extensions.Count > 0
      ? string.Join(", ", descriptor.Extensions.Select(e => "`" + e + "`"))
      : "none";
    page.Append("| Recognised extensions | ").Append(extensions).AppendLine(" |");
    if (descriptor.CompoundExtensions.Count > 0)
      page.Append("| Compound extensions | ")
          .Append(string.Join(", ", descriptor.CompoundExtensions.Select(e => "`" + e + "`")))
          .AppendLine(" |");
    page.AppendLine();
  }

  private static void AppendDetection(StringBuilder page, IFormatDescriptor descriptor) {
    page.AppendLine("## Detection");
    page.AppendLine();
    if (descriptor.MagicSignatures.Count == 0) {
      page.AppendLine("No byte signature: this format is recognised by its extension and by the");
      page.AppendLine("reader accepting the volume's own structures.");
      page.AppendLine();
      return;
    }

    page.AppendLine("| Bytes | At offset | Confidence |");
    page.AppendLine("|---|---|---|");
    foreach (var magic in descriptor.MagicSignatures) {
      var bytes = string.Join(" ", magic.Bytes.Select(b => b.ToString("X2")));
      page.Append("| `").Append(bytes).Append("` | ").Append(magic.Offset)
          .Append(" | ").Append(magic.Confidence.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))
          .AppendLine(" |");
    }
    page.AppendLine();
  }

  private static void AppendVerbs(StringBuilder page, IFormatDescriptor descriptor, object? ops) {
    page.AppendLine("## Verbs");
    page.AppendLine();
    page.AppendLine("| Verb | Offered | What it does |");
    page.AppendLine("|---|---|---|");
    foreach (var (verb, marker, what) in Verbs) {
      var offered = ops != null && marker.IsInstanceOfType(ops) || marker.IsInstanceOfType(descriptor);
      page.Append("| ").Append(verb).Append(" | ").Append(offered ? "yes" : "no")
          .Append(" | ").Append(what).AppendLine(" |");
    }
    page.AppendLine();

    AppendDefragMechanism(page, descriptor);
  }

  /// <summary>How this format changes a layout, which is the thing most worth knowing.</summary>
  private static void AppendDefragMechanism(StringBuilder page, IFormatDescriptor descriptor) {
    var mover = MoverFor(descriptor);
    page.AppendLine("### How it defragments");
    page.AppendLine();
    if (!typeof(IArchiveDefragmentable).IsInstanceOfType(FormatRegistry.GetArchiveOps(descriptor.Id))) {
      page.AppendLine("It does not.");
      page.AppendLine();
      return;
    }

    if (mover == null) {
      page.AppendLine("By rebuilding: every file is read out and a fresh volume is written in the");
      page.AppendLine("order the requested layout asks for. Correct, but it costs the whole payload.");
      page.AppendLine();
      return;
    }

    page.Append("By moving what is out of place, through `").Append(mover.Name).AppendLine("`.");
    page.AppendLine("A run is copied and whatever records its position is rewritten, so the cost is");
    page.AppendLine("the bytes that actually move rather than the whole volume.");
    page.AppendLine();

    var instance = TryCreate(mover);
    if (instance == null) { page.AppendLine(); return; }

    page.AppendLine("| Property | Value | Meaning |");
    page.AppendLine("|---|---|---|");
    page.Append("| Repoints runs independently | ").Append(instance.RepointsRunsIndependently ? "yes" : "no")
        .AppendLine(" | whether a file in several pieces can be moved one piece at a time |");
    page.Append("| Relinks a whole allocation | ").Append(instance.SupportsScatteredRelink ? "yes" : "no")
        .AppendLine(" | whether a scattered file's chain can be restated in one call |");
    page.Append("| Holds runs outside the volume | ").Append(instance.SupportsHeldRuns ? "yes" : "no")
        .AppendLine(" | whether a full volume can be rearranged by lifting a run into memory |");
    page.AppendLine();
  }

  private static Type? MoverFor(IFormatDescriptor descriptor)
    => descriptor.GetType().Assembly.GetTypes()
        .FirstOrDefault(t => !t.IsAbstract && t.IsClass && typeof(IFilesystemBlockMover).IsAssignableFrom(t));

  private static IFilesystemBlockMover? TryCreate(Type mover) {
    try { return Activator.CreateInstance(mover) as IFilesystemBlockMover; } catch { return null; }
  }

  private static void AppendLayout(StringBuilder page, IFormatDescriptor descriptor, XmlDocs docs) {
    page.AppendLine("## How a volume is laid out");
    page.AppendLine();

    var written = false;
    foreach (var type in LayoutTypes(descriptor)) {
      var prose = docs.Prose("T:" + type.FullName);
      if (string.IsNullOrWhiteSpace(prose)) continue;
      page.Append("### ").AppendLine(type.Name);
      page.AppendLine();
      page.AppendLine(prose);
      page.AppendLine();
      written = true;
    }

    if (!written) {
      page.AppendLine("The implementation carries no description of the on-disk structures. Adding");
      page.AppendLine("one to the reader's doc comment will bring it through to here.");
      page.AppendLine();
    }
  }

  /// <summary>The types whose doc comments describe the on-disk structures.</summary>
  /// <remarks>
  /// <para>Nested types are passed over and the rest are put in a fixed order,
  /// because <see cref="Assembly.GetTypes" /> promises no order at all and more
  /// than one type can end in the same word. Btrfs has two ending in "Writer" —
  /// the writer, and a <c>BoundedWriter</c> nested inside the reader — and
  /// LittleFs has three ending in "Layout". Taking the first of them meant taking
  /// whichever the runtime happened to hand over first, and the helper types
  /// carry no doc comment, so the section they won simply disappeared.</para>
  ///
  /// <para>That is how two pages passed here and failed on the build machine for
  /// weeks: the page was not wrong on either, it was written on one machine and
  /// checked on another that made a different arbitrary choice.</para>
  /// </remarks>
  private static IEnumerable<Type> LayoutTypes(IFormatDescriptor descriptor) {
    var assembly = descriptor.GetType().Assembly;
    var seen = new HashSet<Type> { descriptor.GetType() };
    yield return descriptor.GetType();

    // What this filesystem's own types are called: the descriptor's name without
    // the word that makes it a descriptor.
    var family = descriptor.GetType().Name;
    foreach (var tail in new[] { "FormatDescriptor", "Descriptor" })
      if (family.EndsWith(tail, StringComparison.Ordinal)) {
        family = family[..^tail.Length];
        break;
      }

    foreach (var suffix in new[] { "Reader", "Writer", "ExtentMap", "Layout" }) {
      var type = assembly.GetTypes()
        .Where(t => t.IsClass && !t.IsNested && t.Name.EndsWith(suffix, StringComparison.Ordinal))
        // The one named after this filesystem is the one the page is about, and
        // ties break the same way on every machine.
        .OrderByDescending(t => string.Equals(t.Name, family + suffix, StringComparison.Ordinal))
        .ThenByDescending(t => t.Name.StartsWith(family, StringComparison.Ordinal))
        // Where a filesystem shares its assembly with a relative and has no type
        // of its own, the nearest-named relative is the one worth describing.
        .ThenByDescending(t => SharedPrefix(t.Name, family))
        .ThenBy(t => t.Name.Length)
        .ThenBy(t => t.FullName, StringComparer.Ordinal)
        .FirstOrDefault();
      if (type != null && seen.Add(type)) yield return type;
    }
  }

  /// <summary>How many leading characters two names have in common.</summary>
  private static int SharedPrefix(string name, string family) {
    var shared = 0;
    while (shared < name.Length && shared < family.Length && name[shared] == family[shared]) ++shared;
    return shared;
  }

  private static void AppendParameters(StringBuilder page, object? ops) {
    if (ops is not IFormatOptionsSchema schema || schema.OptionsSchema.Count == 0) return;

    page.AppendLine("## Parameters");
    page.AppendLine();
    page.AppendLine("| Key | Kind | Default | Allowed | Meaning |");
    page.AppendLine("|---|---|---|---|---|");
    foreach (var option in schema.OptionsSchema.OrderBy(o => o.Key, StringComparer.Ordinal)) {
      var allowed = option.AllowedValues is { Count: > 0 }
        ? string.Join(", ", option.AllowedValues.Select(v => "`" + v + "`"))
        : "any";
      page.Append("| `").Append(option.Key).Append("` | ").Append(option.Kind)
          .Append(" | `").Append(option.Default).Append("` | ").Append(allowed)
          .Append(" | ").Append(Flatten(option.Description ?? option.DisplayName)).AppendLine(" |");
    }
    page.AppendLine();
  }

  private static void AppendMethods(StringBuilder page, IFormatDescriptor descriptor) {
    if (descriptor.Methods.Count == 0) return;
    page.AppendLine("## Storage methods");
    page.AppendLine();
    foreach (var method in descriptor.Methods)
      page.Append("- `").Append(method.Name).Append("` — ").AppendLine(Flatten(method.DisplayName));
    page.AppendLine();
  }

  private static void AppendReferences(StringBuilder page, IFormatDescriptor descriptor, XmlDocs docs) {
    // Only the descriptor's own list: that is where the convention puts cited
    // sources. A reader or writer may also carry a bulleted list, but those
    // describe a record's byte layout, and printing them here as reading
    // material is worse than printing nothing.
    var references = docs.References("T:" + descriptor.GetType().FullName).ToList();

    page.AppendLine("## Further reading");
    page.AppendLine();
    if (references.Count == 0) {
      page.AppendLine("The implementation cites no sources. Adding a `<list type=\"bullet\">` of them");
      page.AppendLine("to the descriptor's doc comment will bring them through to here.");
      page.AppendLine();
      return;
    }

    foreach (var reference in references.Distinct(StringComparer.Ordinal))
      page.Append("- ").AppendLine(reference);
    page.AppendLine();
  }

  internal static string Flatten(string text)
    => string.Join(" ", text.Split('\n', '\r').Select(l => l.Trim()).Where(l => l.Length > 0))
             .Replace("|", "\\|", StringComparison.Ordinal);
}
