"""Builds the interleaved-file reference images and reads them back through Windows' CDFS driver.

Usage (on Windows, with WSL and genisoimage): python -I mkinterleaved.py <outdir> <workdir>

No mastering tool writes interleaved files, so the images are made from plain ones: genisoimage
writes DATA.BIN contiguously, then its sectors are spread out as File Unit Size sectors of data
followed by Interleave Gap Size sectors of filler (0xA5), appended after the volume, and the
directory records of every tree are patched (extent, bytes 26 and 27). The original contiguous
copy is overwritten with 0x5A, so a reader that ignores the interleave reads the wrong bytes.
Windows mounts each image (Mount-DiskImage, CDFS) and reads DATA.BIN back; manifest-interleave.txt
records what it read, next to the SHA-256 of the file genisoimage was given.
"""
import gzip, hashlib, os, random, struct, subprocess, sys, tempfile

out, work = os.path.abspath(sys.argv[1]), os.path.abspath(sys.argv[2])
S = 2048
os.makedirs(work, exist_ok=True)


def wsl(path):
    return subprocess.run(['wsl', 'wslpath', '-a', path.replace('\\', '/')], capture_output=True, text=True).stdout.strip()


rng = random.Random(5)
data = bytes(rng.getrandbits(8) for _ in range(5 * S + 700))
open(os.path.join(work, 'DATA.BIN'), 'wb').write(data)


def both32(v):
    return struct.pack('<I', v) + struct.pack('>I', v)


def interleave(img, unit, gap):
    img = bytearray(img)
    new_lba = None
    for vd in range(16, 32):
        off = vd * S
        if img[off] == 255:
            break
        if img[off] not in (1, 2):
            continue
        rlba, rlen = struct.unpack_from('<I', img, off + 158)[0], struct.unpack_from('<I', img, off + 166)[0]
        pos, end = rlba * S, rlba * S + rlen
        while pos < end:
            n = img[pos]
            if n == 0:
                pos = (pos // S + 1) * S
                continue
            name = bytes(img[pos + 33:pos + 33 + img[pos + 32]])
            if name in (b'DATA.BIN;1', 'DATA.BIN;1'.encode('utf-16-be'), 'DATA.BIN'.encode('utf-16-be')):
                lba, size = struct.unpack_from('<I', img, pos + 2)[0], struct.unpack_from('<I', img, pos + 10)[0]
                if new_lba is None:
                    body = bytes(img[lba * S:lba * S + size])
                    sectors = (size + S - 1) // S
                    new_lba = len(img) // S
                    k = 0
                    while k < sectors:
                        chunk = body[k * S:(k + unit) * S]
                        img += chunk + bytes(unit * S - len(chunk))
                        k += unit
                        if k < sectors:
                            img += b'\xA5' * (gap * S)
                    img[lba * S:lba * S + sectors * S] = b'\x5A' * (sectors * S)
                img[pos + 2:pos + 10] = both32(new_lba)
                img[pos + 26] = unit
                img[pos + 27] = gap
            pos += n
    for vd in range(16, 32):
        off = vd * S
        if img[off] in (1, 2):
            img[off + 80:off + 88] = both32(len(img) // S)
        if img[off] == 255:
            break
    return bytes(img)


rows = [f"# image unit gap source-sha256 windows-cdfs-sha256 size -- DATA.BIN as genisoimage was given it and as Windows read it"]
for name, unit, gap, joliet in [('interleave-1-1', 1, 1, True), ('interleave-2-3', 2, 3, False)]:
    plain = os.path.join(work, name + '-plain.iso')
    subprocess.run(['wsl', 'genisoimage', '-quiet', '-o', wsl(plain), *(['-J'] if joliet else []), '-V', 'INTERLEAVE',
                    wsl(os.path.join(work, 'DATA.BIN'))], check=True)
    image = interleave(open(plain, 'rb').read(), unit, gap)
    iso = os.path.join(work, name + '.iso')
    open(iso, 'wb').write(image)
    script = os.path.join(work, 'read.ps1')
    with open(script, 'w') as f:
        f.write("param([string]$Iso)\n"
                "$img = Mount-DiskImage -ImagePath $Iso -PassThru\n"
                "try {\n"
                "  Start-Sleep -Milliseconds 500\n"
                "  $v = $img | Get-Volume\n"
                "  $p = \"$($v.DriveLetter):\\DATA.BIN\"\n"
                "  \"$((Get-FileHash $p -Algorithm SHA256).Hash.ToLower()) $((Get-Item $p).Length) $($v.FileSystem)\"\n"
                "} finally { Dismount-DiskImage -ImagePath $Iso | Out-Null }\n")
    r = subprocess.run(['powershell', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', script, '-Iso', iso],
                       capture_output=True, text=True, errors='replace')
    if len(r.stdout.split()) != 3:
        raise SystemExit(r.stdout + r.stderr)
    sha, size, fs = r.stdout.split()
    rows.append(f"{name} {unit} {gap} {hashlib.sha256(data).hexdigest()} {sha} {size}")
    with open(os.path.join(out, name + '.iso.gz'), 'wb') as f:
        f.write(gzip.compress(image, 9, mtime=0))
    print(name, fs, sha == hashlib.sha256(data).hexdigest())
with open(os.path.join(out, 'manifest-interleave.txt'), 'w', newline='\n') as f:
    f.write('\n'.join(rows) + '\n')
