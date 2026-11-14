#!/usr/bin/env python3
"""The mod's built-in data (dump/RepoRPG.*.json, written by the add-on with DumpStrings = true): what there is to translate.

    data.py units   <dump dir> <out data.json>                 every text of the data files with a stable id
    data.py list    <data.json> <file[:from[-to]]> [--done <tsv dir>] [--all]
    data.py paths   <dump dir>                                  every path with a text-like value and what is done with it

An id is "<file>.<n>": the n-th string value of that file, counted over ALL its string values - it does not move when the
table below changes. A text that occurs several times is one unit (the first occurrence); "also" lists the other places.

How a text is used (KIND) decides how it is translated:
    name        a name: shown as it is, in capitals, inside the mod's own sentences               -> term
    text        a description: its numbers can change (ranks, settings)                             -> term + shape
    class       class description: the class list shows its first clause, cut at 44 characters      -> + that summary
    passive     passive description: "Weakness: ..." is cut out of it and shown separately           -> + both halves
    node        talent description: "CAPSTONE: " is taken off before it is shown                     -> + without it
    contract    "{t}" = the target, "{s}" = "s" when the target is not 1                              -> shapes
    synergy     "{v}", "{v2}" = values
    pre/beh/base/post   words gear names are made of ("Sharp" + "Striker Module" + " of the " + "Brawler")
    first/epithet       boss names ("Grindle" + " the " + "Unbroken")
    guide       the Guide: replaced inside the data itself (the mod fills in {key:menu}, {cfg:...} and searches it)
    keywords    search words of a Guide topic: the Russian ones are added to the English ones
Everything else (ids, stats, colours, keys of settings) is never touched.
"""
import json, re, sys, os, glob, collections

KIND = {
    'classes': {
        'classes/name': 'name', 'classes/role': 'name', 'classes/group': 'name',
        'classes/complexityTip': 'text', 'classes/desc': 'class',
        'classes/passives/name': 'name', 'classes/passives/desc': 'passive',
        'classes/active/name': 'name', 'classes/active/desc': 'text',
        'classes/branches/name': 'name', 'classes/branches/desc': 'text',
        'classes/branches/nodes/name': 'name', 'classes/branches/nodes/desc': 'node',
    },
    'rpg': {
        'attributes/name': 'name', 'attributes/desc': 'text', 'attributes/category': 'name',
        'rarities/name': 'name', 'slots/name': 'name', 'slots/desc': 'text', 'slots/bases': 'base',
        'uniques/name': 'name', 'uniques/desc': 'text',
        'perks/name': 'name', 'perks/desc': 'text', 'upgrades/name': 'name',
        'blessings/name': 'name', 'blessings/desc': 'text', 'curses/name': 'name', 'curses/desc': 'text',
        'threatTiers/name': 'name',
    },
    'synergies': {'synergies/name': 'name', 'synergies/desc': 'synergy'},
    'guide': {
        'hostSettings/label': 'guide', 'walkthrough/title': 'guide', 'walkthrough/text': 'guide',
        'topics/title': 'guide', 'topics/summary': 'guide', 'topics/keywords': 'keywords',
        'topics/blocks/items': 'guide', 'topics/blocks/label': 'guide', 'topics/blocks/text': 'guide',
        'topics/blocks/title': 'guide', 'topics/blocks/terms/term': 'guide', 'topics/blocks/terms/meaning': 'guide',
    },
    'bosses': {'first': 'first', 'epithet': 'epithet'},
    'season': {'chapters/name': 'name', 'chapters/flavor': 'text', 'endless/name': 'name', 'endless/flavor': 'text'},
    'routes': {'risks/name': 'name', 'risks/risk': 'text', 'rewards/name': 'name', 'rewards/desc': 'text'},
    'heat': {'cards/name': 'name', 'cards/desc': 'text'},
    'sets': {'sets/name': 'name', 'sets/twoDesc': 'text', 'sets/fourDesc': 'text'},
    'perks': {
        'meta/evolveHow': 'text', 'meta/evolveName': 'name', 'meta/evolveDesc': 'text',
        'perks/name': 'name', 'perks/desc': 'text', 'tags/tag': 'name', 'tags/threeDesc': 'text', 'tags/fiveDesc': 'text',
    },
    'attrbonus': {'milestones/name': 'name', 'milestones/desc': 'text', 'pairings/name': 'name', 'pairings/desc': 'text'},
    'gear': {
        'behaviors/name': 'beh', 'behaviors/desc': 'text', 'prefixes/word': 'pre', 'suffixes/word': 'post',
        'uniques/name': 'name', 'uniques/desc': 'text', 'uniques/flavor': 'text',
        'modules/name': 'name', 'attunement/name': 'name', 'attunement/desc': 'text',
        'cursed/name': 'name', 'cursed/benefit': 'text', 'cursed/curse': 'text', 'cursed/flavor': 'text',
        'starters/name': 'name', 'starters/desc': 'text', 'starters/toolName': 'name',
    },
    'progression': {'unlocks/name': 'name', 'unlocks/title': 'name', 'unlocks/gives': 'text', 'unlocks/how': 'text'},
    'altpassives': {'alts/replaces': 'name', 'alts/name': 'name', 'alts/desc': 'text'},
    'prestige': {'trees/name': 'name', 'trees/desc': 'text', 'trees/nodes/name': 'name', 'trees/nodes/desc': 'node'},
    'teamcontracts': {'contracts/name': 'name', 'contracts/goal': 'contract', 'curses/id': 'name', 'curses/desc': 'text'},
    'contracts': {
        'givers/name': 'name', 'givers/title': 'name', 'givers/theme': 'text',
        'givers/offer': 'text', 'givers/accept': 'text', 'givers/done': 'text', 'givers/fail': 'text',
        'contracts/name': 'name', 'contracts/desc': 'contract', 'contracts/bonusDesc': 'text',
        'chains/name': 'name', 'chains/rewardDesc': 'text', 'chains/steps/desc': 'contract', 'chains/steps/line': 'text',
    },
    'milestones': {'boons/name': 'name', 'boons/desc': 'text'},
    'unlocks': {'unlocks/name': 'name', 'unlocks/hint': 'text', 'modifiers/name': 'name', 'modifiers/risk': 'text', 'modifiers/reward': 'text'},
    'market': {'categories/name': 'name'},
    'research': {'branches/name': 'name', 'projects/name': 'name', 'projects/unlock': 'text'},
}

