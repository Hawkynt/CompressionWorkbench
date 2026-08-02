#pragma warning disable CS1591
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Xml.Linq;

namespace Compression.Tests.Documentation;

/// <summary>
/// The XML documentation the compiler emits beside an assembly, read back so a
/// generated page can quote what the code says about itself.
/// </summary>
public sealed class XmlDocs {

  private static readonly ConcurrentDictionary<string, XmlDocs> Cache = new(StringComparer.Ordinal);
  private readonly Dictionary<string, XElement> _members = new(StringComparer.Ordinal);

  private XmlDocs(string? path) {
    if (path == null || !File.Exists(path)) return;
    XDocument document;
    try { document = XDocument.Load(path); } catch { return; }
    foreach (var member in document.Descendants("member")) {
      var name = member.Attribute("name")?.Value;
      if (name != null) this._members[name] = member;
    }
  }

  public static XmlDocs For(Assembly assembly) {
    ArgumentNullException.ThrowIfNull(assembly);
    var location = assembly.Location;
    return Cache.GetOrAdd(location, _ => new XmlDocs(
      string.IsNullOrEmpty(location) ? null : Path.ChangeExtension(location, ".xml")));
  }

  /// <summary>The summary and remarks of a member, as flowing paragraphs.</summary>
  public string Prose(string memberName) {
    if (!this._members.TryGetValue(memberName, out var member)) return "";

    var paragraphs = new List<string>();
    foreach (var section in new[] { "summary", "remarks" })
      foreach (var element in member.Elements(section))
        paragraphs.AddRange(Paragraphs(element));

    return string.Join("\n\n", paragraphs.Where(p => p.Length > 0));
  }

  /// <summary>The bulleted items a member cites as sources.</summary>
  public IEnumerable<string> References(string memberName) {
    if (!this._members.TryGetValue(memberName, out var member)) yield break;

    foreach (var list in member.Descendants("list")) {
      foreach (var item in list.Elements("item")) {
        // Not Text: that one deliberately ignores everything inside a list, so
        // a citation would come back empty.
        var text = ItemText(item);
        if (text.Length > 0) yield return text;
      }
    }
  }

  /// <summary>
  /// The paragraphs of a doc section. A <c>&lt;para&gt;</c> is one; anything
  /// outside them is another; a <c>&lt;list&gt;</c> is left to the references.
  /// </summary>
  private static IEnumerable<string> Paragraphs(XElement section) {
    var loose = new StringBuilder();
    foreach (var node in section.Nodes()) {
      if (node is XElement element) {
        if (element.Name == "para") {
          var flushed = Normalise(loose.ToString());
          if (flushed.Length > 0) yield return flushed;
          loose.Clear();
          var text = Text(element);
          if (text.Length > 0) yield return text;
          continue;
        }
        if (element.Name == "list") continue;              // cited sources, not prose
        loose.Append(Inline(element));
        continue;
      }
      loose.Append(node.ToString());
    }

    var tail = Normalise(loose.ToString());
    if (tail.Length > 0) yield return tail;
  }

  private static string Text(XElement element) {
    var text = new StringBuilder();
    foreach (var node in element.DescendantNodes()) {
      // A list inside prose is a structure table or a citation list; both read
      // as a run-on sentence when flattened into the paragraph around them.
      if (node.Ancestors().Any(a => a.Name == "list")) continue;
      if (node is XText raw) text.Append(raw.Value);
      else if (node is XElement child && IsCrossReference(child.Name.LocalName))
        text.Append(Inline(child));
    }
    return Normalise(text.ToString());
  }

  /// <summary>One list item, flattened.</summary>
  private static string ItemText(XElement item) {
    var text = new StringBuilder();
    foreach (var node in item.DescendantNodes()) {
      if (node is XText raw) text.Append(raw.Value);
      else if (node is XElement child && IsCrossReference(child.Name.LocalName))
        text.Append(Inline(child));
    }
    return Normalise(text.ToString());
  }

  /// <summary>Whether an element names something rather than saying something.</summary>
  private static bool IsCrossReference(string name)
    => name is "see" or "seealso" or "paramref" or "typeparamref";

  /// <summary>A cross-reference rendered as the thing it names.</summary>
  private static string Inline(XElement element) {
    if (element.Name == "c" || element.Name == "code") return "`" + element.Value.Trim() + "`";
    var target = element.Attribute("cref")?.Value ?? element.Attribute("name")?.Value;
    if (target == null) return element.Value;

    // A cref is "T:Namespace.Type" or "M:Namespace.Type.Member(argument,...)".
    // Taking the text after the last dot picks a fragment of the argument list
    // on the member forms, which reads as a different type altogether.
    if (target.Length > 1 && target[1] == ':') target = target[2..];
    var parenthesis = target.IndexOf('(');
    if (parenthesis >= 0) target = target[..parenthesis];
    var cut = target.LastIndexOf('.');
    return "`" + (cut >= 0 ? target[(cut + 1)..] : target) + "`";
  }

  /// <summary>Doc comments arrive wrapped and indented; markdown wants flow.</summary>
  private static string Normalise(string text) {
    var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    return string.Join(" ", words);
  }
}
