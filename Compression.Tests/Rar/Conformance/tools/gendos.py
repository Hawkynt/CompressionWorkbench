# Generate RAR fixtures with the DOS RAR releases under DOSBox-X, and test them with unrar 7.12.
# usage: gendos.py <out>
import hashlib, os, random, shutil, subprocess, sys
S = os.environ["RAR_FIXTURE_WORK"]  # work directory holding the tools and inputs (see README)
DBX = os.path.join(S, "dosbox", "x", "bin", "x64", "Release", "dosbox-x.exe")
UNRAR = os.path.join(S, "wr712", "UnRAR.exe")
OUT = sys.argv[1]
os.makedirs(OUT, exist_ok=True)
EXE = {
    "140": os.path.join(S, "dosrun", "t1", "RAR.EXE"),
    "155": os.path.join(S, "dos", "rar155", "RAR.EXE"),
    "200": os.path.join(S, "dos", "rar200", "RAR.EXE"),
    "250": os.path.join(S, "dos", "rar250", "RAR.EXE"),
    "393": os.path.join(S, "dos", "rarx393", "rar", "rar32.exe"),
}
SFX = {"155": "IDOS.SFX", "200": "IDOS.SFX", "250": "IDOS.SFX"}

text = open(os.path.join(S, "fxin", "tree", "text.txt"), "rb").read()[:8000]
noise = open(os.path.join(S, "fxin", "tree", "noise.bin"), "rb").read()
_r = random.Random(1234)
big = bytes(_r.randrange(256) for _ in range(12000)) + text[:4000]
wav = open(os.path.join(S, "in", "audio.wav"), "rb").read()[:8044]
INPUTS = {"TEXT.TXT": text, "NOISE.BIN": noise, "EMPTY.DAT": b"", "SUB/SMALL.TXT": b"hello rar\r\n", "BIG.BIN": big, "AUDIO.WAV": wav}

def dos(work, cmds, timeout=120):
    # DOSBox takes only a few -c commands, so the work goes into a batch file.
    CRLF = chr(13) + chr(10)
    open(os.path.join(work, "GO.BAT"), "wb").write(("@echo off" + CRLF + CRLF.join(cmds) + CRLF).encode("ascii"))
    args = [DBX, "-silent", "-nolog", "-fastlaunch", "-c", f'mount c "{work}"', "-c", "c:", "-c", "call GO.BAT", "-c", "exit"]
    try:
        subprocess.run(args, timeout=timeout, capture_output=True)
    except subprocess.TimeoutExpired:
        print("  TIMEOUT", cmds)

manifest, log = [], []
for ver, exe in EXE.items():
    work = os.path.join(S, "dosrun", "g" + ver)
    if os.path.exists(work): shutil.rmtree(work, onerror=lambda f, p, e: (os.chmod(p, 0o777), f(p)))
    os.makedirs(os.path.join(work, "SUB"))
    for n, d in INPUTS.items():
        open(os.path.join(work, n.replace("/", os.sep)), "wb").write(d)
    if ver == "393":
        for f in os.listdir(os.path.dirname(exe)):
            if f.lower() != "default.sfx": shutil.copy(os.path.join(os.path.dirname(exe), f), work)
    else:
        shutil.copy(exe, os.path.join(work, "RAR.EXE"))
    R = "rar32" if ver == "393" else "RAR"
    if ver in SFX: shutil.copy(os.path.join(os.path.dirname(exe), SFX[ver]), os.path.join(work, SFX[ver]))
    files = "TEXT.TXT NOISE.BIN EMPTY.DAT"
    cmds = []
    jobs = []
    for m in (0, 3, 5):
        jobs.append((f"M{m}.RAR", f"{R} a -m{m} M{m}.RAR {files}", files.split(), None))
    jobs.append(("SOLID.RAR", f"{R} a -s SOLID.RAR {files}", files.split(), None))
    jobs.append(("TREE.RAR", f"{R} a -r TREE.RAR TEXT.TXT SUB\\SMALL.TXT", ["TEXT.TXT", "SUB/SMALL.TXT"], None))
    jobs.append(("PW.RAR", f"{R} a -psecret PW.RAR {files}", files.split(), "secret"))
    jobs.append(("PWS.RAR", f"{R} a -m0 -psecret PWS.RAR {files}", files.split(), "secret"))
    jobs.append(("VOL.RAR", f"{R} a -v10 VOL.RAR BIG.BIN TEXT.TXT", ["BIG.BIN", "TEXT.TXT"], None))
    if ver in ("200", "250"):
        jobs.append(("MM.RAR", f"{R} a -mm MM.RAR AUDIO.WAV TEXT.TXT", ["AUDIO.WAV", "TEXT.TXT"], None))
        jobs.append(("SOLMM.RAR", f"{R} a -s -mm SOLMM.RAR AUDIO.WAV TEXT.TXT NOISE.BIN", ["AUDIO.WAV", "TEXT.TXT", "NOISE.BIN"], None))
    if ver == "393":
        jobs.append(("HP.RAR", f"{R} a -hpsecret HP.RAR {files}", files.split(), "secret"))
        jobs.append(("PPM.RAR", f"{R} a -mct+ -m5 PPM.RAR TEXT.TXT", ["TEXT.TXT"], None))
        jobs.append(("AUD.RAR", f"{R} a -mcA+ AUD.RAR AUDIO.WAV", ["AUDIO.WAV"], None))
    if ver in SFX:
        jobs.append(("SFX.EXE", f"{R} a -sfx SFX.RAR {files}", files.split(), None))
    for name, cmd, _, _ in jobs:
        cmds.append(cmd + " > NUL")
    if ver == "393": cmds.insert(0, "set PATH=C:\\")
    dos(work, cmds, timeout=300)
    for name, cmd, ents, pw in jobs:
        p = os.path.join(work, name)
        if not os.path.exists(p):
            print(f"{ver} {name}: NOT CREATED ({cmd})"); continue
        vols = sorted(f for f in os.listdir(work) if f.upper().startswith(name[:-4] + ".") and f.upper() != name and f[-3:].upper()[0] in "RS" and f[-2:].isdigit())
        tag = f"rar{ver}-{name.lower()}"
        dst = os.path.join(OUT, tag)
        shutil.copy(p, dst)
        for v in vols:
            shutil.copy(os.path.join(work, v), os.path.join(OUT, f"rar{ver}-{v.lower()}"))
        r = subprocess.run([UNRAR, "t", "-idq", "-cfg-", ("-p" + pw) if pw else "-p-", dst], capture_output=True)
        print(f"{ver} {name}: size={os.path.getsize(p)} vols={vols} unrar7={r.returncode}")
        for e in ents:
            d = INPUTS[e]
            manifest.append("\t".join([tag, e, "f", str(len(d)), hashlib.sha256(d).hexdigest(), pw or ""]))
        log.append(f"RAR {ver}: {cmd}")
open(os.path.join(OUT, "manifest-dos.tsv"), "w", newline="\n").write("\n".join(manifest) + "\n")
open(os.path.join(OUT, "commands-dos.txt"), "w", newline="\n").write("\n".join(log) + "\n")
