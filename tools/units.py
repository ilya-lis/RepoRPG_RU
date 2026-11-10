#!/usr/bin/env python3
"""Translator's work list.

A unit is what gets translated in one go:
    lit    one string literal of the mod (possibly a format string)
    chain  a sentence the mod glues together:  "Slotted " + a + " into " + b   ->   Slotted {0} into {1}

usage: units.py <flow.json> <decompiled.cs> <units.json>
"""
import json, re, sys, collections

TAG = re.compile(r'</?(?:color|b|i|size|u|s|mark|sup|sub)(?:=[^>]*)?>', re.I)
HOLE = re.compile(r'\{[^{}]*\}')
ORDER = {'NONE': 0, 'KEY': 1, 'LOG': 2, 'UNKNOWN': 3, 'DISPLAY': 4}

DEBUG_TYPES = {'GameCheck', 'NetCodeCheck', 'LoopTrace', 'HudTrace', 'Tuning', 'Coll', 'PatchGuard', 'Guard', 'Compat', 'TransitionStart',
               'LateJoinHost', 'Perf', 'FrameCost', 'Json', 'NetMeter', 'VanillaStyle', 'FontProbe'}
DEBUG_MEMBER = re.compile(r'Debug|Probe|Diag|Trace|Report|Dump|Perf|Bench|SelfTest|Audit')


def core(text):
    return HOLE.sub(' ', TAG.sub(' ', text))


def shape(text):
    c = core(text).strip()
    words = re.findall(r'[A-Za-z]+', c)
    if not words or max(len(w) for w in words) < 2: return 'none'
    if re.fullmatch(r'#?[0-9a-fA-F]{6,8}', text.strip()): return 'none'
    if re.search(r'\s', c): return 'text'
    if re.fullmatch(r'[A-Z][a-z]+[.:!?…]*', c): return 'word'
    if re.fullmatch(r'[A-Z0-9][A-Z0-9\-+/&%.:!?]*', c) and len(c) >= 2: return 'caps'
    if re.fullmatch(r'[(\-+%]*[a-z]+[.:!?,;)]*', c): return 'lower'
    if re.fullmatch(r'[a-z0-9_.:|/\-]+', c): return 'id'
    if re.fullmatch(r'[A-Za-z0-9_]+(/[A-Za-z0-9_]+)*', c): return 'ident'
    return 'text'


def main():
    flow = json.load(open(sys.argv[1], encoding='utf-8'))
    src = open(sys.argv[2], encoding='utf-8').read().split('\n')
    chains = flow['chains']
    units = collections.OrderedDict()
    # literals standing on their own, per method: a piece of a sentence that is spelled like one of them
    # cannot be replaced inside that method unless that one is plain display text too
    alone = collections.defaultdict(set)
    for l in flow['literals']:
        if 'chain' not in l: alone[('.'.join(l['types']) + '::' + (l['member'] or '?'), l['value'])].add(l['use'])
    for l in flow['literals']:
        scope = '.'.join(l['types']) + '::' + (l['member'] or '?')
        if 'chain' in l:
            ch = chains[l['chain']]
            key = ('chain', ch['pattern'])
            u = units.setdefault(key, {'kind': 'chain', 'text': ch['pattern'], 'pieces': ch['pieces'], 'occ': {}, 'lits': []})
            o = u['occ'].setdefault((scope, l['chain']), {'scope': scope, 'line': ch['line'], 'use': 'NONE', 'sink': l['sink'], 'whole': False})
            if ORDER[l['use']] > ORDER[o['use']]: o['use'] = l['use']
        else:
            key = ('lit', l['value'])
            u = units.setdefault(key, {'kind': 'lit', 'text': l['value'], 'occ': {}, 'interp': False})
            u['occ'][(scope, l['line'], len(u['occ']))] = {'scope': scope, 'line': l['line'], 'use': l['use'], 'sink': l['sink'], 'whole': l['whole'],
                                                         'inside': l.get('inside')}
            if l.get('interp'): u['interp'] = True

    out = []
    for (kind, text), u in units.items():
        occ = list(u['occ'].values())
        sh = shape(text)
        if sh == 'none': continue
        shown = [o for o in occ if o['use'] in ('DISPLAY', 'UNKNOWN')]
        if not shown: continue
        has_display = any(o['use'] == 'DISPLAY' for o in occ)
        cfg = [o for o in shown if o['sink'][0] == 'CALL' and (o['sink'][1].endswith('Bind') or o['sink'][1] == 'new ConfigDescription')]
        if not has_display:
            if sh in ('id', 'ident'): continue
            if kind == 'lit' and sh == 'lower' and all(o['whole'] and o['sink'][0] in ('COLL', 'MEMBER', 'CALL', 'ASSIGN') for o in shown): continue
        tnames = {o['scope'].split('::')[0].split('.')[-1] for o in shown}
        debug = all(t in DEBUG_TYPES for t in tnames) or all(DEBUG_MEMBER.search(o['scope'].split('::')[1]) for o in shown)
        if cfg and len(cfg) == len(shown): group = 'config'
        elif debug: group = 'debug'
        elif has_display: group = 'display'
        else: group = 'maybe'
        # short names parked in tables and fields: translated when drawn, never inside the code
        stored = kind == 'lit' and not has_display and all(o['whole'] and o['sink'][0] in ('COLL', 'MEMBER', 'ASSIGN', 'RETURN', 'CALL') for o in shown)
        words = len(re.findall(r"[A-Za-z']+", core(text)))
        # ...but a piece of rich text or a piece with spaces around it never shows up alone: it has to be replaced in the code
        term = (stored and words <= 4 and not re.search(r'[.!?:]\s*$', text.strip()) and '{' not in text
                and text == text.strip() and '<' not in text and '\n' not in text)
        first = min(o['line'] for o in shown)
        rec = {'kind': kind, 'text': text, 'shape': sh, 'group': group, 'first': first, 'term': term,
               'scopes': sorted({o['scope'] for o in shown}),
               'uses': dict(collections.Counter(o['use'] for o in occ)),
               'code': src[first - 1].strip()[:260]}
        if kind == 'chain':
            rec['pieces'] = u['pieces']
            bad = sorted({sc for sc in rec['scopes'] for p in u['pieces']
                          if p and re.search('[A-Za-z]', p) and (alone.get((sc, p), set()) - {'DISPLAY'})})
            if bad: rec['unsafe'] = bad
        ins = sorted({o['inside'] for o in shown if o.get('inside') is not None})
        if ins: rec['inside'] = [chains[i]['pattern'] for i in ins][:3]
        out.append(rec)
    out.sort(key=lambda r: (r['first'], r['kind'] == 'lit'))
    for i, r in enumerate(out): r['id'] = i + 1
    json.dump(out, open(sys.argv[3], 'w', encoding='utf-8'), ensure_ascii=False, indent=0)
    g = collections.Counter(); chars = collections.Counter()
    for r in out:
        g[(r['group'], r['kind'])] += 1; chars[r['group']] += len(r['text'])
    print(len(out), 'units')
    for k in sorted(g): print(f'  {k[0]:8s} {k[1]:6s} {g[k]:5d}')
    print(dict(chars), 'terms', sum(r['term'] for r in out))


if __name__ == '__main__':
    main()
