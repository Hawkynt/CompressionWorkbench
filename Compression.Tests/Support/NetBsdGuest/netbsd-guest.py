#!/usr/bin/env python3
"""Run shell commands in a NetBSD guest booted from an official live image under qemu.

usage: netbsd-guest.py <live.img> <data.img> <command-file>

The live image boots read-only (snapshot=on); <data.img> is attached as the second disk
(wd1, raw: /dev/rwd1d) for exchanging images. The boot menu is driven through the qemu
monitor (option 3, then `consdev com0`), after which the boot prompt, the kernel console
and a root shell are on the serial line. Each non-empty line of the command file runs in
that shell and its output is printed. Memory is capped at 1 GiB; KVM is used when present.
Environment: NB_TIMEOUT (seconds per command, default 600), NB_LOGIN (seconds to boot, 240).
"""
import atexit, os, socket, subprocess, sys, time

live, data, cmdfile = sys.argv[1], sys.argv[2], sys.argv[3]
mon = f"/tmp/cwb-netbsd-mon-{os.getpid()}.sock"   # unix sockets need a Linux file system, not /mnt/c
port = 20000 + os.getpid() % 20000
accel = ["-enable-kvm"] if os.access("/dev/kvm", os.R_OK | os.W_OK) else ["-accel", "tcg"]
qemu = subprocess.Popen(["qemu-system-x86_64", *accel, "-m", "1024", "-smp", "1",
    "-drive", f"file={live},format=raw,if=ide,index=0,snapshot=on",
    "-drive", f"file={data},format=raw,if=ide,index=1,file.locking=off",
    "-display", "none", "-vga", "std", "-nic", "none",
    "-monitor", f"unix:{mon},server,nowait",
    "-serial", f"tcp:127.0.0.1:{port},server,nowait"], stdout=subprocess.DEVNULL, stderr=open(f"/tmp/cwb-netbsd-qemu-{os.getpid()}.log", "w"))
atexit.register(lambda: qemu.poll() is None and qemu.kill())
atexit.register(lambda: os.path.exists(mon) and os.unlink(mon))

def connect(addr, family=socket.AF_INET, tries=100):
    for _ in range(tries):
        try:
            s = socket.socket(family, socket.SOCK_STREAM); s.connect(addr); return s
        except OSError:
            time.sleep(0.2)
    log = f"/tmp/cwb-netbsd-qemu-{os.getpid()}.log"
    raise SystemExit(f"cannot connect {addr}: " + (open(log).read() if os.path.exists(log) else ""))

m = connect(mon, socket.AF_UNIX)
def monitor(cmd):
    m.sendall((cmd + "\n").encode()); time.sleep(0.3)

ser = connect(("127.0.0.1", port))
ser.settimeout(0.5)
buf = b""
def read_until(token, timeout):
    global buf
    end = time.time() + timeout
    while time.time() < end:
        if token in buf:
            i = buf.index(token) + len(token); out = buf[:i]; buf = buf[i:]; return out
        try:
            chunk = ser.recv(65536)
            if chunk: buf += chunk
        except socket.timeout:
            pass
    raise TimeoutError(f"waiting for {token!r}; have {buf[-400:]!r}")

def keys(text):
    names = {" ": "spc", "\n": "ret"}
    for ch in text:
        monitor("sendkey " + names.get(ch, ch))

time.sleep(2)
keys(" ")             # stop the boot menu countdown
keys("3\n")           # "Drop to boot prompt"
time.sleep(2)
keys("consdev com0\n")
read_until(b"> ", 20)  # the boot prompt now answers on com0
ser.sendall(b"boot\r")
read_until(b"login:", int(os.environ.get("NB_LOGIN", "240")))
ser.sendall(b"root\n")
read_until(b"# ", 60)
ser.sendall(b"PS1='CWB''# '; export PS1\n")
read_until(b"CWB# ", 30)
for line in open(cmdfile):
    line = line.rstrip("\n")
    if not line:
        continue
    ser.sendall((line + "\n").encode())
    out = read_until(b"CWB# ", int(os.environ.get("NB_TIMEOUT", "600")))
    sys.stdout.write(out[:-5].decode("latin1")); sys.stdout.flush()
ser.sendall(b"sync; halt -p\n")
time.sleep(5)
