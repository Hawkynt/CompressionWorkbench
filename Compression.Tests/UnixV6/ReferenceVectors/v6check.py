"""Checks a V6 volume with the V6 system itself, on SIMH's simulated PDP-11.

usage: python3 -I v6check.py <V6 root image> <volume image>

Boots UNIX V6 from a copy of the root image (SIMH's uv6swre kit, unix0_v6_rk.dsk) with
the volume attached as RK05 drive 4 (the kit's /etc/rc mounts drives 1 to 3), then runs icheck and dcheck on it, mounts it,
lists it, copies files inside it (so the kernel both reads our blocks and allocates from
our free list), unmounts it and checks it again. The volume image is changed in place;
the console transcript goes to stdout, each step introduced by a line "== <step>".
"""
import os, pty, select, shutil, subprocess, sys, tempfile, time, tty

root, volume = sys.argv[1], os.path.abspath(sys.argv[2])
steps = [
    'echo == icheck', 'icheck /dev/rk4',
    'echo == dcheck', 'dcheck /dev/rk4',
    '/etc/mount /dev/rk4 /mnt',
    'echo == ls', 'ls -l /mnt', 'ls -l /mnt/docs',
    'cp /mnt/big /mnt/big.v6',
    'cp /mnt/docs/notes /mnt/docs/notes.v6',
    'mkdir /mnt/made',
    'cp /mnt/hello /mnt/made/hello',
    'sync',
    '/etc/umount /dev/rk4',
    'echo == icheck2', 'icheck /dev/rk4',
    'echo == dcheck2', 'dcheck /dev/rk4',
    'echo == done',
]

work = tempfile.mkdtemp()
shutil.copy(root, os.path.join(work, 'root.img'))
with open(os.path.join(work, 'v6.ini'), 'w') as ini:
    ini.write('set cpu 11/45\nset rk enabled\natt rk0 root.img\natt rk4 %s\nboot rk0\n' % volume)

master, slave = pty.openpty()
tty.setraw(slave)  # the simulator must see carriage returns as typed
proc = subprocess.Popen(['pdp11', 'v6.ini'], cwd=work, stdin=slave, stdout=slave, stderr=slave, close_fds=True)
os.close(slave)
log = []

def read_until(token, timeout=60):
    end = time.time() + timeout
    buf = ''
    while time.time() < end:
        if select.select([master], [], [], 0.2)[0]:
            try:
                data = os.read(master, 4096).decode('latin1')
            except OSError:
                break
            buf += data
            log.append(data)
            if token in buf:
                return
    raise TimeoutError('waiting for %r' % token)

def send(text):
    for ch in text:  # the console takes one character at a time
        os.write(master, ch.encode('latin1'))
        time.sleep(0.01)

status = 0
try:
    read_until('@')
    send('unix\r')
    read_until('login:')
    send('root\r')
    read_until('# ')
    for step in steps:
        send(step + '\r')
        read_until('# ', 300)
    send('sync\r')
    read_until('# ')
    time.sleep(1)
except TimeoutError as e:
    print('TIMEOUT', e)
    status = 1
finally:
    os.write(master, b'\x05')  # ^E: back to the simulator prompt
    time.sleep(0.5)
    os.write(master, b'quit\r')
    time.sleep(0.5)
    proc.kill()
    proc.wait()
    print(''.join(log).replace('\r', ''))
    shutil.rmtree(work, ignore_errors=True)
sys.exit(status)
