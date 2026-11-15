#!/usr/bin/env python3
"""limits.py <flow.json> <units.json> <translation dir>

Texts the host sends to the other players are cut by the receiving side to a fixed length
(Net.Cut(value, N) and friends in the mod). An English text was written to fit; its translation may not.
This lists every unit that goes into such a place, with the length of the translation, and marks the ones
that would be cut ("TOO LONG") or come close ("tight"). Placeholders are counted as HOLE characters each.

The table below was read off the receiving code of RepoRPG 1.0.100 (Net.OnEvent / Net.Dispatch).
"""
import glob, json, os, re, sys

HOLE = 8

# callee (last name) -> {argument index: limit}
CALLS = {
    'SendHealNotice':    {3: 40},
    'SendResearchData':  {2: 60},
    'HostGrantAll':      {1: 60},
    'Receive':           {1: 60},
    'SendRivalResult':   {4: 120},
    'SendElementCombo':  {1: 20},
    'SendCurse':         {0: 20},
    'SendUplink':        {2: 40},
    'OnUplinkC':         {3: 40},
    'SendTradeState':    {4: 40, 7: 160},
    'TellTrade':         {4: 40, 7: 160},
    'HostBroadcast':     {0: 300},
    'SendWatchPing':     {1: 40},
    'SendPartGrant':     {3: 40},
    'SendLateJoinAnnounce': {0: 40},
    'StartVote':         {2: 60},
}
# member whose returned texts end up in a limited place -> limit left for the text itself
RETURNS = {
    'SponsorGoal': 170, 'SponsorLine': 200, 'Standing': 120,
}
# units checked by hand: id -> (limit, where)
BY_ID = {}

def load(folder):
    tr = {}
    for f in glob.glob(os.path.join(folder, 'part*.tsv')):
        for line in open(f, encoding='utf-8'):
            m = re.match(r'^(\d+)\t(.*)$', line.rstrip('\n'))
            if m: tr[int(m.group(1))] = m.group(2)
    return tr

def size(text):
    text = text.replace('\\n', '\n').replace('\\"', '"')
    holes = len(re.findall(r'\{\d+[^{}]*\}', text))
    return len(re.sub(r'\{\d+[^{}]*\}', '', text)) + holes * HOLE

def main():
    flow = json.load(open(sys.argv[1], encoding='utf-8'))
    units = json.load(open(sys.argv[2], encoding='utf-8'))
    tr = load(sys.argv[3])
    by_line = {}
    for u in units: by_line.setdefault(u['first'], []).append(u)
    by_text = {}
    for u in units: by_text.setdefault(u['text'], []).append(u)
    chains = flow['chains']
    rows, seen = [], set()
    def unit_of(l):
        if 'chain' in l:
            pat = chains[l['chain']]['pattern']
            for u in by_text.get(pat, []): return u
        for u in by_text.get(l['value'], []): return u
        return None
    for l in flow['literals']:
        s = l.get('sink') or ['', '', '']
        limit, where = None, ''
        if s[0] == 'CALL':
            name = s[1].split('.')[-1]
            if name in CALLS and str(s[2]).isdigit() and int(s[2]) in CALLS[name]:
                if name == 'Receive' and 'Research' not in s[1] and 'Research' not in (l.get('types') or []): continue
                if name == 'HostBroadcast' and 'RunShape' not in (l.get('types') or []) and 'RunShape' not in s[1]: continue
                limit, where = CALLS[name][int(s[2])], '%s(#%s)' % (name, s[2])
        elif s[0] == 'RETURN' and l.get('member') in RETURNS:
            limit, where = RETURNS[l['member']], 'return of ' + l['member']
        if limit is None: continue
        u = unit_of(l)
        if u is None or u['id'] in seen: continue
        seen.add(u['id'])
        rows.append((u, limit, where))
    for i, (limit, where) in BY_ID.items():
        for u in units:
            if u['id'] == i and i not in seen: rows.append((u, limit, where))
    bad = 0
    for u, limit, where in sorted(rows, key=lambda r: r[0]['id']):
        t = tr.get(u['id'])
        en = size(u['text'])
        if t is None:
            print('%5d  %-22s limit %3d  en %3d  (not translated)  %s' % (u['id'], where, limit, en, u['text'][:60]))
            continue
        ru = size(t)
        mark = 'TOO LONG' if ru > limit else 'tight' if ru > limit - 10 else 'ok'
        if mark == 'TOO LONG': bad += 1
        print('%5d  %-22s limit %3d  en %3d  ru %3d  %-8s %s' % (u['id'], where, limit, en, ru, mark, t[:70]))
    print('%d texts go into length-limited messages, %d too long' % (len(rows), bad))

main()
