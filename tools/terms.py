#!/usr/bin/env python3
"""terms.py [-v]

Checks the add-on's Russian texts against translation/official.tsv - the words of the game itself as its official
Russian localisation has them. A word of the fourth column (a regular expression) must not occur in a translation,
unless the English original matches the fifth (case does not matter): "точка извлечения" where the game says «точка сбора».

Also checks the monsters' names (+game in translation/ru/extra.tsv): one translation for one name.
Exit code 1 when something is found. -v lists the rules and how many texts each one looked at.
"""
import glob, json, os, re, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, 'tools'))
from official import glossary


def texts():
    """(where, english, russian) for every translated text of the add-on"""
    units = {str(u['id']): u['text'] for u in json.load(open(os.path.join(ROOT, 'translation', 'units.json'), encoding='utf-8'))}
    data = {d['id']: d['text'] for d in json.load(open(os.path.join(ROOT, 'translation', 'data.json'), encoding='utf-8'))}
    ru = os.path.join(ROOT, 'translation', 'ru')
    for path in sorted(glob.glob(os.path.join(ru, 'part*.tsv'))) + sorted(glob.glob(os.path.join(ru, 'data', '*.tsv'))) \
            + [os.path.join(ru, 'extra.tsv')]:
        name = os.path.basename(path)[:-4]
        for n, line in enumerate(open(path, encoding='utf-8'), 1):
            line = line.rstrip('\n').rstrip('\r')
            if not line.strip() or line.startswith('#'): continue
            c = line.split('\t')
            where = '%s:%d' % (os.path.relpath(path, ROOT), n)
            if name == 'extra':
                # +numbers, +words, +names carry placeholder numbers, not a translation
                if len(c) == 3 and c[0] not in ('+numbers', '+words', '+names'): yield where, c[1], c[2]
            elif len(c) >= 2:
                key = c[0].strip().rstrip('ctd')
                en = units.get(key) if name.startswith('part') else data.get(name + '.' + key)
                if en is not None: yield where, en, '\t'.join(c[1:])


def main():
    verbose = '-v' in sys.argv[1:]
    rules = []
    for en, ru, key, avoid, unless, n in glossary():
        if not avoid: continue
        try: rules.append((en, ru, re.compile(avoid, re.I), re.compile(unless, re.I) if unless else None, n, [0]))
        except re.error as e: sys.exit('official.tsv:%d: %s' % (n, e))
    all_texts = list(texts())
    bad = 0
    for where, en, ru in all_texts:
        for word, official, avoid, unless, n, seen in rules:
            m = avoid.search(ru)
            if not m: continue
            if unless is not None and unless.search(en):
                seen[0] += 1
                continue
            bad += 1
            print('%s: «%s» - the game says «%s» for "%s" (official.tsv:%d)\n    %s\n    %s' % (where, m.group(0), official, word, n, en[:140], ru[:140]))
    names = {}
    for n, line in enumerate(open(os.path.join(ROOT, 'translation', 'ru', 'extra.tsv'), encoding='utf-8'), 1):
        c = line.rstrip('\n').split('\t')
        if len(c) == 3 and c[0] == '+game':
            if c[1] in names and names[c[1]] != c[2]:
                bad += 1
                print('translation/ru/extra.tsv:%d: +game %r has two translations' % (n, c[1]))
            names[c[1]] = c[2]
    if verbose:
        for word, official, avoid, unless, n, seen in rules:
            print('  %-22s %-26s not /%s/%s' % (word, official, avoid.pattern, ('   (%d allowed by the original)' % seen[0]) if unless else ''))
    print('terms: %d texts, %d rules, %d names of monsters: %s' % (len(all_texts), len(rules), len(names), 'no old words' if not bad else '%d to fix' % bad))
    return 1 if bad else 0


if __name__ == '__main__':
    sys.exit(main())
