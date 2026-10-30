# Generates the RAR conformance fixtures with RARLAB's own rar.exe and writes manifest.tsv.
# usage: genfx.py <scratch> <outdir>
import hashlib, os, shutil, subprocess, sys

S, OUT = sys.argv[1], sys.argv[2]
RAR7 = os.path.join(S, "wr712", "Rar.exe")
RAR6 = os.path.join(S, "wr624", "Rar.exe")
UNRAR = os.path.join(S, "wr712", "UnRAR.exe")
IN = os.path.join(S, "fxin")
os.makedirs(OUT, exist_ok=True)
for f in os.listdir(OUT):
    if f.endswith(".rar") or f.endswith(".rev") or f.endswith(".exe") or f == "manifest.tsv":
        os.remove(os.path.join(OUT, f))

manifest = []
log = []

def files_of(root, sel=None):
    out = []
    for r, ds, fs in os.walk(root):
        for d in ds:
            p = os.path.join(r, d)
            rel = os.path.relpath(p, root).replace("\\", "/")
            if sel and not any(rel == s or rel.startswith(s + "/") for s in sel): continue
            out.append((rel, "d", 0, ""))
        for f in fs:
            p = os.path.join(r, f)
            rel = os.path.relpath(p, root).replace("\\", "/")
            if sel and not any(rel == s or rel.startswith(s + "/") for s in sel): continue
            data = open(p, "rb").read()
            out.append((rel, "f", len(data), hashlib.sha256(data).hexdigest()))
    return out

def rar(exe, cwd, args, name):
    cmd = [exe, "a", "-cfg-", "-idq", "-y"] + args + [os.path.join(OUT, name)]
    return cmd

def run(cmd, cwd):
    r = subprocess.run(cmd, cwd=cwd, capture_output=True)
    if r.returncode not in (0,):
        raise SystemExit(f"FAILED {cmd}: {r.returncode}\n{r.stdout.decode(errors='replace')}\n{r.stderr.decode(errors='replace')}")
    return r

def make(name, exe, args, srcdir, items, expect=None, password=None, first=None):
    cmd = [exe, "a", "-cfg-", "-idq", "-y"] + args + [os.path.join(OUT, name)] + items
    run(cmd, os.path.join(IN, srcdir))
    test = first or name
    tcmd = [UNRAR, "t", "-idq", "-cfg-"] + ([f"-p{password}"] if password else ["-p-"]) + [os.path.join(OUT, test)]
    run(tcmd, OUT)
    ents = expect if expect is not None else files_of(os.path.join(IN, srcdir), [i.rstrip("/") for i in items if i != "*"] or None)
    for (rel, kind, size, sha) in ents:
        manifest.append("\t".join([test, rel, kind, str(size), sha, password or ""]))
    log.append(" ".join(os.path.basename(c) if i == 0 else c for i, c in enumerate(cmd)))

R5 = ["-ma5"]
R4 = ["-ma4"]
TREE = ["-r", "-ep1", "*"]

# --- methods, both formats
for m in range(6):
    make(f"r5-m{m}.rar", RAR7, R5 + [f"-m{m}", "-r"], "tree", ["*"])
    make(f"r4-m{m}.rar", RAR6, R4 + [f"-m{m}", "-r"], "tree", ["*"])
# --- solid, solid groups
make("r5-solid.rar", RAR7, R5 + ["-s", "-r"], "tree", ["*"])
make("r4-solid.rar", RAR6, R4 + ["-s", "-r"], "tree", ["*"])
make("r5-solid-s2.rar", RAR7, R5 + ["-s2", "-r"], "tree", ["*"])
make("r5-solid-se.rar", RAR7, R5 + ["-se", "-r"], "tree", ["*"])
# --- dictionaries
make("r5-md128k.rar", RAR7, R5 + ["-md128k", "-m5"], "vol", ["text.txt"])
make("r5-md1g.rar", RAR7, R5 + ["-md1g", "-m3"], "vol", ["text.txt"])
make("r5-md4g.rar", RAR7, R5 + ["-md4g", "-m3", "-s"], "tree", ["text.txt", "noise.bin"])
make("r4-md64k.rar", RAR6, R4 + ["-md64k", "-m3"], "vol", ["text.txt"])
make("r4-md4m.rar", RAR6, R4 + ["-md4m", "-m5"], "vol", ["text.txt"])
# --- multithreading
make("r5-mt1.rar", RAR7, R5 + ["-mt1", "-m5"], "vol", ["text.txt"])
make("r5-mt16.rar", RAR7, R5 + ["-mt16", "-m5"], "vol", ["text.txt"])
# --- filters (inputs from make_inputs.py)
make("r5-x86.rar", RAR7, R5 + ["-m3"], "../in", ["x86.bin"])
make("r5-delta.rar", RAR7, R5 + ["-m3", "-mcD+"], "../in", ["table.bin"])
make("r5-audio.rar", RAR7, R5 + ["-m3"], "../in", ["audio.wav", "image.bmp"])

