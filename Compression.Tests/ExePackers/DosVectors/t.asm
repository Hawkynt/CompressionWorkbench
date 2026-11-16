; Test DOS MZ program for packer round trips.
; nasm -f bin -DNREL=n -DNTEXT=n -DNRAND=n -DNFAR=n -o T.EXE t.asm
; Prints OK when every relocated far pointer in its tables matches its segment
; and the data checksum matches the one computed at build time, BAD otherwise.
%ifndef NREL
%define NREL 4
%endif
%ifndef NTEXT
%define NTEXT 40
%endif
%ifndef NRAND
%define NRAND 512
%endif
%ifndef NFAR
%define NFAR 2
%endif
%ifndef SEED
%define SEED 12345
%endif
%define DATAPARA 0x100

bits 16
org 0
mz:     db 'MZ'
        dw (fend - mz) % 512
        dw ((fend - mz) + 511) / 512
        dw NREL + NFAR + 2
        dw (img - mz) / 16
        dw 0x40
        dw 0xFFFF
        dw stackpara
        dw 0x0200
        dw 0
        dw entry - img
        dw 0
        dw reloc - mz
        dw 0
reloc:  dw entry - img + 1, 0
        dw farfix - img + 1, 0
%assign i 0
%rep NREL
        dw tbl - data + 4*i + 2, DATAPARA
%assign i i+1
%endrep
%assign i 0
%rep NFAR
        dw 4*i + 2, farpara
%assign i i+1
%endrep
        times (-($ - mz)) & 15 db 0
img:
entry:  mov ax, DATAPARA          ; relocated: DS = load segment + DATAPARA
        mov ds, ax
        mov dx, ax
        mov cx, NREL
        mov si, tbl - data
.chk:   cmp [si + 2], dx
        jne bad
        add si, 4
        loop .chk
farfix: mov ax, farpara           ; relocated: ES = load segment + farpara
        mov es, ax
        mov cx, NFAR
        xor si, si
.chk2:  cmp [es:si + 2], ax
        jne bad
        add si, 4
        loop .chk2
        ; checksum of the blob
        mov si, blob - data
        mov cx, chklen
        xor bx, bx
.sum:   lodsb
        xor ah, ah
        add bx, ax
        rol bx, 1
        loop .sum
        cmp bx, [csum - data]
        jne bad
        mov dx, msgok - data
        mov ah, 9
        int 0x21
        mov ax, 0x4C00
        int 0x21
bad:    mov ax, DATAPARA
        mov ds, ax
        mov dx, msgbad - data
        mov ah, 9
        int 0x21
        mov ax, 0x4C01
        int 0x21
        times DATAPARA * 16 - ($ - img) db 0x90
data:
msgok:  db 'OK', 13, 10, '$'
msgbad: db 'BAD', 13, 10, '$'
csum:   dw CSUM
tbl:
%assign i 0
%rep NREL
        dw (i * 7) & 0xFFFF, DATAPARA
%assign i i+1
%endrep
blob:
%assign i 0
%rep NTEXT
        db 'Line of packer test text number ', '0' + (i % 10), ' with repeats.', 13, 10
%assign i i+1
%endrep
%assign r SEED
%rep NRAND
%assign r (r * 1103515245 + 12345) & 0x7FFFFFFF
        db (r >> 16) & 0xFF
%endrep
blobend:
        times (-($ - img)) & 15 db 0
farseg:
%assign i 0
%rep NFAR
        dw i, farpara
%assign i i+1
%endrep
fend:
farpara equ (farseg - img) / 16
stackpara equ ((fend - img) + 15) / 16
chklen equ ((blobend - blob) > 60000) * 60000 + ((blobend - blob) <= 60000) * (blobend - blob)
