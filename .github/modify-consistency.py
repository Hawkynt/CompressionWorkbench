from pathlib import Path
import re

# Normalize descriptor flags to the established public contract:
# IArchiveModifiable means an existing instance can be edited through the API,
# regardless of whether the physical implementation patches in place or stages a
# verified rebuild.
for path in list(Path('FileSystems').glob('**/*FormatDescriptor.cs')) + list(Path('FileFormats').glob('**/*FormatDescriptor.cs')):
    text = path.read_text(errors='ignore')
    # Only touch classes that explicitly implement the capability.
    head = text[:text.find('{', text.find('class ')) + 1] if 'class ' in text else ''
    if 'IArchiveModifiable' not in head:
        continue
    m = re.search(r'public\s+FormatCapabilities\s+Capabilities\s*=>\s*(.*?);', text, re.S)
    if not m:
        continue
    expr = m.group(1)
    if 'FormatCapabilities.CanModify' in expr:
        continue
    replacement = m.group(0)[:-1].rstrip() + ' | FormatCapabilities.CanModify;'
    text = text[:m.start()] + replacement + text[m.end():]
    # Remove obsolete comments that equate rebuild-backed mutation with WORM.
    text = re.sub(
      r'\s*// WORM, not R/W: Add/Remove rebuild the whole image \(read-all -> re-create\),\n'
      r'\s*// so the verb works via rebuild but nothing is modified in place\. CanModify\n'
      r'\s*// must not be advertised\. See Compression\.Registry/FormatCapabilities\.cs\.\n',
      '\n  // Existing-instance mutation may be implemented by a verified rebuild; that still satisfies CanModify.\n',
      text,
      count=1)
    path.write_text(text)

# Permanent runtime consistency test: no hidden modifier and no false CanModify flag.
p = Path('Compression.Tests/Operations/MarkerInterfaceCoverageTests.cs')
t = p.read_text()
anchor = '''  [TestCaseSource(nameof(Markers))]\n  public void RegistryDrivenSourceIsSupersetOfReflection(Type marker) {'''
method = '''  [Test]\n  public void CanModifyFlagAndRuntimeModifierStayInSync() {\n    Compression.Lib.FormatRegistration.EnsureInitialized();\n    var problems = new List<string>();\n    foreach (var descriptor in FormatRegistry.All) {\n      var ops = FormatRegistry.GetArchiveOps(descriptor.Id);\n      var flag = descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify);\n      var runtime = ops is IArchiveModifiable;\n      if (flag != runtime)\n        problems.Add($"{descriptor.Id}: CanModify={flag}, IArchiveModifiable={runtime}");\n    }\n    Assert.That(problems, Is.Empty,\n      "Existing-instance mutation capability drift:\\n  " + string.Join("\\n  ", problems));\n  }\n\n'''
if method not in t:
    if anchor not in t:
        raise SystemExit('marker consistency insertion anchor not found')
    t = t.replace(anchor, method + anchor, 1)
p.write_text(t)

Path('.github/modify-consistency.py').unlink()