make("r4-ppmd.rar", RAR6, R4 + ["-m5", "-mct+"], "tree", ["text.txt"])
# --- times (high precision, all three), attributes
make("r5-times.rar", RAR7, R5 + ["-tsm4", "-tsc4", "-tsa4", "-r"], "tree", ["*"])
make("r4-times.rar", RAR6, R4 + ["-tsm4", "-tsc4", "-tsa4", "-r"], "tree", ["*"])
# --- links: hard links (-oh), identical files (-oi)
make("r5-hardlink.rar", RAR7, R5 + ["-oh"], "links", ["target.txt", "hard.txt"])
make("r5-filecopy.rar", RAR7, R5 + ["-oi:1"], "dup", ["one.txt", "two.txt"])
# --- NTFS streams
make("r5-streams.rar", RAR7, R5 + ["-os"], "ads", ["host.txt"],
     expect=files_of(os.path.join(IN, "ads")) + [("host.txt:extra", "f", 21, hashlib.sha256(b"alternate stream data").hexdigest())])
make("r4-streams.rar", RAR6, R4 + ["-os"], "ads", ["host.txt"],
     expect=files_of(os.path.join(IN, "ads")) + [("host.txt:extra", "f", 21, hashlib.sha256(b"alternate stream data").hexdigest())])
# --- comment, recovery record, lock, quick open
cmt = os.path.join(S, "fxcomment.txt")
open(cmt, "w", encoding="utf-8").write("archive comment line 1\narchive comment line 2\n")
make("r5-comment.rar", RAR7, R5 + [f"-z{cmt}"], "tree", ["text.txt"])
make("r4-comment.rar", RAR6, R4 + [f"-z{cmt}"], "tree", ["text.txt"])
make("r5-rr.rar", RAR7, R5 + ["-rr5%"], "tree", ["text.txt"])
make("r4-rr.rar", RAR6, R4 + ["-rr5%"], "tree", ["text.txt"])
make("r5-locked.rar", RAR7, R5 + ["-k"], "tree", ["text.txt"])
make("r4-locked.rar", RAR6, R4 + ["-k"], "tree", ["text.txt"])
make("r5-qo.rar", RAR7, R5 + ["-qo+"], "tree", ["text.txt", "noise.bin"])
# --- BLAKE2sp hash
make("r5-blake2.rar", RAR7, R5 + ["-htb", "-r"], "tree", ["*"])
# --- encryption
make("r5-enc.rar", RAR7, R5 + ["-psecret", "-r"], "tree", ["*"], password="secret")
make("r5-enc-store.rar", RAR7, R5 + ["-psecret", "-m0"], "tree", ["text.txt", "noise.bin"], password="secret")
make("r5-enc-solid.rar", RAR7, R5 + ["-psecret", "-s", "-r"], "tree", ["*"], password="secret")
make("r5-enc-hp.rar", RAR7, R5 + ["-hpsecret", "-r"], "tree", ["*"], password="secret")
make("r5-enc-blake2.rar", RAR7, R5 + ["-psecret", "-htb"], "tree", ["text.txt"], password="secret")
make("r4-enc.rar", RAR6, R4 + ["-psecret", "-r"], "tree", ["*"], password="secret")
make("r4-enc-hp.rar", RAR6, R4 + ["-hpsecret", "-r"], "tree", ["*"], password="secret")
# unicode password
make("r5-enc-unicode.rar", RAR7, R5 + ["-pgrüße", "-m3"], "tree", ["text.txt"], password="grüße")
make("r4-enc-unicode.rar", RAR6, R4 + ["-pgrüße", "-m3"], "tree", ["text.txt"], password="grüße")
# --- file versions (-ver)
ver = os.path.join(IN, "ver")
open(os.path.join(ver, "doc.txt"), "w", newline="\n").write("version one\n")
run([RAR7, "a", "-cfg-", "-idq", "-y", "-ma5", "-ver", os.path.join(OUT, "r5-versions.rar"), "doc.txt"], ver)
open(os.path.join(ver, "doc.txt"), "w", newline="\n").write("version two, longer\n")
run([RAR7, "u", "-cfg-", "-idq", "-y", "-ma5", "-ver", os.path.join(OUT, "r5-versions.rar"), "doc.txt"], ver)
run([UNRAR, "t", "-idq", "-cfg-", os.path.join(OUT, "r5-versions.rar")], OUT)
manifest.append("\t".join(["r5-versions.rar", "doc.txt", "f", "20", hashlib.sha256(b"version two, longer\n").hexdigest(), ""]))
manifest.append("\t".join(["r5-versions.rar", "doc.txt;1", "f", "12", hashlib.sha256(b"version one\n").hexdigest(), ""]))
log.append("rar a -ma5 -ver r5-versions.rar doc.txt; (modify); rar u -ma5 -ver r5-versions.rar doc.txt")
# --- volumes (new and old naming, solid, encrypted)
def vol(name, exe, args, first, items=["random.bin", "text.txt"], password=None):
    make(name, exe, args, "vol", items, password=password, first=first)
