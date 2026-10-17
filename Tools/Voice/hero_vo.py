"""List a Guildrun hero's voice clips (names + durations) from the game's VO.bank.

usage: python hero_vo.py <HeroToken> [--json out.json]
  HeroToken = the prefix the clips use, usually the internal name (KAI, REYNA, SKORN, ...).
  With no token, prints the clip count per prefix for every hero.
"""
import collections
import json
import os
import struct
import sys

BANK = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', '..', 'Guildrun_Data', 'StreamingAssets', 'VO.bank')
FREQ = {1: 8000, 2: 11000, 3: 11025, 4: 16000, 5: 22050, 6: 24000, 7: 32000, 8: 44100, 9: 48000}


def clips():
    d = open(BANK, 'rb').read()
    o = d.index(b'FSB5')
    ver, num, shdr = struct.unpack('<3I', d[o + 4:o + 16])
    hdr = 60 if ver == 1 else 64
    p, samples = o + hdr, []
    for _ in range(num):
        v, = struct.unpack('<Q', d[p:p + 8]); p += 8
        nxt, freq, ns = v & 1, FREQ.get((v >> 1) & 0xF, 44100), v >> 34
        while nxt:
            c, = struct.unpack('<I', d[p:p + 4]); p += 4
            nxt, size, typ = c & 1, (c >> 1) & 0xFFFFFF, c >> 25
            if typ == 2 and size >= 4: freq = struct.unpack('<I', d[p:p + 4])[0]
            p += size
        samples.append((ns, freq))
    base = o + hdr + shdr
    offs = struct.unpack(f'<{num}I', d[base:base + 4 * num])
    names = [d[base + of:d.index(b'\0', base + of)].decode('latin1') for of in offs]
    return {n: round(ns / f, 2) for n, (ns, f) in zip(names, samples)}


if __name__ == '__main__':
    c = clips()
    if len(sys.argv) < 2:
        for k, v in collections.Counter(n.split('_')[0].upper() for n in c).most_common(): print(f'{v:3}  {k}')
        sys.exit()
    tok = sys.argv[1].upper()
    mine = {k: v for k, v in sorted(c.items()) if k.upper().startswith(tok + '_') or k.upper() == tok}
    for k, v in mine.items(): print(f'{v:5.2f}s  {k}')
    if '--json' in sys.argv: json.dump(mine, open(sys.argv[sys.argv.index('--json') + 1], 'w'), indent=1)
