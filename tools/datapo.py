#!/usr/bin/env python3
"""The mod's built-in data in the dictionary (used by mkpo.py).

    translation/data.json           the texts (tools/data.py units)
    translation/ru/data/*.tsv       id<TAB>russian

A translation line may carry word forms after the text:   Острый|f=Острая|n=Острое|p=Острые
    m f n p     forms of an adjective a gear name starts with (m only when the text itself is not that adjective)
    g           gender of a gear base: m, f, n or p (plural)
    post        the genitive that goes after a gear base instead ("of the Brawler" -> драчуна; "Second Wind" -> второго дыхания)
    e           the form used in a boss name when it is not the text itself (a nickname made of nouns: Мелкий Шрифт)
                boss names: an adjective takes its f= form after a name marked g=f

What comes out:
    terms       exact texts, translated at the moment they are drawn
    shapes      the same texts with their numbers as placeholders (a talent at another rank, a contract with another target)
    guide       values of guide.json, replaced inside the data itself (msgctxt "data:guide")
"""
import json, re, os, glob, collections

NUMBER = re.compile(r'\d+(?:[\u00a0\u202f]\d{3})*(?:[.,]\d+)?')       # the same rule as Loc.NumberEnd
SLOT = re.compile(r'\{(?:t|v2|v)\}|' + NUMBER.pattern)
TOKEN = re.compile(r'\{[^{}]*\}')
TAG = re.compile(r'</?(?:color|b|i|size|u|s)(?:=[^>]*)?>', re.I)
CAPSTONE = 'CAPSTONE: '


def unescape(s):
    out = []; i = 0
    while i < len(s):
        c = s[i]
        if c == '\\' and i + 1 < len(s):
            d = s[i + 1]
            out.append({'n': '\n', 't': '\t', '"': '"', '\\': '\\', 'r': '\r'}.get(d, '\\' + d)); i += 2
        else:
            out.append(c); i += 1
    return ''.join(out)


def forms(cell):
    """'Острый|f=Острая' -> ('Острый', {'f': 'Острая'})"""
    parts = cell.split('|')
    extra = {}
    for p in parts[1:]:
        k, eq, v = p.partition('=')
        if not eq or k not in ('m', 'f', 'n', 'p', 'g', 'post', 'e'): raise ValueError('unknown form %r' % p)
        extra[k] = v
    return parts[0], extra


def shape(en, ru):
    """('+{0}% max health for {1}s', '+{0}% к здоровью на {1} с') - every number (and {t}, {v}, {v2}) becomes a placeholder.
    None when the text has no numbers. ValueError when the translation does not have the very same numbers."""
    slots = SLOT.findall(en)
    if not slots: return None
    left = list(enumerate(slots))
    def take(m):
        for k, (i, n) in enumerate(left):
            if n == m.group(0):
                del left[k]
                return '{%d}' % i
        raise ValueError('the translation has the number %s, the original does not' % m.group(0))
    pr = SLOT.sub(take, ru)
    if left: raise ValueError('the translation lost the number %s' % left[0][1])
    it = iter(range(len(slots)))
    pe = SLOT.sub(lambda m: '{%d}' % next(it), en)
    if '{' in SLOT.sub('', en) or '{' in SLOT.sub('', ru): raise ValueError('braces')
    return pe, pr


def shape_key(en):
    """the English side of shape(): the text with every number as a placeholder; None when it has no numbers"""
    slots = SLOT.findall(en)
    if not slots or '{' in SLOT.sub('', en): return None
    it = iter(range(len(slots)))
    return SLOT.sub(lambda m: '{%d}' % next(it), en)


def fill(msgid, msgstr, values=None):
    """a sample of a shape: the placeholders filled with numbers"""
    n = len(re.findall(r'\{\d+\}', msgid))
    values = values or [str(3 + 4 * i) for i in range(n)]
    f = lambda s: re.sub(r'\{(\d+)\}', lambda m: values[int(m.group(1))], s)
    return f(msgid), f(msgstr)


def summary(text):
    """RpgRuntime::ClassSummary: the first clause of a class description, cut at 44 characters"""
    cut = [i for i in (text.find('.'), text.find(';'), text.find(':')) if i >= 0]
    if cut and min(cut) > 0: text = text[:min(cut)]
    if len(text) > 46: text = text[:44].rstrip() + '\u2026'
    return text


def cap(s): return s[:1].upper() + s[1:]
def low(s): return s[:1].lower() + s[1:]


