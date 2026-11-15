#!/usr/bin/env python3
"""tsv.py <units.json> <draft.txt> <out.tsv>: lines "id<space>text" -> "id<TAB>text", edge spaces copied from the original."""
import json, re, sys

def main():
    units = {x['id']: x for x in json.load(open(sys.argv[1], encoding='utf-8'))}
    out, seen = [], set()
    for n, line in enumerate(open(sys.argv[2], encoding='utf-8').read().split('\n'), 1):
        if not line.strip() or line.startswith('#'): continue
        m = re.match(r'^(\d+) (.*)$', line, re.S)
        if not m: sys.exit('line %d: no id' % n)
        i, t = int(m.group(1)), m.group(2)
        if i not in units: sys.exit('line %d: unknown id %d' % (n, i))
        if i in seen: sys.exit('line %d: id %d twice' % (n, i))
        seen.add(i)
        src = units[i]['text']
        lead, trail = len(src) - len(src.lstrip(' ')), len(src) - len(src.rstrip(' '))
        out.append('%d\t%s' % (i, ' ' * lead + t.strip(' ') + ' ' * trail))
    open(sys.argv[3], 'w', encoding='utf-8').write('\n'.join(out) + '\n')
    print('%d lines' % len(out))

main()
