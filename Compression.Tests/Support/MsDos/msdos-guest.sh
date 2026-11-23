#!/bin/bash
# Boots a real MS-DOS under qemu with a FAT16 host hard disk carrying a compressed volume file, runs a
# batch file and copies out what it wrote. See MsDosGuest.cs / MsDosImageStaging.md.
#
# usage: msdos-guest.sh <kind> <boot> <tool source> <product> <volume or -> <result dir> <AUTOEXEC.BAT>
#                       <payload dir or -> <host cylinders> <max wait seconds>
#   kind dos   : <boot> is install disk 1 of MS-DOS 6.x; <product>.EXE (or the compressed .EX_) is
#                looked up on the install disks beside it (<tool source> is ignored).
#   kind dos7  : <boot> is a bootable MS-DOS 7.x floppy, <tool source> its DRVSPACE.BIN.
#   kind win98 : <boot> is a Windows 98 CD image; its boot floppy, DRVSPACE.BIN and SCANDISK come
#                from the CD.
#   product    : DBLSPACE or DRVSPACE — names the volume file (<product>.000), the INI file and the tool.
. "$(dirname "$(readlink -f "$0")")/../../Support/cwb-rmtree.sh"
set -u
exec < /dev/null          # mtools must never stop to ask
KIND="$1"; BOOT="$2"; TOOLSRC="$3"; PRODUCT="$4"; CVF="$5"; RES="$6"; AUTO="$7"; PAY="$8"; CYL="$9"; MAXWAIT="${10}"
W=$(mktemp -d /tmp/cwbdos.XXXXXX)
QP=0
trap '[ $QP -ne 0 ] && kill $QP 2>/dev/null; cwb_rmtree "$W"' EXIT
mkdir -p "$RES"
FLOPPY_B=()
PRE=""

if [ "$KIND" = win98 ]; then
  # The CD's El Torito boot floppy carries the real-mode DRVSPACE.BIN in its root and SCANDISK in its
  # EBD.CAB.
  geteltorito -o "$W/boot.img" "$BOOT" > /dev/null 2>&1 || { echo "no boot image on $BOOT"; exit 3; }
  mkdir -p "$W/cab"
  mcopy -n -i "$W/boot.img" ::DRVSPACE.BIN "$W/cab/DRVSPACE.BIN" 2>/dev/null
  mcopy -n -i "$W/boot.img" ::EBD.CAB "$W/cab/EBD.CAB" 2>/dev/null && cabextract -q -F scandisk.exe -d "$W/cab" "$W/cab/EBD.CAB" > /dev/null 2>&1
  DRV="$W/cab/DRVSPACE.BIN"; SCAN=$(find "$W/cab" -iname scandisk.exe | head -1)
  [ -f "$DRV" ] || { echo "DRVSPACE.BIN not found on the CD's boot floppy"; exit 3; }
  TOOLSRC="$DRV"
else
  cp "$BOOT" "$W/boot.img"
fi

# Room on the floppy: drop installer-only files neither boot nor the drivers need.
for f in SETUP.EXE SETUP.MSG DOSSETUP.INI BUSETUP.EXE COUNTRY.TX_ EGA.CP_ EGA2.CP_ EGA3.CP_ ISO.CP_ KEYBRD2.SY_ \
         EMM386.EX_ MEM.EX_ MSCDEX.EXE QBASIC.EXE README.TXT REPLACE.EX_ RESTORE.EX_ XCOPY.EX_ DEFRAG.EXE DEFRAG.HL_ \
         NETWORKS.TXT PACKING.LST FDISK.EXE DEBUG.EXE EDIT.COM KEYB.COM KEYBOARD.SYS NLSFUNC.EXE COUNTRY.SYS \
         8514.VI_ CGA.GR_ CGA.IN_ CGA.VI_ EGA.GR_ EGA.IN_ EGA.SY_ EGA.VI_ EGAMONO.GR_ HERC.GR_ HERC.VI_ DOSSWAP.EX_ \
         DELTREE.EX_ GDISK.EXE PKUNZIP.EXE MESSAGES.EXE SUSCR.EXE BOOTPART.EXE SETUP.BAT SETUP.INF CHANGE.EXE \
         MCD.EXE XMSDSK.EXE DOSKEY.COM CTMOUSE.EXE VIDE-CDD.SYS ELTORITO.SYS LICENSE.TXT FINDCD.EXE WBAT.COM \
         HIMEM.SYS KILLER.EXE INIFILE.COM EBD.CAB EXTRACT.EXE OAKCDROM.SYS BTDOSM.SYS FLASHPT.SYS BTCDROM.SYS \
         ASPI2DOS.SYS ASPI8DOS.SYS ASPI4DOS.SYS ASPI8U2.SYS ASPICD.SYS README.TXT RAMDRIVE.SYS SETRAMD.BAT \
         FIND.EXE CHECKSR.BAT; do
  [ "$KIND" = win98 ] && [ "$f" = HIMEM.SYS ] && continue     # Windows 98's SCANDISK needs XMS
  mdel -i "$W/boot.img" "::$f" 2>/dev/null
