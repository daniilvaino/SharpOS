#!/usr/bin/env python3
"""
Analyze a SharpOS GC heap segment dump and locate objects whose
m_pMT field has been overwritten by the UTF-16 "Rtor" pattern that
crashes MethodTable::SanityCheck.

Dump layout assumption: file offset 0 corresponds to runtime VA
SEGMENT_BASE_VA (the segment 'Start' field from the [GC-SEG] line
of the kernel fault dump).
"""

import struct
import sys
from pathlib import Path

DUMP_PATH = Path(r"C:\work\OS\heap_dump.bin")
SEGMENT_BASE_VA = 0x3780030
TARGET_PATTERN = 0x0072006F00740052   # UTF-16 "Rtor" LE

KERNEL_IMAGE_BASE = 0x1BD48000
KERNEL_IMAGE_SIZE = 0x10000000

def looks_like_mt_pointer(v: int) -> bool:
    return KERNEL_IMAGE_BASE <= v < KERNEL_IMAGE_BASE + KERNEL_IMAGE_SIZE

def fmt_qword(v: int) -> str:
    return f"0x{v:016X}"

def fmt_ascii(v: int) -> str:
    """Render qword bytes as ASCII, replacing non-printables with '.'."""
    out = []
    for shift in range(0, 64, 8):
        b = (v >> shift) & 0xFF
        out.append(chr(b) if 32 <= b < 127 else '.')
    return ''.join(out)

def fmt_utf16(v: int) -> str:
    """Render qword bytes as 4 UTF-16 LE chars, '.' for non-printable."""
    out = []
    for shift in range(0, 64, 16):
        c = (v >> shift) & 0xFFFF
        out.append(chr(c) if 32 <= c < 127 else '.')
    return ''.join(out)

def main():
    data = DUMP_PATH.read_bytes()
    print(f"Dump: {DUMP_PATH} ({len(data):,} bytes)")
    print(f"VA range: 0x{SEGMENT_BASE_VA:X} .. 0x{SEGMENT_BASE_VA + len(data):X}")
    print(f"Searching for qword pattern: {fmt_qword(TARGET_PATTERN)}  (\"Rtor\" UTF-16 LE)")
    print()

    # Pass 1: find all 8-aligned positions where qword == TARGET_PATTERN
    hits = []
    target_bytes = struct.pack('<Q', TARGET_PATTERN)
    pos = 0
    while True:
        i = data.find(target_bytes, pos)
        if i < 0:
            break
        if i % 8 == 0:
            hits.append(i)
        pos = i + 1
    print(f"Found {len(hits)} qword-aligned occurrences of \"Rtor\" pattern.")
    print()

    # Pass 2: for each hit, dump 8 qwords before + 16 qwords after.
    for hit_idx, file_off in enumerate(hits):
        va = SEGMENT_BASE_VA + file_off
        print("=" * 78)
        print(f"HIT #{hit_idx}: file offset 0x{file_off:08X}  VA 0x{va:X}")

        # Print context: 8 qwords before .. 16 qwords after
        ctx_start = max(0, file_off - 8 * 8)
        ctx_end = min(len(data), file_off + 17 * 8)
        for off in range(ctx_start, ctx_end, 8):
            qw = struct.unpack_from('<Q', data, off)[0]
            marker = '   '
            if off == file_off:
                marker = '>>>'
            elif off == file_off + 8:
                marker = ' L+'        # length field if hit is at object[0]
            mt_hint = ' [MT?]' if looks_like_mt_pointer(qw) else ''
            print(f"  {marker} +{off-file_off:+04X}  0x{SEGMENT_BASE_VA + off:010X}  "
                  f"{fmt_qword(qw)}  {fmt_ascii(qw)}  utf16=\"{fmt_utf16(qw)}\"{mt_hint}")

        # Heuristic: if hit is at offset 0 of an object, find the
        # preceding "real" MT to guess object class. Walk backward for
        # the nearest qword that looks_like_mt_pointer -- that would be
        # the m_pMT of the *previous* object in the heap.
        prev_mt_off = None
        for back in range(file_off - 8, max(0, file_off - 1024), -8):
            qw = struct.unpack_from('<Q', data, back)[0]
            if looks_like_mt_pointer(qw):
                prev_mt_off = back
                break
        if prev_mt_off is not None:
            prev_mt = struct.unpack_from('<Q', data, prev_mt_off)[0]
            gap = file_off - prev_mt_off
            print(f"  >> nearest preceding MT-like pointer: file off 0x{prev_mt_off:X} "
                  f"VA 0x{SEGMENT_BASE_VA + prev_mt_off:X} -> MT 0x{prev_mt:X}  "
                  f"(gap = {gap} bytes -- preceding object size, including header)")

        # Also check trailing 4 qwords past header to see possible string body / array content.
        print()

    # Pass 3: aggregate of unique values seen at file_off+8 (length/second qword)
    if hits:
        print()
        print(f"Aggregated second-qword (file_off+8) values across {len(hits)} hits:")
        from collections import Counter
        secondQws = Counter()
        for h in hits:
            if h + 8 + 8 <= len(data):
                secondQws[struct.unpack_from('<Q', data, h + 8)[0]] += 1
        for v, c in secondQws.most_common(10):
            print(f"  count={c}  qword={fmt_qword(v)}  utf16=\"{fmt_utf16(v)}\"")

if __name__ == '__main__':
    main()
