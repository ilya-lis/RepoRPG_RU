#!/usr/bin/env python3
"""Compares the mod's data files with what the patcher makes of them (tests/DataCheck.cs).

  datacheck.py <dump dir> <rewritten dir>

Both must be valid JSON of the same structure; values may differ only where the rules allow it; no allowed text
may be left without a translation; nothing the mod compares may change. Exit code 1 on any problem.
"""
import json, os, re, sys

def walk(a, b, path, out, bad):
    if isinstance(a, dict):
        if not isinstance(b, dict) or list(a.keys()) != list(b.keys()):
            bad.append('%s: the object changed its shape' % path); return
        for k in a: walk(a[k], b[k], path + [k], out, bad)
    elif isinstance(a, list):
        if not isinstance(b, list) or len(a) != len(b):
            bad.append('%s: the array changed its length' % path); return
        for x, y in zip(a, b): walk(x, y, path, out, bad)
    elif a != b:
        if not isinstance(a, str) or not isinstance(b, str): bad.append('%s: a value that is not a text changed' % '/'.join(path))
        else: out.append(('/'.join(path), a, b))

TOKEN = re.compile(r'\{[a-z]+:[^{}]*\}')

def main():
    dump, new = sys.argv[1], sys.argv[2]
    bad, changed = [], 0
    for name in sorted(os.listdir(dump)):
        if not name.endswith('.json'): continue
        stem = name[:-5].split('.')[-1]
        raw_a, raw_b = open(os.path.join(dump, name), 'rb').read(), open(os.path.join(new, name), 'rb').read()
        if raw_a.startswith(b'\xef\xbb\xbf') != raw_b.startswith(b'\xef\xbb\xbf'): bad.append('%s: the byte order mark changed' % stem)
        try:
            a = json.loads(raw_a.decode('utf-8-sig')); b = json.loads(raw_b.decode('utf-8-sig'))
        except ValueError as e:
            bad.append('%s: not valid JSON any more: %s' % (stem, e)); continue
        diff = []
        walk(a, b, [], diff, bad)
        changed += len(diff)
        for path, en, ru in diff:
            if stem == 'contracts':
                if path != 'givers/name': bad.append('contracts: %s changed (%r)' % (path, en))
            elif stem == 'guide':
                last = path.split('/')[-1]
                if last in ('id', 'type', 'tab', 'topic', 'key', 'name', 'icon'): bad.append('guide: %s is a key, it changed (%r)' % (path, en))
                heads = lambda s: sorted(t if '?' not in t else t[:t.index('?')] for t in TOKEN.findall(s))
                if heads(en) != heads(ru): bad.append('guide: %s: tokens differ (%r)' % (path, en[:50]))
                for t in TOKEN.findall(ru):
                    if '?' in t and t[t.index('?'):].count(':') != 1: bad.append('guide: %s: a yes/no token needs one colon (%r)' % (path, t[:50]))
                if last == 'keywords' and not ru.startswith(en + ' '): bad.append('guide: %s: the English keywords are gone' % path)
            else: bad.append('%s: %s changed, no rule allows that' % (stem, path))
        if raw_a == raw_b and diff: bad.append('%s: internal error' % stem)
    left = 0
    for line in open(os.path.join(new, 'changes.tsv'), encoding='utf-8'):
        stem, path, mark, value = line.rstrip('\n').split('\t', 3)
        if mark == '?' and re.search('[A-Za-z]{2}', TOKEN.sub('', value)) and not (stem == 'contracts'):
            left += 1
            if left <= 20: print('  LEFT %s %s: %s' % (stem, path, value[:90]))
    for p in bad[:40]: print('  PROBLEM ' + p)
    print('data files: %d values changed, %d allowed texts left as they were, %d problems' % (changed, left, len(bad)))
    return 1 if bad or left else 0

if __name__ == '__main__':
    sys.exit(main())
