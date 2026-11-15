#!/usr/bin/env python3
"""Compares the mod's data files with what the patcher makes of them (tests/DataCheck.cs).

  datacheck.py <dump dir> <rewritten dir> [rules file]

Both must be valid JSON of the same structure; values may differ only where the rules allow it; no allowed text
may be left without a translation; nothing the mod compares may change. Exit code 1 on any problem.

The patcher has one more reason to leave a text alone, and this check repeats it: the mod recognises some texts by their
wording (StartsWith("Chain"), Contains(" x")), and a translation that would change the answer of such a test is not put
into the data (Builder.BrokenProbe). The tests are read from dump/literals.tsv - the list of every literal of the real
RepoRPG.dll and where it goes; the [probes] section of the rules file says which of them do not count.
"""
import json, os, re, sys

KIND = {'StartsWith': 'prefix', 'EndsWith': 'suffix', 'Contains': 'inside', 'IndexOf': 'inside', 'LastIndexOf': 'inside', 'Replace': 'inside'}


def unescape(s):
    return re.sub(r'\\(.)', lambda m: {'n': '\n', 't': '\t', 'r': '\r'}.get(m.group(1), m.group(1)), s)


def read_probes(dump, rules):
    """the wording tests of the mod that can be about interface text (the same rule as Analysis.ProbeMatters)"""
    sets = {'ignore': set(), 'protect': set(), 'free': set(), 'free-data': set()}
    section = ''
    for line in open(rules, encoding='utf-8'):
        line = line.strip()
        if line.startswith('[') and line.endswith(']'): section = line[1:-1].strip().lower(); continue
        m = re.match(r'^(ignore|protect|free|free-data)\s+"(.*)"\s*$', line) if section == 'probes' else None
        if m: sets[m.group(1)].add(unescape(m.group(2)))
    probes, seen = [], set()
    path = os.path.join(dump, 'literals.tsv')
    if not os.path.isfile(path): return None, sets
    # the list does not say whether a test ignores case; the decompiled listing next to dump/ does
    listing = None
    root = os.path.dirname(os.path.abspath(dump))
    blobs = [f for f in os.listdir(root) if f.endswith('.blob')]
    if len(blobs) == 1: listing = open(os.path.join(root, blobs[0]), encoding='utf-8', errors='replace').read()
    for n, line in enumerate(open(path, encoding='utf-8')):
        cols = line.rstrip('\n').split('\t')
        if n == 0 or len(cols) < 4: continue
        m = re.match(r'^System\.String::(\w+)#0$', cols[2])
        if not m or m.group(1) not in KIND: continue
        text, kind = unescape(cols[3]), KIND[m.group(1)]
        if not text or text in sets['ignore']: continue
        if text not in sets['protect']:
            if len(text) >= 3 and text[0] == '{' and text[-1] == '}': continue
            if not any(c.isascii() and c.isalpha() for c in text): continue
            if kind == 'inside' and all(c.isascii() and c.isalnum() for c in text): continue
        modes = {False, True}                                    # not known: both are tried
        if listing is not None:
            call = m.group(1) + '(' + json.dumps(text, ensure_ascii=False)
            found, at = set(), listing.find(call)
            while at >= 0:
                tail = listing[at + len(call):at + len(call) + 60].split('\n')[0]
                c = re.search(r'StringComparison\.(\w+)', tail)
                # the patcher assumes the loosest comparison whenever the call takes a comparison mode
                found.add((c is not None and 'IgnoreCase' in c.group(1)) or (c is None and 'StringComparison' in tail)
                          or re.match(r'\s*,\s*(true|false)[,)]', tail) is not None)
                at = listing.find(call, at + 1)
            if found: modes = found
        for nocase in sorted(modes):
            if (text, kind, nocase) in seen: continue
            seen.add((text, kind, nocase))
            probes.append((text, kind, nocase, '%s("%s") in %s' % (m.group(1), text, cols[0])))
    return probes, sets


def hits(text, kind, s, nocase):
    if nocase: text, s = text.lower(), s.lower()
    return s.startswith(text) if kind == 'prefix' else s.endswith(text) if kind == 'suffix' else text in s


def broken(probes, en, ru):
    """the test whose answer the translation would change"""
    for text, kind, nocase, where in probes:
        if hits(text, kind, en, nocase) != hits(text, kind, ru, nocase): return where
    return None


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
    rules = sys.argv[3] if len(sys.argv) > 3 else os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'mod', 'RepoRPG_RU.rules')
    probes, sets = read_probes(dump, rules)
    bad, changed, blocked = [], 0, 0
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
            if probes and stem not in sets['free-data'] and en not in sets['free']:
                where = broken(probes, en, ru)
                if where:
                    blocked += 1
                    bad.append('%s: %s: the patcher will leave this text in English - the mod tests texts with %s, and the '
                               'translation changes the answer (%r)' % (stem, path, where, en[:60]))
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
    print('data files: %d values changed, %d allowed texts left as they were, %d stopped by a wording test (%s), %d problems'
          % (changed, left, blocked, 'no dump/literals.tsv: not checked' if probes is None else '%d tests' % len(probes), len(bad)))
    return 1 if bad or left else 0

if __name__ == '__main__':
    sys.exit(main())