vol("r5-vol.rar", RAR7, R5 + ["-v20k", "-m3"], "r5-vol.part1.rar")

vol("r5-vol-solid.rar", RAR7, R5 + ["-v20k", "-m3", "-s"], "r5-vol-solid.part1.rar")
vol("r5-vol-enc.rar", RAR7, R5 + ["-v20k", "-m3", "-psecret"], "r5-vol-enc.part1.rar", password="secret")
vol("r5-vol-hp.rar", RAR7, R5 + ["-v20k", "-m0", "-hpsecret"], "r5-vol-hp.part1.rar", password="secret")
vol("r4-vol.rar", RAR6, R4 + ["-v20k", "-m3"], "r4-vol.part1.rar")
vol("r4-vol-old.rar", RAR6, R4 + ["-v20k", "-m3", "-vn"], "r4-vol-old.rar")
vol("r4-vol-enc.rar", RAR6, R4 + ["-v20k", "-m3", "-psecret"], "r4-vol-enc.part1.rar", password="secret")
vol("r5-vol-rev.rar", RAR7, R5 + ["-v20k", "-m0", "-rv1"], "r5-vol-rev.part1.rar")
# --- SFX prefix: a small non-RAR stub before an archive (rar's own SFX modules are tool-gated tests)
data = open(os.path.join(OUT, "r5-m3.rar"), "rb").read()
open(os.path.join(OUT, "r5-prefixed.exe"), "wb").write(b"MZ" + bytes(4094) + data)
data4 = open(os.path.join(OUT, "r4-m3.rar"), "rb").read()
open(os.path.join(OUT, "r4-prefixed.exe"), "wb").write(b"MZ" + bytes(4094) + data4)
for n, src in (("r5-prefixed.exe", "r5-m3.rar"), ("r4-prefixed.exe", "r4-m3.rar")):
    run([UNRAR, "t", "-idq", "-cfg-", os.path.join(OUT, n)], OUT)
    for line in [l for l in manifest if l.startswith(src + "\t")]:
        manifest.append(n + line[len(src):])

open(os.path.join(OUT, "manifest.tsv"), "w", encoding="utf-8", newline="\n").write("\n".join(manifest) + "\n")
open(os.path.join(OUT, "commands.txt"), "w", encoding="utf-8", newline="\n").write("\n".join(log) + "\n")
total = sum(os.path.getsize(os.path.join(OUT, f)) for f in os.listdir(OUT))
print("fixtures:", len([f for f in os.listdir(OUT) if not f.endswith('.tsv') and not f.endswith('.txt')]), "bytes:", total)
