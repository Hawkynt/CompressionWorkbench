# Deterministic RAR conformance inputs (Windows: sets creation time, attributes, an NTFS stream).
import ctypes, ctypes.wintypes as wt, os, random, shutil, sys

out, base = sys.argv[1], sys.argv[2]
if os.path.exists(out):
    shutil.rmtree(out, onerror=lambda f, p, e: (os.chmod(p, 0o666), f(p)))
tree = os.path.join(out, "tree")
os.makedirs(os.path.join(tree, "sub", "deep"))
os.makedirs(os.path.join(tree, "emptydir"))
text = open(os.path.join(base, "text.txt"), encoding="utf-8", newline="").read()

def w(path, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(data if isinstance(data, bytes) else data.encode("utf-8"))

w(os.path.join(tree, "text.txt"), text[:20000])
w(os.path.join(tree, "empty.dat"), b"")
w(os.path.join(tree, "sub", "deep", "small.txt"), "hello rar\n")
w(os.path.join(tree, "Grüße-日本語.txt"), "unicode name\n")
rnd = random.Random(4242)
w(os.path.join(tree, "noise.bin"), bytes(rnd.randrange(256) for _ in range(2048)))

kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
CreateFileW = kernel32.CreateFileW
CreateFileW.restype = wt.HANDLE
SetFileTime = kernel32.SetFileTime
CloseHandle = kernel32.CloseHandle

def ft(ticks_since_1601):
    return wt.FILETIME(ticks_since_1601 & 0xFFFFFFFF, ticks_since_1601 >> 32)

def to_ft(y, mo, d, h, mi, s, extra_ticks):
    import datetime
    dt = datetime.datetime(y, mo, d, h, mi, s)
    delta = dt - datetime.datetime(1601, 1, 1)
    return (delta.days * 86400 + delta.seconds) * 10_000_000 + extra_ticks

M = to_ft(2021, 2, 3, 4, 5, 6, 1234567)
C = to_ft(2020, 1, 2, 3, 4, 5, 7654321)
A = to_ft(2022, 3, 4, 5, 6, 7, 1111111)

def set_times(path):
    h = CreateFileW(path, 0x100, 7, None, 3, 0x02000000, None)  # FILE_WRITE_ATTRIBUTES, backup semantics for dirs
    if h == wt.HANDLE(-1).value:
        raise OSError(ctypes.get_last_error(), path)
    c, a, m = ft(C), ft(A), ft(M)
    if not SetFileTime(h, ctypes.byref(c), ctypes.byref(a), ctypes.byref(m)):
        raise OSError(ctypes.get_last_error(), path)
    CloseHandle(h)

paths = []
for root, dirs, files in os.walk(tree):
    for n in dirs + files:
        paths.append(os.path.join(root, n))
for p in sorted(paths, key=len, reverse=True):
    set_times(p)
set_times(tree)

FILE_ATTRIBUTE_READONLY, FILE_ATTRIBUTE_HIDDEN, FILE_ATTRIBUTE_ARCHIVE = 1, 2, 0x20
kernel32.SetFileAttributesW(os.path.join(tree, "noise.bin"), FILE_ATTRIBUTE_READONLY | FILE_ATTRIBUTE_ARCHIVE)
kernel32.SetFileAttributesW(os.path.join(tree, "sub", "deep", "small.txt"), FILE_ATTRIBUTE_HIDDEN | FILE_ATTRIBUTE_ARCHIVE)

ads = os.path.join(out, "ads")
w(os.path.join(ads, "host.txt"), "main stream\n")
with open(os.path.join(ads, "host.txt") + ":extra", "wb") as f:
    f.write(b"alternate stream data")

vol = os.path.join(out, "vol")
r2 = random.Random(777)
w(os.path.join(vol, "random.bin"), bytes(r2.randrange(256) for _ in range(49152)))
w(os.path.join(vol, "text.txt"), text[:30000])

links = os.path.join(out, "links")
w(os.path.join(links, "target.txt"), "link target\n")
os.link(os.path.join(links, "target.txt"), os.path.join(links, "hard.txt"))

dup = os.path.join(out, "dup")
w(os.path.join(dup, "one.txt"), text[:5000])
w(os.path.join(dup, "two.txt"), text[:5000])

ver = os.path.join(out, "ver")
w(os.path.join(ver, "doc.txt"), "version one\n")
print("inputs ok")
