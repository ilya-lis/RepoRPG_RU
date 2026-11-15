#!/usr/bin/env python3
"""probes.py <flow.json> <source.cs> <units.json> <translation dir> [rules file]

The mod recognises some of its own texts by their wording: why.StartsWith("Load too big"), reason.EndsWith(" kill"),
text.Replace("pick waiting", ...). The patcher never replaces a text inside the code when that would change the answer of
such a test - the text is then translated only at the moment it is drawn. This lists those texts in advance (the same
rule as Analysis.ProbeMatters / Builder.BrokenProbe, applied to the decompiled source), so that the translator can
check that each of them really is recognised when drawn, or reword the translation so that the test still passes.

It over-reports a little: here a Contains() on a list of names cannot be told from a Contains() on a string.
"""
import glob, json, os, re, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import mkpo

KIND = {'StartsWith': 'prefix', 'EndsWith': 'suffix', 'Contains': 'inside', 'IndexOf': 'inside', 'LastIndexOf': 'inside', 'Replace': 'inside'}
IGNORE, PROTECT, FREE = set(), set(), set()                                     # the [probes] section of the rules file


def read_rules(path):
    section = ''
    for line in open(path, encoding='utf-8'):
        line = line.strip()
        if line.startswith('[') and line.endswith(']'): section = line[1:-1].strip().lower(); continue
        m = re.match(r'^(ignore|protect|free)\s+"(.*)"\s*$', line) if section == 'probes' else None
        if m: {'ignore': IGNORE, 'protect': PROTECT, 'free': FREE}[m.group(1)].add(mkpo.unescape(m.group(2)))


def matters(text, kind):
    if text in IGNORE: return False
    if text in PROTECT: return True
    if len(text) >= 3 and text[0] == '{' and text[-1] == '}': return False
    letter = any(c.isascii() and c.isalpha() for c in text)
    plain = all(c.isascii() and c.isalnum() for c in text)
    if not letter: return False
    if kind == 'inside' and plain: return False
    return True


def hits(p, s):
    text, kind, nocase = p
    if len(s) < len(text): return False
    a, b = (s.lower(), text.lower()) if nocase else (s, text)
    return a.startswith(b) if kind == 'prefix' else a.endswith(b) if kind == 'suffix' else b in a


def main():
    read_rules(sys.argv[5] if len(sys.argv) > 5 else os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'mod', 'RepoRPG_RU.rules'))
    flow = json.load(open(sys.argv[1], encoding='utf-8'))
    src = open(sys.argv[2], encoding='utf-8', errors='replace').read().split('\n')
    units = json.load(open(sys.argv[3], encoding='utf-8'))
    probes, seen = [], set()
    for l in flow['literals']:
        s = l.get('sink') or ['', '', '']
        if s[0] != 'CALL' or str(s[2]) != '0': continue
        name = s[1].split('.')[-1]
        if name not in KIND or not l['value']: continue
        line = src[l['line'] - 1] if 0 < l['line'] <= len(src) else ''
        # the patcher assumes the loosest comparison whenever the call takes a comparison mode
        at = line.find(name + '(' + json.dumps(l['value'], ensure_ascii=False))
        tail = line[at:at + len(name) + len(l['value']) + 60] if at >= 0 else line
        m = re.search(r'StringComparison\.(\w+)', tail)
        nocase = (m is not None and 'IgnoreCase' in m.group(1)) or (m is None and 'StringComparison' in tail) \
                 or re.search(r'",\s*(true|false)[,)]', tail) is not None
        key = (l['value'], KIND[name], nocase)
        if key in seen or not matters(l['value'], KIND[name]): continue
        seen.add(key)
        probes.append((key, '%s::%s line %d' % ('/'.join(l.get('types') or ['?']), l.get('member'), l['line'])))
    tr = {}
    for f in sorted(glob.glob(os.path.join(sys.argv[4], 'part*.tsv'))):
        for line in open(f, encoding='utf-8'):
            m = re.match(r'^(\d+)\t(.*)$', line.rstrip('\n'))
            if m: tr[int(m.group(1))] = mkpo.unescape(m.group(2))
    n = 0
    for u in units:
        t = tr.get(u['id'])
        if t is None: continue
        t = mkpo.edges(u['text'], t)
        pairs = []
        if u['kind'] == 'lit': pairs.append((u['text'], t))
        else:
            rs, hs = mkpo.split_runs(u['text']); rt, ht = mkpo.split_runs(t)
            if hs != ht or len(rs) != len(rt): continue          # drawn-time only anyway
            pairs += [(a, b) for a, b in zip(rs, rt) if a != b and a]
        for a, b in pairs:
            if a in FREE: continue
            for p, where in probes:
                if hits(p, a) != hits(p, b):
                    n += 1
                    print('%5d  %-10s %-22r %s\n         %r\n      -> %r' % (u['id'], p[1] + ('/i' if p[2] else ''), p[0], where, a[:110], b[:110]))
                    break
    print('%d probes that matter; %d texts stay as they are inside the code because of them' % (len(probes), n))

main()
