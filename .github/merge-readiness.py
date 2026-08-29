from pathlib import Path


def replace(path, old, new, count=1):
    p = Path(path)
    text = p.read_text()
    if old not in text:
        raise SystemExit(f'expected text not found in {path}: {old[:120]!r}')
    p.write_text(text.replace(old, new, count))

# ReFS: wire the already-implemented offline editor into the public descriptor.
replace('FileSystems/FileSystem.Refs/RefsFormatDescriptor.cs',
'''  IFormatDescriptor,\n  IArchiveFormatOperations,\n  IFilesystemExtentMap,''',
'''  IFormatDescriptor,\n  IArchiveFormatOperations,\n  IArchiveModifiable,\n  IFilesystemExtentMap,''')
replace('FileSystems/FileSystem.Refs/RefsFormatDescriptor.cs',
'''    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |\n    FormatCapabilities.SupportsMultipleEntries;''',
'''    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |\n    FormatCapabilities.CanModify | FormatCapabilities.SupportsMultipleEntries;''')
replace('FileSystems/FileSystem.Refs/RefsFormatDescriptor.cs',
'''  public string Description => "Microsoft ReFS 3.x volume image with native read-only driver projection, namespace/allocation parsing, and offline filesystem-metadata placement support.";''',
'''  public string Description => "Microsoft ReFS 3.x volume image with native read-only driver projection, namespace/allocation parsing, offline existing-file replacement/removal, and filesystem-metadata placement support.";''')
anchor = '''\n  private static List<ArchiveEntryInfo> ListDiagnosticSurface(Stream stream) {'''
insert = '''\n  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs)\n    => RefsOfflineModifier.Add(archive, inputs);\n\n  public void Remove(Stream archive, string[] entryNames)\n    => RefsOfflineModifier.Remove(archive, entryNames);\n'''
replace('FileSystems/FileSystem.Refs/RefsFormatDescriptor.cs', anchor, insert + anchor)

# Maintenance marker tests: purge is its own explicit capability.
replace('Compression.Tests/Operations/MarkerInterfaceCoverageTests.cs',
'''    ("purge/modify", typeof(IArchiveModifiable)),''',
'''    ("purge", typeof(IArchivePurgeable)),\n    ("modify", typeof(IArchiveModifiable)),''')

# Generic purge test must exercise the Purge contract, not approximate it with Remove(all).
p = Path('Compression.Tests/Operations/GenericPurgeRoundTripTests.cs')
t = p.read_text()
t = t.replace('''/// Safety net for the broad rollout of the default <see cref="IArchiveModifiable"/>\n/// (verified extract → edit → re-create rebuild). For every filesystem descriptor\n/// using the DEFAULT Remove, this builds a small image, purges it (Remove every\n/// entry), and asserts the result is a valid, listable, empty container — the\n/// <em>purge</em> verb. The rebuild only commits a result that re-lists, so a writer\n/// limitation surfaces as a clean throw (original untouched) rather than corruption.''',
'''/// Safety net for the explicit <see cref="IArchivePurgeable"/> capability. For every\n/// purgeable descriptor that can create a representative probe, this builds an image,\n/// invokes Purge, and asserts the user's live files are gone while the result remains\n/// listable. A descriptor that advertises purge must not silently fail or corrupt.''')
t = t.replace('''  private static IEnumerable<string> ModifiableDefaultIds() =>\n    Compression.Tests.Support.CapabilityImplementers.RegisteredIdsExposing(typeof(IArchiveModifiable))\n      .Where(id => FormatRegistry.GetArchiveOps(id) is IArchiveCreatable\n                   && Enum.TryParse<FormatDetector.Format>(id, out _)\n                   && !Compression.Tests.Support.CapabilityImplementers.DeclaresOwn(id, "Remove", typeof(Stream), typeof(string[])));\n\n  [TestCaseSource(nameof(ModifiableDefaultIds))]''',
'''  private static IEnumerable<string> PurgeableIds() =>\n    Compression.Tests.Support.CapabilityImplementers.RegisteredIdsExposing(typeof(IArchivePurgeable))\n      .Where(id => FormatRegistry.GetArchiveOps(id) is IArchiveCreatable\n                   && Enum.TryParse<FormatDetector.Format>(id, out _));\n\n  [TestCaseSource(nameof(PurgeableIds))]''')
t = t.replace('''      var modifiable = (IArchiveModifiable)fmtOps;\n      try {\n        modifiable.Remove(ms, [.. before]);\n      } catch (NotSupportedException) {\n        Assert.Pass($"{formatId}: purge cleanly NotSupported (no corruption).");\n        return;\n      } catch (Exception ex) {\n        Assert.Ignore($"{formatId}: purge rebuild failed non-destructively ({ex.GetType().Name}).");\n        return;\n      }''',
'''      var purgeable = (IArchivePurgeable)fmtOps;\n      Assert.DoesNotThrow(() => purgeable.Purge(ms),\n        $"{formatId}: advertises IArchivePurgeable but purge failed for its own representative image.");''')
p.write_text(t)

