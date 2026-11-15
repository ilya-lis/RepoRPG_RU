#!/usr/bin/env python3
"""Adds a translator's draft to translation/ru/data/<file>.tsv.

    dtsv.py <data.json> <tsv dir> <file> <draft.txt>

The draft has one text per line: the id (the number tools/data.py list prints), a space, the translation.
Lines that are empty or start with # are skipped. An id that is already in the tsv is replaced.
"""
import json, sys, os, re


def main():
    units = {u['id']: u for u in json.load(open(sys.argv[1], encoding='utf-8'))}
    stem = sys.argv[3]
    path = os.path.join(sys.argv[2], stem + '.tsv')
    have = []
    if os.path.isfile(path):
        have = open(path, encoding='utf-8').read().split('\n')
        while have and not have[-1]: have.pop()
    index = {l.split('\t', 1)[0]: i for i, l in enumerate(have) if '\t' in l and not l.startswith('#')}
    added = replaced = 0
    for n, line in enumerate(open(sys.argv[4], encoding='utf-8'), 1):
        line = line.rstrip('\n')
        if not line.strip() or line.startswith('#'): continue
        m = re.match(r'^(\d{4}) (.*)$', line)
        if not m: sys.exit('line %d: expected "NNNN text": %r' % (n, line[:60]))
        key, text = m.group(1), m.group(2)
        if stem + '.' + key not in units: sys.exit('line %d: no unit %s.%s' % (n, stem, key))
        row = key + '\t' + text
        if key in index: have[index[key]] = row; replaced += 1
        else: index[key] = len(have); have.append(row); added += 1
    os.makedirs(sys.argv[2], exist_ok=True)
    open(path, 'w', encoding='utf-8', newline='\n').write('\n'.join(have) + '\n')
    print('%s: %d added, %d replaced, %d lines' % (path, added, replaced, len(index)))


if __name__ == '__main__':
    main()
