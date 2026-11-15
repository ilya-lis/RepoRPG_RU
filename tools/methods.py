#!/usr/bin/env python3
"""Index of method declarations in ILSpy-style decompiled C#:  line, enclosing type, name, [(type, name), ...] parameters."""
import re, sys, json

DECL = re.compile(r'^(\t+)(?:\[[^\]]+\]\s*)*(?:(?:private|public|internal|protected|static|unsafe|override|virtual|sealed|async|new|extern|abstract)\s+)+'
                  r'([\w<>\[\]?,. ()]+?)\s+([\w.]+)\((.*)\)\s*(?:where .*)?$')
TYPE = re.compile(r'^(\t*)(?:(?:private|public|internal|protected|static|sealed|abstract|partial|readonly|unsafe|new)\s+)*(class|struct|interface|enum)\s+(\w+)')


def split_params(s):
    out, depth, cur = [], 0, ''
    for ch in s:
        if ch in '<([': depth += 1
        elif ch in '>)]': depth -= 1
        if ch == ',' and depth == 0:
            out.append(cur); cur = ''
        else:
            cur += ch
    if cur.strip(): out.append(cur)
    res = []
    for p in out:
        p = p.split('=')[0].strip()
        p = re.sub(r'^(?:this|ref|out|in|params)\s+', '', p)
        m = re.match(r'^(.*?)\s+(\w+)$', p)
        if m: res.append((m.group(1).strip(), m.group(2)))
    return res


def index(path):
    stack, out = [], []
    for i, line in enumerate(open(path, encoding='utf-8'), 1):
        line = line.rstrip('\n').rstrip('\r')
        m = TYPE.match(line)
        if m:
            depth = len(m.group(1))
            while stack and stack[-1][0] >= depth: stack.pop()
            stack.append((depth, m.group(3)))
            continue
        m = DECL.match(line)
        if m:
            depth = len(m.group(1))
            while stack and stack[-1][0] >= depth: stack.pop()
            if m.group(2).strip() in ('return', 'new', 'await', 'throw', 'else'): continue
            out.append({'line': i, 'type': '.'.join(t for _, t in stack), 'name': m.group(3), 'ret': m.group(2).strip(), 'params': split_params(m.group(4))})
    return out


if __name__ == '__main__':
    idx = index(sys.argv[1])
    if len(sys.argv) > 2:
        json.dump(idx, open(sys.argv[2], 'w'))
    print(len(idx), 'methods')