# Add a root-level purge verb to the CLI. It is deliberately destructive and requires --yes.
program = Path('Compression.CLI/Program.cs')
t = program.read_text()
insert_after = '''replaceCmd.SetAction((ParseResult ctx) => {\n  var archive = ctx.GetValue(replaceArchiveArg)!;\n  var name = ctx.GetValue(replaceNameArg)!;\n  var file = ctx.GetValue(replaceFileArg)!;\n  if (!archive.Exists) { Console.Error.WriteLine($"File not found: {archive.FullName}"); return 1; }\n  if (!file.Exists) { Console.Error.WriteLine($"File not found: {file.FullName}"); return 1; }\n\n  var opts = new CompressionOptions {\n    Method = MethodSpec.Parse(ctx.GetValue(methodOpt)),\n    Level = ctx.GetValue(levelOpt),\n    Password = ctx.GetValue(passwordOpt),\n  };\n\n  Console.Write($"Replacing '{name}' in {archive.Name}...");\n  var sw = Stopwatch.StartNew();\n  ArchiveOperations.Replace(archive.FullName, name, file.FullName, opts);\n  sw.Stop();\n  Console.WriteLine($" done ({sw.ElapsedMilliseconds}ms)");\n  return 0;\n});\n'''
purge_block = '''\n// ── purge ────────────────────────────────────────────────────────────\n\nvar purgeArchiveArg = new Argument<FileInfo>("archive") { Description = "Archive or filesystem image to empty" };\nvar purgeYesOpt = new Option<bool>("--yes", "-y") { Description = "Confirm destructive purge without prompting" };\nvar purgeCmd = new Command("purge", """\n  Remove all live user entries while leaving a valid empty container/image.\n  This is different from 'wipe': purge removes content; wipe preserves content\n  and sanitizes only unused/dead bytes. The format must advertise IArchivePurgeable.\n\n  Examples:\n    cwb purge disk.d64 --yes\n    cwb purge archive.zip --yes\n  """) { purgeArchiveArg, purgeYesOpt };\npurgeCmd.SetAction((ParseResult ctx) => {\n  var archive = ctx.GetValue(purgeArchiveArg)!;\n  if (!archive.Exists) { Console.Error.WriteLine($"File not found: {archive.FullName}"); return 1; }\n  if (!ctx.GetValue(purgeYesOpt)) {\n    Console.Error.WriteLine("Purge is destructive. Re-run with --yes to remove all live user entries.");\n    return 2;\n  }\n\n  try {\n    FormatRegistration.EnsureInitialized();\n    var formatId = FormatDetector.Detect(archive.FullName).ToString();\n    var ops = FormatRegistry.GetArchiveOps(formatId);\n    if (ops is not IArchivePurgeable purgeable) {\n      Console.Error.WriteLine($"Format {formatId} does not advertise purge support.");\n      return 1;\n    }\n\n    var before = ops.List(File.OpenRead(archive.FullName), null).Count(e => !e.IsDirectory);\n    Console.Write($"Purging {archive.Name} ({formatId}, {before} live file(s))...");\n    var sw = Stopwatch.StartNew();\n    using (var stream = File.Open(archive.FullName, FileMode.Open, FileAccess.ReadWrite, FileShare.None))\n      purgeable.Purge(stream);\n    sw.Stop();\n    using var verify = File.OpenRead(archive.FullName);\n    var after = ops.List(verify, null).Count(e => !e.IsDirectory);\n    Console.WriteLine($" done ({sw.ElapsedMilliseconds}ms; {before} -> {after} live file(s))");\n    return 0;\n  } catch (Exception ex) {\n    Console.Error.WriteLine($"Purge failed: {ex.Message}");\n    return 1;\n  }\n});\n'''
if insert_after not in t:
    raise SystemExit('replace command anchor not found in Program.cs')
t = t.replace(insert_after, insert_after + purge_block, 1)
t = t.replace('''    cwb wipe-empty disk.img                  Zero all unused space in image''',
'''    cwb purge disk.img --yes                 Remove all live user entries\n    cwb wipe-empty disk.img                  Zero all unused space in image''', 1)
t = t.replace('''  listCmd, extractCmd, createCmd, testCmd, addCmd, removeCmd, replaceCmd, infoCmd,''',
'''  listCmd, extractCmd, createCmd, testCmd, addCmd, removeCmd, replaceCmd, purgeCmd, infoCmd,''', 1)
program.write_text(t)

# Correct stale operation-model prose without touching generated tables.
coverage = Path('docs/OPERATION_COVERAGE.md')
t = coverage.read_text()
t = t.replace('''No archive descriptor currently implements a dedicated `IArchivePurgeable` interface; purge is represented by `IArchiveModifiable.Remove` over all live entries.''',
'''Purge is an explicit `IArchivePurgeable` capability. `IArchiveModifiable` inherits it because removing all live user entries is a required subset of full modification; generic purge is staged and verified before commit.''')
t = t.replace('''CramFS and SquashFS remain read-only/WORM because their on-disk formats are immutable by design.''',
'''CramFS, SquashFS and EROFS remain read-only when mounted by their native operating-system drivers, but CompressionWorkbench exposes verified offline rebuild-backed modification for supported profiles. Mounted-driver R/W is a separate capability and remains fail-closed where the native format is immutable or crash-consistent mutation is not implemented.''')
coverage.write_text(t)

# Remove the one-shot machinery in the commit it creates.
Path('.github/merge-readiness.py').unlink()
Path('.github/workflows/merge-readiness-once.yml').unlink()