done

case "$KIND" in
  dos)
    # The tool: plain on one of the install disks, or compressed (.EX_) and expanded in the guest.
    for img in "$(dirname "$BOOT")"/*.[iI][mM][gG]; do
      if mdir -i "$img" "::$PRODUCT.EXE" > /dev/null 2>&1; then
        mcopy -n -i "$img" "::$PRODUCT.EXE" "$W/$PRODUCT.EXE" && mcopy -o -i "$W/boot.img" "$W/$PRODUCT.EXE" "::$PRODUCT.EXE"
        break
      fi
      if mdir -i "$img" "::$PRODUCT.EX_" > /dev/null 2>&1; then
        FLOPPY_B=(-drive file="$img",format=raw,if=floppy,index=1,readonly=on)
        PRE="EXPAND B:\\$PRODUCT.EX_ A:\\$PRODUCT.EXE > NUL"
        break
      fi
    done
    INI="MaxRemovableDrives=2\r\nFirstDrive=D\r\nLastDrive=H\r\nMaxFileFragments=117\r\n"
    MEM=16 ;;
  dos7|win98)
    if [ "$KIND" = dos7 ]; then
      mattrib -i "$W/boot.img" -r -s -h ::DRVSPACE.BIN 2>/dev/null
      mcopy -o -i "$W/boot.img" "$TOOLSRC" ::DRVSPACE.BIN
    fi
    [ "$KIND" = win98 ] && [ -n "$SCAN" ] && mcopy -o -i "$W/boot.img" "$SCAN" ::SCANDISK.EXE
    INI="MaxRemovableDrives=2\r\nFirstDrive=D\r\nLastDrive=H\r\nMaxFileFragments=117\r\nActivateDrive=E,C0\r\n"
    # MS-DOS 7.1's floppy needs the driver switched on. Windows 98's boot floppy keeps its own, empty
    # MSDOS.SYS: its IO.SYS loads DRVSPACE.BIN by default and stops at "Starting Windows 98..." once
    # MSDOS.SYS has an [Options] section.
    if [ "$KIND" = dos7 ]; then
      printf '[Paths]\r\nWinDir=C:\\WINDOWS\r\nWinBootDir=C:\\WINDOWS\r\nHostWinBootDrv=C\r\n[Options]\r\nBootGUI=0\r\nLogo=0\r\nDrvSpace=1\r\nDblSpace=1\r\nAutoScan=0\r\n' > "$W/MSDOS.SYS"
      mattrib -i "$W/boot.img" -r -s -h ::MSDOS.SYS 2>/dev/null
      mcopy -o -i "$W/boot.img" "$W/MSDOS.SYS" ::MSDOS.SYS
      mattrib -i "$W/boot.img" +r +s +h ::MSDOS.SYS
    fi
    MEM=32 ;;
esac
printf "$INI" > "$W/$PRODUCT.INI"
mcopy -o -i "$W/boot.img" "$W/$PRODUCT.INI" "::$PRODUCT.INI"
: > "$W/CONFIG.SYS"
[ "$KIND" = win98 ] && printf 'DEVICE=A:\\HIMEM.SYS /TESTMEM:OFF\r\n' > "$W/CONFIG.SYS"
printf 'FILES=30\r\nBUFFERS=20\r\nLASTDRIVE=Z\r\n' >> "$W/CONFIG.SYS"
mcopy -o -i "$W/boot.img" "$W/CONFIG.SYS" ::CONFIG.SYS
printf '[ENVIRONMENT]\r\n   Display = Mono\r\n   Mouse = Off\r\n   ScanTimeOut = Off\r\n   NumPasses = 1\r\n   LabelCheck = Off\r\n[CUSTOM]\r\n   DriveSummary = Off\r\n   AllSummary = Off\r\n   Surface = Never\r\n   CheckHost = Never\r\n   SaveLog = Overwrite\r\n   Undo = Never\r\n' > "$W/SCANDISK.INI"
for k in DS_Header FAT_Media Okay_Entries Bad_Chain Crosslinks Boot_Sector Invalid_MDFAT DS_Crosslinks DS_LostClust DS_Signatures Mismatch_FAT Bad_Clusters; do
  printf '   %s = Fix\r\n' $k >> "$W/SCANDISK.INI"
done
printf '   Bad_Entries = Delete\r\n   LostClust = Save\r\n' >> "$W/SCANDISK.INI"
mcopy -o -i "$W/boot.img" "$W/SCANDISK.INI" ::SCANDISK.INI
{ [ -n "$PRE" ] && printf '@ECHO OFF\r\n%s\r\n' "$PRE"; cat "$AUTO"; } > "$W/AUTOEXEC.BAT"
mcopy -o -i "$W/boot.img" "$W/AUTOEXEC.BAT" ::AUTOEXEC.BAT
mdel -i "$W/boot.img" ::LOG.TXT 2>/dev/null
if [ "$PAY" != "-" ]; then mmd -i "$W/boot.img" ::IN; mcopy -i "$W/boot.img" "$PAY"/* ::IN/ || echo "payload does not fit the floppy"; fi

# Host hard disk: CYL x 16 heads x 63 sectors, one FAT16 partition from sector 63.
truncate -s $((CYL*16*63*512)) "$W/hdd.img"
echo '63,,6,*' | sfdisk -q "$W/hdd.img" > /dev/null 2>&1
mformat -i "$W/hdd.img@@32256" -T $((CYL*16*63-63)) -h 16 -s 63 -H 63 -v HOST ::
if [ "$CVF" != "-" ]; then
  mcopy -i "$W/hdd.img@@32256" "$CVF" "::$PRODUCT.000"
  mattrib -i "$W/hdd.img@@32256" +h +s +r "::$PRODUCT.000"
fi
qemu-system-i386 -m $MEM -display none -no-reboot \
  -drive file="$W/boot.img",format=raw,if=floppy,index=0,cache=writethrough "${FLOPPY_B[@]}" \
  -drive file="$W/hdd.img",format=raw,if=ide,index=0,cache=writethrough \
  -monitor unix:$W/mon,server,nowait -boot a > "$W/qemu.log" 2>&1 &
QP=$!
if [ "$KIND" = win98 ]; then
  # The CD's boot floppy opens with "1. Boot from Hard Disk  2. Boot from CD-ROM": answer 2.
  sleep 3
  python3 - "$W/mon" <<'PY'
import socket, sys, time
s = socket.socket(socket.AF_UNIX); s.connect(sys.argv[1]); time.sleep(0.3)
s.send(b'sendkey 2\n'); time.sleep(0.5)
PY
fi
for i in $(seq 1 $((MAXWAIT/2))); do
  sleep 2
  if mtype -i "$W/boot.img" ::LOG.TXT 2>/dev/null | grep -q DONE; then sleep 2; break; fi
done
# The text screen at the end (what an interactive prompt would be waiting on).
python3 - "$W/mon" "$RES/screen.txt" <<'PY'
import os, socket, sys, time
s = socket.socket(socket.AF_UNIX); s.connect(sys.argv[1]); time.sleep(0.5)
tmp = sys.argv[2] + '.bin'
s.send(('pmemsave 0xb8000 4000 "' + tmp + '"\r\n').encode())
for _ in range(20):
    time.sleep(0.5)
    if os.path.exists(tmp) and os.path.getsize(tmp) == 4000: break
b = open(tmp, 'rb').read() if os.path.exists(tmp) else bytes(4000)
rows = [''.join(chr(b[(r * 80 + c) * 2]) if 32 <= b[(r * 80 + c) * 2] < 127 else ' ' for c in range(80)).rstrip() for r in range(25)]
open(sys.argv[2], 'w').write('\n'.join(rows) + '\n')
PY
kill $QP 2>/dev/null; wait $QP 2>/dev/null; QP=0
mtype -i "$W/boot.img" ::LOG.TXT > "$RES/log.txt" 2>/dev/null
mtype -i "$W/boot.img" ::SCANDISK.LOG > "$RES/scandisk.txt" 2>/dev/null
mcopy -n -s -i "$W/boot.img" ::OUT "$RES/" 2>/dev/null
for n in 000 001; do mcopy -n -i "$W/hdd.img@@32256" "::$PRODUCT.$n" "$RES/$PRODUCT.$n" 2>/dev/null; done
exit 0
