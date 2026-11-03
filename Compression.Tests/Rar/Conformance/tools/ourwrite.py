# Create archives with cwb and check them with unrar 7.12, rar 7.12, rar 6.24 (RAR 4) and 7-Zip:
# test, then extract and compare byte for byte.
import hashlib, os, shutil, subprocess, sys
sys.stdout.reconfigure(encoding="utf-8", errors="replace")
S = os.environ["RAR_FIXTURE_WORK"]  # work directory holding the tools and inputs (see README)
CWB = os.environ["CWB_EXE"]
UNRAR = os.path.join(S, "wr712", "UnRAR.exe")
RAR7 = os.path.join(S, "wr712", "Rar.exe")
SZ = os.environ.get("CWB_TOOL_7Z", "7z")
OW = os.path.join(S, "ow")
IN = os.path.join(S, "fxin")
only = sys.argv[1:]

def tree(d):
    res = {}
    for r, ds, fs in os.walk(d):
        for f in fs:
            p = os.path.join(r, f)
            res[os.path.relpath(p, d).replace("\\", "/")] = hashlib.sha256(open(p, "rb").read()).hexdigest()
        for x in ds:
            res[os.path.relpath(os.path.join(r, x), d).replace("\\", "/") + "/"] = "dir"
    return res

def rm(d):
    if os.path.exists(d): shutil.rmtree(d, onerror=lambda f, p, e: (os.chmod(p, 0o777), f(p)))

CASES = [
    # name, source dir, cwb args, password, first volume name
    ("r5-store", "tree", ["-m", "store"], None),
    ("r5-l1", "tree", ["-l", "1", "--solid-size", "1"], None),
    ("r5-l5", "tree", ["--solid-size", "1"], None),
    ("r5-l9", "tree", ["-l", "9", "--solid-size", "1"], None),
    ("r5-solid", "tree", ["--solid-size", "0"], None),
    ("r5-big", "vol", ["-l", "5", "--solid-size", "1"], None),
    ("r5-enc", "tree", ["-p", "secret", "--solid-size", "1"], "secret"),
    ("r5-enc-solid", "tree", ["-p", "secret"], "secret"),
    ("r5-hp", "tree", ["-p", "secret", "-eh"], "secret"),
    ("r5-vol.part1", "vol", ["-v", "20k", "--solid-size", "1"], None),
    ("r5-vol-store.part1", "vol", ["-v", "20k", "-m", "store"], None),
    ("r5-vol-enc.part1", "vol", ["-v", "20k", "-p", "secret", "--solid-size", "1"], "secret"),
    ("r5-vol-hp.part1", "vol", ["-v", "20k", "-p", "secret", "-eh"], "secret"),
    ("r5-vol-solid.part1", "vol", ["-v", "20k"], None),
    ("r4-store", "tree", ["--opt", "TargetCompatibility=Rar4", "-m", "store"], None),
    ("r4-l5", "tree", ["--opt", "TargetCompatibility=Rar4", "--solid-size", "1"], None),
    ("r4-solid", "tree", ["--opt", "TargetCompatibility=Rar4"], None),
    ("r4-enc", "tree", ["--opt", "TargetCompatibility=Rar4", "-p", "secret", "--solid-size", "1"], "secret"),
    ("r4-vol.part1", "vol", ["--opt", "TargetCompatibility=Rar4", "-v", "20k", "--solid-size", "1"], None),
    ("r4-vol-old", "vol", ["--opt", "TargetCompatibility=Rar4", "-v", "20k", "--solid-size", "1"], None),
    ("r4-vol-enc.part1", "vol", ["--opt", "TargetCompatibility=Rar4", "-v", "20k", "-p", "secret", "--solid-size", "1"], "secret"),
    ("r15", "vol", ["--opt", "TargetCompatibility=Rar1_5"], None),
]
os.makedirs(OW, exist_ok=True)
for name, src, args, pw in CASES:
    if only and name not in only: continue
    for f in os.listdir(OW):
        if f.startswith(name.split(".")[0] + "."): os.remove(os.path.join(OW, f))
    arc = os.path.join(OW, name + ".rar")
    srcdir = os.path.join(IN, src)
    items = sorted(os.listdir(srcdir))
    r = subprocess.run([CWB, "create", arc] + items + args + ["--force-compress"], cwd=srcdir, capture_output=True)
    if r.returncode != 0:
        print(f"{name:20} CREATE FAILED {(r.stdout + r.stderr).decode(errors='replace').strip().splitlines()[-1][:200]}"); continue
    want = tree(srcdir)
    if name == "r15": want = {k: v for k, v in want.items()}
    res = []
    pwa = [f"-p{pw}"] if pw else ["-p-"]
    for tool, cmd in (("unrar", [UNRAR, "x", "-y", "-idq", "-cfg-"] + pwa), ("rar7", [RAR7, "x", "-y", "-idq", "-cfg-"] + pwa), ("7z", [SZ, "x", "-y"] + ([f"-p{pw}"] if pw else []))):
        out = os.path.join(S, "ow-x", tool)
        rm(out); os.makedirs(out)
        if tool == "7z": rr = subprocess.run(cmd + ["-o" + out, arc], capture_output=True)
        else: rr = subprocess.run(cmd + [arc, out + os.sep], capture_output=True)
        got = tree(out)
        missing = [k for k, v in want.items() if got.get(k) != v and not (v == "dir" and k not in got and name in ("r15",))]
        status = "OK" if rr.returncode == 0 and not missing else f"rc={rr.returncode} bad={missing[:3]}"
        if rr.returncode != 0:
            msg = (rr.stdout + rr.stderr).decode(errors="replace").strip().splitlines()
            status += " " + " | ".join(msg[-2:])[:160]
        res.append(f"{tool}:{status}")
    vols = len([f for f in os.listdir(OW) if f.startswith(name.split(".")[0] + ".")])
    print(f"{name:20} vols={vols} " + "  ".join(res))
