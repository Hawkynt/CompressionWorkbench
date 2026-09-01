using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text;
using System.Text.RegularExpressions;

var roots = new[] {
  "Compression.Core",
  "Codecs",
  "FileFormats",
  "FileSystems",
  "Hawkynt.Algorithms.Checksums",
  "Hawkynt.Algorithms.Hashing",
};

var changed = new List<string>();
foreach (var root in roots.Where(Directory.Exists)) {
  foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
             .Where(p => !p.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))) {
    var source = File.ReadAllText(path);
    var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));
    var rewritten = new ApiDocRewriter().Visit(tree.GetRoot())!;
    var text = rewritten.ToFullString();
    if (text == source)
      continue;
    File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    changed.Add(path);
  }
}

Console.WriteLine($"Updated {changed.Count} C# source files.");
foreach (var path in changed)
  Console.WriteLine(path);

sealed partial class ApiDocRewriter : CSharpSyntaxRewriter {
  private readonly Stack<bool> _externallyVisibleTypes = new();

  private bool CurrentTypeIsExternallyVisible => this._externallyVisibleTypes.TryPeek(out var value) && value;

  public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
    => this.VisitType(node, node.Identifier.ValueText, base.VisitClassDeclaration);

  public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node)
    => this.VisitType(node, node.Identifier.ValueText, base.VisitStructDeclaration);

  public override SyntaxNode? VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
    => this.VisitType(node, node.Identifier.ValueText, base.VisitInterfaceDeclaration);

  public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node) {
    var visible = this.IsTypeExternallyVisible(node.Modifiers);
    var documented = visible ? AddSummary(node, TypeSummary(node.Identifier.ValueText, "record")) : node;
    this._externallyVisibleTypes.Push(visible);
    var result = base.VisitRecordDeclaration(documented);
    this._externallyVisibleTypes.Pop();
    return result;
  }

  public override SyntaxNode? VisitEnumDeclaration(EnumDeclarationSyntax node) {
    var visible = this.IsTypeExternallyVisible(node.Modifiers);
    var documented = visible ? AddSummary(node, $"Specifies {Humanize(node.Identifier.ValueText).ToLowerInvariant()} values.") : node;
    this._externallyVisibleTypes.Push(visible);
    var result = base.VisitEnumDeclaration(documented);
    this._externallyVisibleTypes.Pop();
    return result;
  }

  public override SyntaxNode? VisitEnumMemberDeclaration(EnumMemberDeclarationSyntax node) {
    if (!this.CurrentTypeIsExternallyVisible)
      return base.VisitEnumMemberDeclaration(node);
    var documented = AddSummary(node, EnumValueSummary(node.Identifier.ValueText));
    return base.VisitEnumMemberDeclaration(documented);
  }

  public override SyntaxNode? VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
    => this.VisitMember(node, $"Initializes a new instance of <see cref=\"{node.Identifier.ValueText}\"/>.", base.VisitConstructorDeclaration);

  public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    => this.VisitMember(node, MethodSummary(node.Identifier.ValueText), base.VisitMethodDeclaration);

  public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax node)
    => this.VisitMember(node, PropertySummary(node.Identifier.ValueText, node.Type, node.AccessorList), base.VisitPropertyDeclaration);

  public override SyntaxNode? VisitIndexerDeclaration(IndexerDeclarationSyntax node)
    => this.VisitMember(node, "Gets or sets the value at the specified index.", base.VisitIndexerDeclaration);

  public override SyntaxNode? VisitEventDeclaration(EventDeclarationSyntax node)
    => this.VisitMember(node, $"Occurs when {Humanize(node.Identifier.ValueText).ToLowerInvariant()}.", base.VisitEventDeclaration);

  public override SyntaxNode? VisitEventFieldDeclaration(EventFieldDeclarationSyntax node) {
    if (!this.CurrentTypeIsExternallyVisible || !IsPublicOrProtected(node.Modifiers))
      return base.VisitEventFieldDeclaration(node);
    var names = string.Join(" and ", node.Declaration.Variables.Select(v => Humanize(v.Identifier.ValueText).ToLowerInvariant()));
    return base.VisitEventFieldDeclaration(AddSummary(node, $"Occurs when {names}."));
  }

  public override SyntaxNode? VisitFieldDeclaration(FieldDeclarationSyntax node) {
    if (!this.CurrentTypeIsExternallyVisible || !IsPublicOrProtected(node.Modifiers))
      return base.VisitFieldDeclaration(node);
    var names = string.Join(" and ", node.Declaration.Variables.Select(v => Humanize(v.Identifier.ValueText).ToLowerInvariant()));
    var description = node.Modifiers.Any(SyntaxKind.ConstKeyword)
      ? $"Defines the {names} constant value."
      : $"Provides the {names} value.";
    return base.VisitFieldDeclaration(AddSummary(node, description));
  }

  public override SyntaxNode? VisitOperatorDeclaration(OperatorDeclarationSyntax node)
    => this.VisitMember(node, $"Implements the <c>{node.OperatorToken.ValueText}</c> operator.", base.VisitOperatorDeclaration);

  public override SyntaxNode? VisitConversionOperatorDeclaration(ConversionOperatorDeclarationSyntax node)
    => this.VisitMember(node, $"Converts a value to <see cref=\"{EscapeXml(node.Type.ToString())}\"/>.", base.VisitConversionOperatorDeclaration);

  public override SyntaxNode? VisitDelegateDeclaration(DelegateDeclarationSyntax node) {
    if (!this.IsTypeExternallyVisible(node.Modifiers))
      return base.VisitDelegateDeclaration(node);
    return base.VisitDelegateDeclaration(AddSummary(node, $"Represents the {Humanize(node.Identifier.ValueText).ToLowerInvariant()} callback."));
  }

  private SyntaxNode? VisitType<T>(T node, string name, Func<T, SyntaxNode?> visit) where T : TypeDeclarationSyntax {
    var visible = this.IsTypeExternallyVisible(node.Modifiers);
    var documented = visible ? AddSummary(node, TypeSummary(name, node.Kind().ToString())) : node;
    this._externallyVisibleTypes.Push(visible);
    var result = visit(documented);
    this._externallyVisibleTypes.Pop();
    return result;
  }

  private SyntaxNode? VisitMember<T>(T node, string summary, Func<T, SyntaxNode?> visit) where T : MemberDeclarationSyntax {
    if (!this.CurrentTypeIsExternallyVisible || !IsPublicOrProtected(GetModifiers(node)))
      return visit(node);
    return visit(AddSummary(node, summary));
  }

  private bool IsTypeExternallyVisible(SyntaxTokenList modifiers) {
    if (!IsPublicOrProtected(modifiers))
      return false;
    return this._externallyVisibleTypes.Count == 0 || this.CurrentTypeIsExternallyVisible;
  }

  private static SyntaxTokenList GetModifiers(MemberDeclarationSyntax node) => node switch {
    BaseMethodDeclarationSyntax method => method.Modifiers,
    BasePropertyDeclarationSyntax property => property.Modifiers,
    _ => default,
  };

  private static bool IsPublicOrProtected(SyntaxTokenList modifiers)
    => modifiers.Any(SyntaxKind.PublicKeyword) || modifiers.Any(SyntaxKind.ProtectedKeyword);

  private static T AddSummary<T>(T node, string summary) where T : SyntaxNode {
    var leading = node.GetLeadingTrivia();
    if (leading.ToFullString().Contains("<summary", StringComparison.OrdinalIgnoreCase))
      return node;

    var indentation = GetIndentation(node);
    var documentation = SyntaxFactory.ParseLeadingTrivia(
      $"{indentation}/// <summary>{Environment.NewLine}" +
      $"{indentation}/// {summary}{Environment.NewLine}" +
      $"{indentation}/// </summary>{Environment.NewLine}");

    // Keep blank lines before the declaration ahead of the new XML documentation,
    // but place attributes after the documentation because attributes are part of the node.
    return node.WithLeadingTrivia(leading.AddRange(documentation));
  }

  private static string GetIndentation(SyntaxNode node) {
    var line = node.SyntaxTree.GetText().Lines.GetLineFromPosition(node.SpanStart).ToString();
    return new string(line.TakeWhile(char.IsWhiteSpace).ToArray());
  }

  private static string TypeSummary(string name, string kind) {
    var words = Humanize(name);
    var lower = words.ToLowerInvariant();
    if (name.EndsWith("Options", StringComparison.Ordinal)) return $"Specifies options for {Humanize(name[..^7]).ToLowerInvariant()}.";
    if (name.EndsWith("Descriptor", StringComparison.Ordinal)) return $"Describes {Humanize(name[..^10]).ToLowerInvariant()}.";
    if (name.EndsWith("Reader", StringComparison.Ordinal)) return $"Reads {Humanize(name[..^6]).ToLowerInvariant()} data.";
    if (name.EndsWith("Writer", StringComparison.Ordinal)) return $"Writes {Humanize(name[..^6]).ToLowerInvariant()} data.";
    if (name.EndsWith("Decoder", StringComparison.Ordinal)) return $"Decodes {Humanize(name[..^7]).ToLowerInvariant()} data.";
    if (name.EndsWith("Encoder", StringComparison.Ordinal)) return $"Encodes {Humanize(name[..^7]).ToLowerInvariant()} data.";
    if (name.EndsWith("Exception", StringComparison.Ordinal)) return $"Represents an error involving {Humanize(name[..^9]).ToLowerInvariant()}.";
    if (kind.Contains("Interface", StringComparison.Ordinal)) return $"Defines the contract for {lower}.";
    return $"Represents {Article(lower)} {lower}.";
  }

  private static string MethodSummary(string name) {
    var words = Humanize(name);
    if (name.StartsWith("TryParse", StringComparison.Ordinal)) return $"Attempts to parse {ObjectWords(name, "TryParse")} from the supplied data.";
    if (name.StartsWith("Parse", StringComparison.Ordinal)) return $"Parses {ObjectWords(name, "Parse")} from the supplied data.";
    if (name.StartsWith("TryRead", StringComparison.Ordinal)) return $"Attempts to read {ObjectWords(name, "TryRead")} from the supplied input.";
    if (name.StartsWith("Read", StringComparison.Ordinal)) return $"Reads {ObjectWords(name, "Read")} from the supplied input.";
    if (name.StartsWith("Write", StringComparison.Ordinal)) return $"Writes {ObjectWords(name, "Write")} to the supplied output.";
    if (name.StartsWith("Encode", StringComparison.Ordinal) || name is "Compress") return "Encodes the supplied input.";
    if (name.StartsWith("Decode", StringComparison.Ordinal) || name is "Decompress" or "Extract") return "Decodes the supplied input.";
    if (name is "List") return "Lists the entries in the supplied container.";
    if (name is "Add" or "AddOrReplace" or "Append") return "Adds the supplied entry to the target container.";
    if (name is "Remove" or "Delete") return "Removes the specified entry from the target container.";
    if (name is "Dispose") return "Releases resources held by this instance.";
    if (name is "GetEnumerator") return "Returns an enumerator over the available values.";
    if (name.StartsWith("Enumerate", StringComparison.Ordinal)) return $"Enumerates {ObjectWords(name, "Enumerate")}.";
    if (name.StartsWith("Get", StringComparison.Ordinal)) return $"Gets {ObjectWords(name, "Get")}.";
    if (name.StartsWith("Set", StringComparison.Ordinal)) return $"Sets {ObjectWords(name, "Set")}.";
    if (name.StartsWith("Compute", StringComparison.Ordinal) || name.StartsWith("Calculate", StringComparison.Ordinal)) return $"Computes {ObjectWords(name, name.StartsWith("Compute", StringComparison.Ordinal) ? "Compute" : "Calculate")} for the supplied data.";
    if (name.StartsWith("Validate", StringComparison.Ordinal) || name.StartsWith("Verify", StringComparison.Ordinal)) return "Validates the supplied data.";
    return $"Performs the {words.ToLowerInvariant()} operation.";
  }

  private static string PropertySummary(string name, TypeSyntax type, AccessorListSyntax? accessors) {
    var words = Humanize(name).ToLowerInvariant();
    if (type is PredefinedTypeSyntax predefined && predefined.Keyword.IsKind(SyntaxKind.BoolKeyword))
      return $"Gets a value indicating whether {words}.";
    var writable = accessors?.Accessors.Any(a => a.Keyword.IsKind(SyntaxKind.SetKeyword) || a.Keyword.IsKind(SyntaxKind.InitKeyword)) == true;
    return $"Gets{(writable ? " or sets" : string.Empty)} the {words}.";
  }

  private static string EnumValueSummary(string name) {
    var words = Humanize(name);
    return name switch {
      "None" => "Specifies that no option is selected.",
      "Unknown" => "Specifies an unknown or unrecognized value.",
      "Auto" or "Automatic" => "Selects the value automatically.",
      _ => $"Specifies the {words.ToLowerInvariant()} option.",
    };
  }

  private static string ObjectWords(string name, string prefix) {
    var suffix = name[prefix.Length..];
    return string.IsNullOrEmpty(suffix) ? "the value" : $"the {Humanize(suffix).ToLowerInvariant()}";
  }

  private static string Article(string text) => text.Length > 0 && "aeiou".Contains(char.ToLowerInvariant(text[0])) ? "an" : "a";

  private static string EscapeXml(string text) => text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

  private static string Humanize(string name) {
    var value = name.Replace('_', ' ');
    value = LowerToUpperRegex().Replace(value, "$1 $2");
    value = AcronymBoundaryRegex().Replace(value, "$1 $2");
    value = LetterDigitRegex().Replace(value, "$1 $2");
    return WhitespaceRegex().Replace(value, " ").Trim();
  }

  [GeneratedRegex("([a-z0-9])([A-Z])")]
  private static partial Regex LowerToUpperRegex();
  [GeneratedRegex("([A-Z])([A-Z][a-z])")]
  private static partial Regex AcronymBoundaryRegex();
  [GeneratedRegex("([A-Za-z])([0-9])")]
  private static partial Regex LetterDigitRegex();
  [GeneratedRegex("\\s+")]
  private static partial Regex WhitespaceRegex();
}
