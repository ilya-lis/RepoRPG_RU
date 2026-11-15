#!/usr/bin/env python3
"""Print method bodies from the decompiled source without the decompiler's //IL_ noise.
   usage: body.py <source> <Name|line> [max_lines] [grep-regex]"""
import re, sys, json, os
sys.path.insert(0, os.path.dirname(__file__))
from methods import index

def body(src, start):
    depth = 0; seen = False; out = []
    for i in range(start - 1, min(start + 3000, len(src))):
        l = src[i]
        if not l.strip().startswith('//IL_'): out.append((i + 1, l))
        for ch in l:
            if ch == '{': depth += 1; seen = True
            elif ch == '}': depth -= 1
        if seen and depth == 0: break
    return out

if __name__ == '__main__':
    path, what = sys.argv[1], sys.argv[2]
    limit = int(sys.argv[3]) if len(sys.argv) > 3 else 60
    rx = re.compile(sys.argv[4]) if len(sys.argv) > 4 else None
    src = open(path, encoding='utf-8').read().split('\n')
    starts = [int(what)] if what.isdigit() else [m['line'] for m in index(path) if m['name'] == what]
    for st in starts:
        lines = body(src, st)
        if rx: lines = [lines[0]] + [x for x in lines[1:] if rx.search(x[1])]
        for n, l in lines[:limit]:
            print(f'{n}: ' + l.replace('\t', ' ').strip()[:230])
        if len(lines) > limit: print(f'   ... {len(lines) - limit} more lines')
        print()
