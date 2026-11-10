#!/usr/bin/env python3
"""bench.py <units.json> <translation dir> <out.txt>

Texts of every unit that has no translation yet, placeholders filled in ("{0}" -> "3"), one per line.
The display-time translator is run over them (tests/langcheck.sh @file): none of them has an entry of its own,
so whatever changes shows what the fallbacks do to texts they were not written for.
"""
import glob, json, os, re, sys

def main():
    units = json.load(open(sys.argv[1], encoding='utf-8'))
    done = set()
    for f in glob.glob(os.path.join(sys.argv[2], 'part*.tsv')):
        for line in open(f, encoding='utf-8'):
            m = re.match(r'^(\d+)\t', line)
            if m: done.add(int(m.group(1)))
    out = []
    for u in units:
        if u['id'] in done: continue
        t = re.sub(r'\{\d+[^{}]*\}', '3', u['text']).replace('{{', '{').replace('}}', '}')
        out.append(t.replace('\\', '\\\\').replace('\n', '\\n').replace('\r', '\\r'))
    open(sys.argv[3], 'w', encoding='utf-8').write('\n'.join(out) + '\n')
    print('%d texts' % len(out))

main()
