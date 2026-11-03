# Select libarchive RAR test archives and record unrar 7.12's extraction as the expected output.
import hashlib, os, shutil, subprocess, sys
S = os.environ["RAR_FIXTURE_WORK"]  # work directory holding the tools and inputs (see README)
BIN = os.path.join(S, "la", "bin")
UNRAR = os.path.join(S, "wr712", "UnRAR.exe")
OUT = sys.argv[1]
os.makedirs(OUT, exist_ok=True)
PW = {"test_read_format_rar5_encrypted.rar": "password", "test_read_format_rar5_encrypted_filenames.rar": "password",
      "test_read_format_rar5_solid_encrypted.rar": "password", "test_read_format_rar5_solid_encrypted_filenames.rar": "password",
      "test_read_format_rar4_encrypted.rar": "password", "test_read_format_rar4_encrypted_filenames.rar": "password",
      "test_read_format_rar4_solid_encrypted.rar": "password", "test_read_format_rar4_solid_encrypted_filenames.rar": "password",
      "test_read_format_rar_encryption_data.rar": "12345678", "test_read_format_rar_encryption_header.rar": "12345678",
      "test_read_format_rar_encryption_partially.rar": "12345678"}
# Known, documented differences from unrar (see README): kept out of the byte-exact set.
SKIP = {"test_read_format_rar5_extra_field_version.rar"}
man, bad = [], []
for name in sorted(os.listdir(BIN)):
    p = os.path.join(BIN, name)
    if os.path.getsize(p) > 100_000 or name.endswith(".Z"): continue
    first = name
    if ".part" in name:
        if not (name.endswith("part01.rar") or name.endswith("part0001.rar")):
            shutil.copy(p, OUT); continue
    shutil.copy(p, OUT)
    if name in SKIP: continue
    tmp = os.path.join(S, "lc", "m")
    if os.path.exists(tmp): shutil.rmtree(tmp, onerror=lambda f, x, e: (os.chmod(x, 0o777), f(x)))
    os.makedirs(tmp)
    pw = PW.get(name)
    r = subprocess.run([UNRAR, "x", "-y", "-idq", "-cfg-", "-ol-", ("-p" + pw) if pw else "-p-", p, tmp + os.sep], capture_output=True)
    files = []
    for root, ds, fs in os.walk(tmp):
        for f in fs:
            fp = os.path.join(root, f)
            data = open(fp, "rb").read()
            files.append((os.path.relpath(fp, tmp).replace("\\", "/"), len(data), hashlib.sha256(data).hexdigest()))
    if r.returncode == 0:
        for rel, size, sha in files:
            man.append("\t".join([name, rel, "f", str(size), sha, pw or ""]))
    else:
        # unrar refuses (part of) it: we must refuse too, or produce only what unrar produced.
        bad.append("\t".join([name, str(r.returncode), pw or ""] + [f"{rel}={sha}" for rel, _, sha in files]))
open(os.path.join(OUT, "manifest.tsv"), "w", newline="\n", encoding="utf-8").write("\n".join(man) + "\n")
open(os.path.join(OUT, "refused.tsv"), "w", newline="\n", encoding="utf-8").write("\n".join(bad) + "\n")
print(len(man), "entries;", len(bad), "refused archives")
