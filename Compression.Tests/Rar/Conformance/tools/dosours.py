# Archives written by cwb in the old formats, tested and extracted by the DOS RAR of their era.
import hashlib, os, shutil, subprocess, sys
sys.stdout.reconfigure(encoding="utf-8", errors="replace")
S = os.environ["RAR_FIXTURE_WORK"]  # work directory holding the tools and inputs (see README)
CWB = os.environ["CWB_EXE"]
DBX = os.path.join(S, "dosbox", "x", "bin", "x64", "Release", "dosbox-x.exe")
SRC = os.path.join(S, "dosrun", "g155")
FILES = ["TEXT.TXT", "NOISE.BIN", "EMPTY.DAT", "BIG.BIN"]
TOOLS = {
    "155": (os.path.join(S, "dos", "rar155"), ["RAR.EXE"], "RAR"),
    "200": (os.path.join(S, "dos", "rar200"), ["RAR.EXE"], "RAR"),
    "250": (os.path.join(S, "dos", "rar250"), ["RAR.EXE", "UNRAR.EXE"], "RAR"),
    "393": (os.path.join(S, "dos", "rarx393", "rar"), None, "rar32"),
}
CASES = [  # name, cwb args, password, which DOS versions must accept it
    ("OURS15", ["--opt", "TargetCompatibility=Rar1_5"], None, ["155", "200", "250", "393"]),
    ("OURS20S", ["--opt", "TargetCompatibility=Rar4", "-m", "store"], None, ["200", "250", "393"]),
    ("OURS29", ["--opt", "TargetCompatibility=Rar4", "--solid-size", "1"], None, ["393"]),
    ("OURS29SO", ["--opt", "TargetCompatibility=Rar4"], None, ["393"]),
    ("OURS29P", ["--opt", "TargetCompatibility=Rar4", "-p", "secret", "--solid-size", "1"], "secret", ["393"]),
    ("OURS29V", ["--opt", "TargetCompatibility=Rar4", "-v", "16k", "--solid-size", "1"], None, ["393"]),
]
CRLF = chr(13) + chr(10)
for ver, (tdir, files, exe) in TOOLS.items():
    work = os.path.join(S, "dosrun", "o" + ver)
    if os.path.exists(work): shutil.rmtree(work, onerror=lambda f, p, e: (os.chmod(p, 0o777), f(p)))
    os.makedirs(work)
    for f in (files or os.listdir(tdir)):
        shutil.copy(os.path.join(tdir, f), work)
    lines = ["@echo off", "set PATH=C:\\", f"{exe} t -y CTRL.RAR > CTRL.T", "if errorlevel 1 echo FAIL > CTRL.ERR"]
    shutil.copy(os.path.join(S, "dosfx", "rar155-m3.rar"), os.path.join(work, "CTRL.RAR"))
    for name, args, pw, vers in CASES:
        if ver not in vers: continue
        arc = os.path.join(work, name + ".RAR")
        r = subprocess.run([CWB, "create", arc] + FILES + args + ["--force-compress"], cwd=SRC, capture_output=True)
        if r.returncode != 0:
            print(ver, name, "CREATE FAILED"); continue
        os.makedirs(os.path.join(work, name))
        p = f"-p{pw} " if pw else ""
        lines.append(f"{exe} t -y {p}{name}.RAR > {name}.T")
        lines.append(f"if errorlevel 1 echo FAIL > {name}.ERR")
        lines.append(f"cd {name}")
        lines.append(f"..\\{exe} x -y {p}..\\{name}.RAR > ..\\{name}.X")
        lines.append(f"if errorlevel 1 echo FAIL > ..\\{name}.XER")
        lines.append("cd ..")
    open(os.path.join(work, "GO.BAT"), "wb").write(CRLF.join(lines).encode("ascii") + CRLF.encode())
    try:
        subprocess.run([DBX, "-silent", "-nolog", "-fastlaunch", "-c", f'mount c "{work}"', "-c", "c:", "-c", "call GO.BAT", "-c", "exit"], timeout=300, capture_output=True)
    except subprocess.TimeoutExpired:
        print(ver, "TIMEOUT")
    print(f"RAR {ver} control (RAR 1.55 archive) errorlevel-fail={os.path.exists(os.path.join(work, 'CTRL.ERR'))}")
    for name, args, pw, vers in CASES:
        if ver not in vers: continue
        t_err = os.path.exists(os.path.join(work, name + ".ERR"))
        x_err = os.path.exists(os.path.join(work, name + ".XER"))
        same = all(os.path.exists(os.path.join(work, name, f)) and open(os.path.join(work, name, f), "rb").read() == open(os.path.join(SRC, f), "rb").read() for f in FILES)
        log = ""
        tp = os.path.join(work, name + ".T")
        if os.path.exists(tp) and (t_err or not same):
            log = open(tp, "rb").read().decode("cp437", "replace").replace(CRLF, " | ")[-240:]
        print(f"RAR {ver} {name:9} test={'FAIL' if t_err else 'ok'} extract={'FAIL' if x_err else 'ok'} byte-exact={same} {log}")
