#!/usr/bin/env python3
"""
qmp_dump_vmem.py - Dump GUEST VIRTUAL memory from a hung QEMU via QMP
human-monitor-command 'x' (which walks the current CPU's page tables).

Use this when you need to inspect kernel-space high VAs like 0x50000818DD58
that are NOT identity-mapped (so pmemsave on the same number won't work).

Usage:
    python tools/qmp_dump_vmem.py 0x50000818DD58 0x80
    python tools/qmp_dump_vmem.py 0x50000818DD58 0x80 --port 4444
"""

import argparse
import json
import socket
import sys
import re


def qmp_exec(sock_file, cmd):
    sock_file.write(json.dumps(cmd) + "\n")
    sock_file.flush()
    return json.loads(sock_file.readline())


def dump(addr, size, port):
    s = socket.create_connection(("127.0.0.1", port), timeout=5)
    f = s.makefile("rw")
    f.readline()  # greeting
    qmp_exec(f, {"execute": "qmp_capabilities"})
    # Use 'xp' if you want physical; here we use 'x' (virtual, current CPU).
    hmc = f"x /{size}bx 0x{addr:x}"
    resp = qmp_exec(f, {
        "execute": "human-monitor-command",
        "arguments": {"command-line": hmc},
    })
    s.close()
    raw = resp.get("return", "")
    # Output lines look like:
    #   00005000085b1480: 0x48 0x8b 0xc1 ...
    print(f"# x /{size}bx 0x{addr:x}")
    print(raw.rstrip())

    # Aggregate raw bytes for hex blob
    bytes_out = []
    for line in raw.splitlines():
        m = re.match(r"\s*[0-9a-fA-F]+:\s+(.*)", line)
        if not m:
            continue
        for tok in m.group(1).split():
            tok = tok.strip().lower()
            if tok.startswith("0x"):
                tok = tok[2:]
            try:
                bytes_out.append(int(tok, 16))
            except ValueError:
                pass
    print(f"# blob ({len(bytes_out)}B): {' '.join(f'{b:02X}' for b in bytes_out)}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("addr", help="virtual address (hex with 0x)")
    ap.add_argument("size", help="bytes to dump (hex/dec)")
    ap.add_argument("--port", type=int, default=4444)
    a = ap.parse_args()
    addr = int(a.addr, 16)
    size = int(a.size, 16) if a.size.startswith("0x") else int(a.size)
    dump(addr, size, a.port)


if __name__ == "__main__":
    main()