def load(data_json, tsv_dir, problems):
    units = json.load(open(data_json, encoding='utf-8'))
    by_id = {u['id']: u for u in units}
    tr = {}
    for f in sorted(glob.glob(os.path.join(tsv_dir, '*.tsv'))):
        stem = os.path.basename(f)[:-4]
        for n, line in enumerate(open(f, encoding='utf-8'), 1):
            line = line.rstrip('\n').rstrip('\r')
            if not line.strip() or line.startswith('#'): continue
            cols = line.split('\t')
            where = 'data/%s:%d' % (os.path.basename(f), n)
            if len(cols) != 2: problems.append(where + ': expected id<TAB>text'); continue
            key = cols[0].strip()
            if '.' not in key: key = stem.split('-')[0] + '.' + key          # "0012" inside classes.tsv is classes.0012
            if key not in by_id: problems.append(where + ': unknown id ' + cols[0]); continue
            if key in tr: problems.append(where + ': %s translated twice' % key); continue
            try: text, extra = forms(unescape(cols[1]))
            except ValueError as e: problems.append('%s: %s' % (where, e)); continue
            tr[key] = (text, extra, where)
    return units, tr


def build(data_json, tsv_dir, known, problems):
    """known: english -> russian of the entries the code units already have (a data text that is the same text gets the same words)"""
    units, tr = load(data_json, tsv_dir, problems)
    terms, shapes, guide, checks = collections.OrderedDict(), collections.OrderedDict(), collections.OrderedDict(), []
    todo = collections.Counter(); done = collections.Counter()

    def term(en, ru, where):
        if not en.strip() or en == ru: return
        if en in known and known[en] != ru:
            problems.append('%s: %r is %r elsewhere in the dictionary, here %r' % (where, en[:50], known[en][:40], ru[:40])); return
        if terms.get(en, ru) != ru:
            problems.append('%s: %r has two translations: %r and %r' % (where, en[:50], terms[en][:40], ru[:40])); return
        if en not in known: terms[en] = ru
        checks.append((en, ru))

    def text(en, ru, where, exact=True):
        """a description: the exact text and its shape"""
        if not en.strip(): return
        if exact and '{' not in en: term(en, ru, where)
        try: sh = shape(en, ru)
        except ValueError as e:
            problems.append('%s: %s: %r' % (where, e, en[:60])); return
        if not sh: return
        if shapes.get(sh[0], sh[1]) != sh[1]:
            problems.append('%s: two different translations of one text once the numbers are taken out: %r' % (where, sh[0][:60])); return
        shapes[sh[0]] = sh[1]
        checks.append(fill(*sh))

    pre, behs, base, slots, post, first, epithet = [], [], [], [], [], [], []
    left_ids = []
    for u in units:
        todo[u['file']] += 1
        en, kinds = u['text'], u['kinds']
        have = tr.get(u['id'])
        if have is None:
            if en in known and not (set(kinds) & {'pre', 'beh', 'base', 'post', 'guide', 'keywords', 'contract', 'synergy'}):
                done[u['file']] += 1            # the same text as a code unit: already in the dictionary
                if '{' not in en and NUMBER.search(en): text(en, known[en], u['id'], exact=False)
            else: left_ids.append(u['id'])
            continue
        ru, extra, where = have
        done[u['file']] += 1
        if 'guide' in kinds or 'keywords' in kinds:
            a = sorted(t if '?' not in t else t[:t.index('?')] for t in TOKEN.findall(en))
            b = sorted(t if '?' not in t else t[:t.index('?')] for t in TOKEN.findall(ru))
            if a != b: problems.append('%s: the {tokens} differ from the original: %r' % (where, en[:60])); continue
            for t in TOKEN.findall(ru):
                if '?' in t:
                    yes_no = t[t.index('?') + 1:-1]
                    if yes_no.count(':') != 1: problems.append('%s: "{cfg:Key?yes:no}" needs exactly one colon after the question mark: %r' % (where, t[:60]))
            if sorted(x.lower() for x in TAG.findall(en)) != sorted(x.lower() for x in TAG.findall(ru)):
                problems.append('%s: rich text tags differ: %r' % (where, en[:60])); continue
            if en.count('\n') != ru.count('\n'): problems.append('%s: line breaks differ: %r' % (where, en[:60])); continue
            guide[en] = (en + ' ' + ru) if 'keywords' in kinds else ru
            continue
        if TOKEN.sub('', ru).count('{') or TOKEN.sub('', ru).count('}'):
            problems.append('%s: stray braces' % where); continue
        if set(kinds) & {'pre', 'beh'}:
            m = extra.get('m', ru)
            if 'post' not in extra and not all(k in extra for k in 'fnp'):
                problems.append('%s: a word gear names start with needs f=, n= and p= forms (or post=)' % where)
            else:
                pre.append((en, m, extra))
                if 'beh' in kinds: behs.append((en, m, extra))
        if 'slot' in kinds:
            if extra.get('g') not in ('m', 'f', 'n', 'p'): problems.append('%s: a gear slot needs g=m, f, n or p' % where)
            else: slots.append((en, ru, extra['g']))
        if 'base' in kinds:
            if extra.get('g') not in ('m', 'f', 'n', 'p'): problems.append('%s: a gear base needs g=m, f, n or p' % where)
            else: base.append((en, ru, extra['g']))
        if 'post' in kinds:
            if 'post' not in extra: problems.append('%s: the last word of gear names needs post= (genitive)' % where)
            else: post.append((en, extra['post']))
        if 'first' in kinds: first.append((en, ru, extra.get('g', 'm')))
        if 'epithet' in kinds: epithet.append((en, extra.get('e', ru), extra.get('f')))
        if kinds == ['pre'] or kinds == ['post'] or kinds == ['first'] or kinds == ['epithet'] or kinds == ['base']:
            continue                                             # never shown on their own
        if 'contract' in kinds:
            if '{s}' in ru: problems.append('%s: {s} is the English plural ending, leave it out' % where); continue
            if ('{t}' in en) != ('{t}' in ru): problems.append('%s: {t} differs' % where); continue
            for s in ({'', 's'} if '{s}' in en else {''}):
                text(en.replace('{s}', s), ru, where, exact='{s}' not in en)
            continue
        if 'synergy' in kinds:
            if sorted(TOKEN.findall(en)) != sorted(TOKEN.findall(ru)): problems.append('%s: {v} / {v2} differ' % where); continue
            text(en, ru, where)
            continue
        if any(k in kinds for k in ('text', 'class', 'passive', 'node')):
            text(en, ru, where)
        else:
            term(en, ru, where)
        if 'class' in kinds:
            se, sr = summary(en), summary(ru)
            if se != en: text(se, sr, where + ' (summary)')
        if 'passive' in kinds:
            i = en.lower().find('weakness:')
            if i >= 0:
                j = ru.find('Слабость:')
                if j < 0: problems.append('%s: "Weakness:" has to be "Слабость:" (the mod cuts the description there)' % where)
                else:
                    text(en[:i].strip(), ru[:j].strip(), where + ' (before the weakness)')
                    text(en[i + 9:].strip(), ru[j + 9:].strip(), where + ' (the weakness)')

    # names the mod builds itself
    made = 0
    if base and (pre or post):
        for b_en, b_ru, g in base:
            for p_en, p_m, p_x in [(None, None, None)] + pre:
                for s_en, s_ru in [(None, None)] + post:
                    if p_en is None and s_en is None: term(b_en, b_ru, 'gear name'); continue
                    en = (p_en + ' ' if p_en else '') + b_en + (' of the ' + s_en if s_en else '')
                    if p_en is None: ru = b_ru
                    elif 'post' in p_x and not all(k in p_x for k in 'fnp'): ru = b_ru + ' ' + p_x['post']
                    else: ru = cap({'m': p_m, 'f': p_x['f'], 'n': p_x['n'], 'p': p_x['p']}[g]) + ' ' + low(b_ru)
                    if s_en: ru += ' ' + s_ru
                    if en in known or en in terms: continue
                    terms[en] = ru; made += 1
    # "Prototype Mender Core": the item of the research project "Prototype lab"
    for b_en, b_ru, g in slots:
        for p_en, p_m, p_x in behs:
            en = 'Prototype ' + p_en + ' ' + b_en
            if 'post' in p_x and not all(k in p_x for k in 'fnp'): ru = b_ru + ' ' + p_x['post']
            else: ru = cap({'m': p_m, 'f': p_x['f'], 'n': p_x['n'], 'p': p_x['p']}[g]) + ' ' + low(b_ru)
            if en not in known and en not in terms: terms[en] = 'Прототип: ' + low(ru); made += 1
    bosses = 0
    for f_en, f_ru, g in first:
        for e_en, e_ru, e_f in epithet:
            # "Grindle the Unbroken" - Гриндл Несломленный, "Madame Ohm the Unbroken" - Мадам Ом Несломленная;
            # a nickname made of nouns does not change: Гриндл Мелкий Шрифт
            en = f_en + ' the ' + e_en
            if en not in terms: terms[en] = f_ru + ' ' + (e_f if g == 'f' and e_f else e_ru); bosses += 1
    # a text that differs from a translated one only by its numbers is covered by that shape
    by_id = {u['id']: u for u in units}
    still = []
    for uid in left_ids:
        u = by_id[uid]
        key = None if set(u['kinds']) & {'guide', 'keywords'} else shape_key(u['text'])
        if key is not None and key in shapes:
            done[u['file']] += 1
            # the mod puts a number where {t} / {v} is before it shows the text, so the sample gets numbers too
            values = [v if not v.startswith('{') else str(3 + 4 * i) for i, v in enumerate(SLOT.findall(u['text']))]
            checks.append(fill(key, shapes[key], values))
        else: still.append(uid)
    left_ids = still
    stats = collections.OrderedDict()
    stats['units'] = sum(todo.values()); stats['translated'] = sum(done.values())
    stats['left ids'] = left_ids
    stats['left'] = ', '.join('%s %d' % (f, todo[f] - done[f]) for f in todo if todo[f] != done[f])
    stats['gear names'] = made; stats['boss names'] = bosses
    return terms, shapes, guide, checks, stats
