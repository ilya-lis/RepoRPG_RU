#!/usr/bin/env python3
"""For a range of data units: the names in it and how the dictionary already calls them (in sentences of the mod's code).

    dnames.py <data.json> <po> <file[:from[-to]]>
"""
import json, re, sys


def main():
    units = json.load(open(sys.argv[1], encoding='utf-8'))
    stem, _, rng = sys.argv[3].partition(':')
    lo, hi = 0, 10 ** 9
    if rng:
        a, _, b = rng.partition('-'); lo = int(a); hi = int(b) if b else 10 ** 9
    po = open(sys.argv[2], encoding='utf-8').read().split('\n\n')
    entries = []
    for block in po:
        ctx = re.search(r'^msgctxt "(.*)"$', block, re.M)
        mid = re.search(r'^msgid "(.*)"$', block, re.M); ms = re.search(r'^msgstr "(.*)"$', block, re.M)
        if mid and ms and not (ctx and ctx.group(1) in ('numbers', 'words', 'shape')) and not (ctx and ctx.group(1).startswith('data')):
            entries.append((mid.group(1), ms.group(1)))
    exact = {a: b for a, b in entries}
    seen = set()
    for u in units:
        if u['file'] != stem: continue
        n = int(u['id'].rsplit('.', 1)[1])
        if n < lo or n > hi: continue
        if not any(k in ('name', 'beh', 'slot') for k in u['kinds']): continue
        name = u['text']
        if name in seen: continue
        seen.add(name)
        hits = []
        if name in exact: hits.append('= ' + exact[name])
        rx = re.compile(r'(?<![A-Za-z])' + re.escape(name) + r'(?![A-Za-z])')
        for a, b in entries:
            if a != name and rx.search(a) and len(hits) < 3: hits.append('%s -> %s' % (a[:70], b[:70]))
        if hits: print('%-22s %s' % (name, '   |   '.join(hits)))


if __name__ == '__main__':
    main()