# the order the files are worked through (and listed in)
ORDER = ['rpg', 'classes', 'altpassives', 'prestige', 'perks', 'attrbonus', 'gear', 'sets', 'synergies', 'contracts', 'teamcontracts',
         'milestones', 'heat', 'routes', 'season', 'unlocks', 'progression', 'research', 'market', 'bosses', 'guide', 'brawl']


def walk(doc):
    """every string value of a JSON document with the path of property names leading to it (arrays keep the name of their property);
    the same walk as JsonText.Translate of the patcher"""
    out = []
    def go(v, path):
        if isinstance(v, dict):
            for k, x in v.items(): go(x, path + [k])
        elif isinstance(v, list):
            for x in v: go(x, path)
        elif isinstance(v, str):
            if v != '' and path: out.append(('/'.join(path), v))
    go(doc, [])
    return out


def load(dump):
    files = collections.OrderedDict()
    for f in sorted(glob.glob(os.path.join(dump, 'RepoRPG.*.json'))):
        stem = os.path.basename(f)[len('RepoRPG.'):-len('.json')]
        files[stem] = walk(json.load(open(f, encoding='utf-8-sig'), object_pairs_hook=collections.OrderedDict))
    return files


def units(dump):
    files = load(dump)
    out, seen = [], {}
    for stem in ORDER + [s for s in files if s not in ORDER]:
        kinds = KIND.get(stem, {})
        for n, (path, text) in enumerate(files.get(stem, ())):
            kind = kinds.get(path)
            if not kind: continue
            # the mod takes "CAPSTONE: " off a talent's description before it shows it anywhere
            if kind == 'node' and text.startswith('CAPSTONE: '): text = text[len('CAPSTONE: '):]
            guide = kind in ('guide', 'keywords')
            key = (guide, text)
            if key in seen:
                u = seen[key]
                if kind not in u['kinds']: u['kinds'].append(kind)
                place = '%s:%s' % (stem, path)
                if place not in u['also'] and place != '%s:%s' % (u['file'], u['path']): u['also'].append(place)
                u['count'] += 1
                continue
            u = collections.OrderedDict(id='%s.%04d' % (stem, n), file=stem, path=path, kinds=[kind], text=text, count=1, also=[])
            seen[key] = u
            out.append(u)
    return out


def main():
    cmd = sys.argv[1]
    if cmd == 'units':
        us = units(sys.argv[2])
        json.dump(us, open(sys.argv[3], 'w', encoding='utf-8', newline='\n'), ensure_ascii=False, indent=0)
        by = collections.Counter(u['file'] for u in us)
        chars = collections.Counter()
        for u in us: chars[u['file']] += len(u['text'])
        print('%d units, %d characters' % (len(us), sum(chars.values())))
        print('  ' + ', '.join('%s %d (%d)' % (f, by[f], chars[f]) for f in by))
    elif cmd == 'paths':
        files = load(sys.argv[2])
        for stem, vals in files.items():
            agg = collections.OrderedDict()
            for path, text in vals: agg.setdefault(path, []).append(text)
            for path, texts in agg.items():
                kind = KIND.get(stem, {}).get(path, '')
                spaced = sum(1 for t in texts if ' ' in t)
                if kind or spaced * 3 >= len(texts):
                    print('%-9s %-14s %-36s %4d  %s' % (kind or '-', stem, path, len(texts), ' | '.join(t[:30] for t in texts[:3])))
    elif cmd == 'list':
        us = json.load(open(sys.argv[2], encoding='utf-8'))
        sel = sys.argv[3]
        stem, _, rng = sel.partition(':')
        lo, hi = 0, 10 ** 9
        if rng:
            a, _, b = rng.partition('-')
            lo = int(a); hi = int(b) if b else 10 ** 9
        done = set()
        if '--done' in sys.argv:
            d = sys.argv[sys.argv.index('--done') + 1]
            for f in glob.glob(os.path.join(d, '*.tsv')):
                for line in open(f, encoding='utf-8'):
                    if '\t' in line and not line.startswith(('#', '+')): done.add(line.split('\t', 1)[0])
        for u in us:
            if u['file'] != stem: continue
            n = int(u['id'].rsplit('.', 1)[1])
            if n < lo or n > hi: continue
            if u['id'] in done and '--all' not in sys.argv: continue
            leaf = u['path'].split('/', 1)[-1] if '/' in u['path'] else u['path']
            special = [k for k in u['kinds'] if k in ('pre', 'beh', 'base', 'post', 'first', 'epithet')]
            if special: leaf += ' [' + ','.join(special) + ']'
            print('%s\t%s\t%s' % (u['id'].split('.', 1)[1], leaf, u['text'].replace('\\', '\\\\').replace('\n', '\\n')))
    else:
        print(__doc__); return 2
    return 0


if __name__ == '__main__':
    sys.exit(main())
