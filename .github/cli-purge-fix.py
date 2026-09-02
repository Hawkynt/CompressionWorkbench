from pathlib import Path
p = Path('Compression.CLI/Program.cs')
t = p.read_text()
old = '''    var before = ops.List(File.OpenRead(archive.FullName), null).Count(e => !e.IsDirectory);'''
new = '''    int before;\n    using (var source = File.OpenRead(archive.FullName))\n      before = ops.List(source, null).Count(e => !e.IsDirectory);'''
if old not in t:
    raise SystemExit('generated purge read handle anchor not found')
p.write_text(t.replace(old, new, 1))
Path('.github/cli-purge-fix.py').unlink()
