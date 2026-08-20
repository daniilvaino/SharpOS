#!/usr/bin/env python3
"""Name an address in a SharpOS app from its PDB.

An app fault prints raw addresses, and every one of them is meaningless until a
name is put to it. The kernel has had a way to do this for a long time (map file
plus image base); apps had none, so this reads the public symbols straight out
of the PDB the linker already writes.

    python tools/symbolize_app.py <app.pdb> <app.exe> 0x10001F555 [more...]

Addresses may be absolute (the image base is read from the PE) or relative.
"""
import struct
import sys


def read_u32(data, offset):
    return struct.unpack_from('<I', data, offset)[0]


def read_u16(data, offset):
    return struct.unpack_from('<H', data, offset)[0]


class Msf:
    """The page-based container a PDB lives in."""

    def __init__(self, data):
        if not data.startswith(b'Microsoft C/C++ MSF 7.00'):
            raise ValueError('not an MSF 7.00 file')

        self.data = data
        # Superblock: block size at 0x20, block count at 0x28, directory size
        # at 0x2C, and the page that lists the directory's pages at 0x34.
        self.page_size = read_u32(data, 32)
        directory_bytes = read_u32(data, 44)
        directory_map_page = read_u32(data, 52)

        pages_for_directory = self._page_count(directory_bytes)
        map_bytes = self._read_pages([directory_map_page],
                                     pages_for_directory * 4)
        directory_pages = list(struct.unpack_from(
            '<%dI' % pages_for_directory, map_bytes, 0))

        self.directory = self._read_pages(directory_pages, directory_bytes)
        self.streams = self._parse_directory()

    def _page_count(self, size):
        return (size + self.page_size - 1) // self.page_size

    def _read_pages(self, pages, size):
        out = bytearray()
        for page in pages:
            start = page * self.page_size
            out += self.data[start:start + self.page_size]
        return bytes(out[:size])

    def _parse_directory(self):
        d = self.directory
        count = read_u32(d, 0)

        sizes = [read_u32(d, 4 + i * 4) for i in range(count)]

        streams = []
        at = 4 + count * 4
        for size in sizes:
            if size == 0xFFFFFFFF:
                size = 0
            pages = self._page_count(size)
            page_list = [read_u32(d, at + i * 4) for i in range(pages)]
            at += pages * 4
            streams.append((page_list, size))
        return streams

    def stream(self, index):
        pages, size = self.streams[index]
        return self._read_pages(pages, size)


def pe_sections(path):
    """Section virtual addresses, and the image base, from the PE itself."""
    data = open(path, 'rb').read()
    pe = read_u32(data, 0x3C)
    if data[pe:pe + 4] != b'PE\0\0':
        raise ValueError('not a PE file')

    sections = read_u16(data, pe + 6)
    optional_size = read_u16(data, pe + 20)
    optional = pe + 24

    magic = read_u16(data, optional)
    image_base = struct.unpack_from('<Q', data, optional + 24)[0] if magic == 0x20B \
        else read_u32(data, optional + 28)

    table = optional + optional_size
    out = []
    for i in range(sections):
        entry = table + i * 40
        name = data[entry:entry + 8].rstrip(b'\0').decode('ascii', 'replace')
        virtual_address = read_u32(data, entry + 12)
        virtual_size = read_u32(data, entry + 8)
        out.append((name, virtual_address, virtual_size))
    return image_base, out


def public_symbols(msf, sections):
    """(rva, name) for every public symbol, sorted by address."""
    dbi = msf.stream(3)
    if len(dbi) < 64:
        return []

    symbol_stream = read_u16(dbi, 20)     # DBI header: symbol record stream
    records = msf.stream(symbol_stream)

    out = []
    at = 0
    while at + 4 <= len(records):
        length = read_u16(records, at)
        if length < 2:
            break
        kind = read_u16(records, at + 2)

        # S_PUB32: flags(4) offset(4) section(2) then a zero-terminated name.
        if kind == 0x110E and at + 4 + 10 <= len(records):
            offset = read_u32(records, at + 8)
            section = read_u16(records, at + 12)
            end = records.index(b'\0', at + 14)
            name = records[at + 14:end].decode('utf-8', 'replace')

            if 1 <= section <= len(sections):
                rva = sections[section - 1][1] + offset
                out.append((rva, name))

        at += length + 2

    out.sort()
    return out


def main(argv):
    if len(argv) < 4:
        print(__doc__)
        return 2

    pdb_path, exe_path = argv[1], argv[2]
    image_base, sections = pe_sections(exe_path)
    symbols = public_symbols(Msf(open(pdb_path, 'rb').read()), sections)

    if not symbols:
        print('no public symbols found in', pdb_path)
        return 1

    print('image base 0x%X, %d public symbols' % (image_base, len(symbols)))

    for text in argv[3:]:
        value = int(text, 0)
        rva = value - image_base if value >= image_base else value

        best = None
        for symbol_rva, name in symbols:
            if symbol_rva <= rva:
                best = (symbol_rva, name)
            else:
                break

        if best is None:
            print('0x%X  rva 0x%X  <before first symbol>' % (value, rva))
        else:
            print('0x%X  rva 0x%X  %s + 0x%X'
                  % (value, rva, best[1], rva - best[0]))

    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
