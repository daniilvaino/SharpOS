#!/usr/bin/env python3
"""qmp_cpu_info.py - dump current CPU registers from running QEMU via QMP."""
import json, socket, sys

def main():
    port = 4444
    s = socket.create_connection(("127.0.0.1", port), timeout=5)
    f = s.makefile("rw")
    f.readline()
    f.write(json.dumps({"execute": "qmp_capabilities"}) + "\n"); f.flush()
    f.readline()
    f.write(json.dumps({
        "execute": "human-monitor-command",
        "arguments": {"command-line": "info registers"}
    }) + "\n"); f.flush()
    resp = json.loads(f.readline())
    print(resp.get("return", "").rstrip())
    s.close()

if __name__ == "__main__":
    main()
