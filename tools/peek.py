#!/usr/bin/env python3
"""Source line(s) of translation units:  peek.py <units.json> <blob> id[,id...] [context-lines]"""
import json, sys, re
U = {u['id']: u for u in json.load(open(sys.argv[1], encoding='utf-8'))}
src = open(sys.argv[2], encoding='utf-8').read().split('\n')
ctx = int(sys.argv[4]) if len(sys.argv) > 4 else 0
for i in sys.argv[3].split(','):
    u = U[int(i)]
    n = u['first']
    print('#%s  line %d  %s  %s' % (i, n, u['scopes'][0], u['uses']))
    for k in range(n - ctx, n + ctx + 1):
        line = src[k - 1].strip()
        if line.startswith('//IL_'): continue
        print('    ' + line[:420])
