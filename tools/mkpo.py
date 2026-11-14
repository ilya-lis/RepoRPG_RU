#!/usr/bin/env python3
"""Builds the dictionary of the add-on from the translator's files.

    translation/units.json      what there is to translate (tools/units.py)
    translation/ru/*.tsv        id<TAB>russian        (\\n, \\t, \\" and \\\\ are escapes; lines starting with # are comments)
                                extra entries:  +term<TAB>english<TAB>russian   +pattern<TAB>...   +global<TAB>...   +@Type::Member<TAB>...
 -> mod/lang/RepoRPG_RU.po      for the game
 -> <checks.tsv>                english sample <TAB> expected text, for tests/langcheck.sh

usage: mkpo.py <units.json> <tsv dir> <out.po> [checks.tsv]
"""
import json, re, sys, glob, os, collections
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import datapo

HOLE = re.compile(r'\{\{|\}\}|\{[^{}]*\}|[{}]')
TAG = re.compile(r'</?(?:color|b|i|size|u|s|mark|sup|sub)(?:=[^>]*)?>', re.I)
NUM = re.compile(r'^\{(\d+)([,:][^{}]*)?\}$')
NAMED = re.compile(r'^\{[A-Za-z][^{}]*\}$')
SIMPLE_NAME = re.compile(r'^\{[A-Za-z][A-Za-z0-9]{0,7}\}$')


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


def po(s):
    return '"' + s.replace('\\', '\\\\').replace('"', '\\"').replace('\n', '\\n').replace('\t', '\\t').replace('\r', '\\r') + '"'


def edges(src, tr):
    """the translation gets the outer whitespace of the original (pieces of sentences depend on it)"""
    lead = src[:len(src) - len(src.lstrip(' \n\t'))]
    trail = src[len(src.rstrip(' \n\t')):]
    return lead + tr.strip(' \n\t') + trail if src.strip(' \n\t') else tr


def check(src, tr):
    """-> problem text or None; mirrors Fmt.SameHoles of the add-on"""
    hs, ht = HOLE.findall(src), HOLE.findall(tr)
    known = {h for h in hs if NUM.match(h) or NAMED.match(h)}
    for h in ht:
        if (NUM.match(h) or NAMED.match(h)) and h not in known: return 'placeholder %s is not in the original' % h
    stray = lambda hh: sorted(h for h in hh if not (NUM.match(h) or NAMED.match(h)))
    if stray(hs) != stray(ht): return 'braces differ'
    if sorted(t.lower() for t in TAG.findall(src)) != sorted(t.lower() for t in TAG.findall(tr)): return 'rich text tags differ'
    if ('\n' in tr) != ('\n' in src) and len(src) < 60: return 'line breaks differ'
    m = TAGGED.match(src)
    if m and not tr.startswith(m.group(0)): return 'the message tag %r has to stay in front' % m.group(0)
    return None


# "season|The run is complete.": the host sends the note with a tag in front, the receiving side takes the tag off and shows the rest
TAGGED = re.compile(r'^(?:\{\d+\})?[a-z][a-z0-9+\-]{1,11}\|(?=\S)')


def split_runs(pattern):
    """'a {0} b {1}' -> (['a ', ' b ', ''], [0, 1])"""
    runs, holes, cur, i = [], [], [], 0
    for m in re.finditer(r'\{\{|\}\}|\{(\d+)\}', pattern):
        cur.append(pattern[i:m.start()]); i = m.end()
        if m.group(0) == '{{': cur.append('{')
        elif m.group(0) == '}}': cur.append('}')
        else:
            runs.append(''.join(cur)); cur = []; holes.append(int(m.group(1)))
    cur.append(pattern[i:]); runs.append(''.join(cur))
    return runs, holes


NUMBER = re.compile(r'\d+(?:[\u00a0 ]\d{3})+(?!\d)|\d+(?:[.,]\d+)?')


def numbered(en, ru):
    """('+{0}% max health for {1}s', '+{0}% к здоровью на {1} с', 2) - every number becomes a placeholder; () when there are
    no numbers; None when the translation does not have the very same numbers."""
    nums = NUMBER.findall(en)
    if not nums: return ()
    if '{' in en or '}' in en or '{' in ru or '}' in ru: return ()
    left = list(enumerate(nums))
    def take(m):
        for k, (i, n) in enumerate(left):
            if n == m.group(0):
                del left[k]
                return '{%d}' % i
        raise KeyError(m.group(0))
    try: pr = NUMBER.sub(take, ru)
    except KeyError: return None
    if left: return None
    it = iter(range(len(nums)))
    pe = NUMBER.sub(lambda m: '{%d}' % next(it), en)
    return pe, pr, len(nums)


def sample(text, expected):
    """fill the placeholders of an english text and of its translation with the same values"""
    vals = {}
    # a placeholder the translation leaves out is the English plural ending ("{0} item{1}"): the mod fills it with "s"
    kept = set(int(m.group(1)) for m in (NUM.match(h) for h in HOLE.findall(expected)) if m)
    for h in HOLE.findall(text):
        m = NUM.match(h)
        if m and int(m.group(1)) not in kept: vals[int(m.group(1))] = 's'
    def fill(s):
        def rep(m):
            h = m.group(0)
            if h == '{{': return '{'
            if h == '}}': return '}'
            n = NUM.match(h)
            if n: return vals.setdefault(int(n.group(1)), str(3 + 4 * int(n.group(1))))
            return h
        return HOLE.sub(rep, s)
    return fill(text), fill(expected)


def main():
    units = json.load(open(sys.argv[1], encoding='utf-8'))
    by_id = {str(u['id']): u for u in units}
    tr, extra, problems = {}, [], []
    hand_num, hand_words = [], []
    for f in sorted(glob.glob(os.path.join(sys.argv[2], '*.tsv'))):
        for n, line in enumerate(open(f, encoding='utf-8'), 1):
            line = line.rstrip('\n').rstrip('\r')
            if not line.strip() or line.startswith('#'): continue
            cols = line.split('\t')
            where = '%s:%d' % (os.path.basename(f), n)
            if cols[0].startswith('+'):
                if len(cols) != 3: problems.append(where + ': expected +kind<TAB>english<TAB>russian'); continue
                if cols[0] in ('+numbers', '+words'):
                    # +numbers<TAB>+{0} Scrap – {1}<TAB>0     placeholder 0 of that sentence is always a number
                    # +words<TAB>{0} gear<TAB>0               placeholder 0 is one of the mod's own words (a rarity, a tab):
                    #                                         the sentence is recognised only when the dictionary has that word
                    # +words<TAB>Rarity: {0}<TAB>0f           ...and a rarity standing there takes that form: m he, f she, n it,
                    #                                         p they («Редкость: редкая»). Such a sentence is translated only at
                    #                                         the moment it is drawn: inside the mod's code nobody knows the word
                    # +words<TAB>{0} gear<TAB>0N              ...and nothing but a rarity may stand there ("team gear" is another text)
                    mark = cols[2].strip()
                    if cols[0] == '+numbers':
                        if not re.fullmatch(r'\d+(,\d+)*', mark): problems.append(where + ': expected placeholder numbers like 0,2')
                        else: hand_num.append((unescape(cols[1]), set(int(x) for x in mark.split(',')), where))
                    elif not re.fullmatch(r'\d+[mfnpMFNP]?(,\d+[mfnpMFNP]?)*', mark): problems.append(where + ': expected placeholder numbers like 0,2 or 0f')
                    else: hand_words.append((unescape(cols[1]), dict((int(x.rstrip('mfnpMFNP')), x[len(x.rstrip('mfnpMFNP')):]) for x in mark.split(',')), where))
                    continue
                extra.append((cols[0][1:], unescape(cols[1]), unescape(cols[2]), where)); continue
            if len(cols) != 2: problems.append(where + ': expected id<TAB>text'); continue
            key = cols[0].strip().rstrip('ct')
            if key not in by_id: problems.append(where + ': unknown id ' + cols[0]); continue
            if key in tr: problems.append(where + ': id %s translated twice' % key)
            tr[key] = (unescape(cols[1]), where)

    glob_e, term_e, pat_e = collections.OrderedDict(), collections.OrderedDict(), collections.OrderedDict()
    num_e = {}                 # english text with placeholders -> the placeholders that are numbers wherever the mod makes it

    def numbers(en, u):
        have = set(u.get('num', ()))
        # stat lines are "+5% max health", "-2 extra jumps": the mod writes the sign and the number first (StatInfo::Describe)
        if 'StatInfo::Describe' in u.get('scopes', ()) and en.startswith('{0}'): have.add(0)
        num_e[en] = (num_e[en] & have) if en in num_e else have
    chain_scoped = []          # (scope, unit id, {piece: translation})
    checks = []
    lit_by_text = {}
    formed = set(en for en, marks, where in hand_words if any(marks.values()))
    for uid, (t, where) in tr.items():
        u = by_id[uid]
        src = u['text']
        t = edges(src, t)
        if t == src: continue
        bad = check(src, t)
        if bad:
            problems.append('%s: #%s %s: %r -> %r' % (where, uid, bad, src[:60], t[:60])); continue
        if u.get('group') == 'config':
            # The Settings tab shows a one-line summary of every description, cut by the mod itself (RpgRuntime::SettingSummary):
            # a leading "Label: " shorter than 24 characters is dropped, then everything from the first ". " on, then the final ".".
            # The same cuts hit the translation, so an abbreviation in the first sentence ("см. X", "макс. запас") ends it early.
            n = t.find(': ')
            body = t[n + 2:] if 0 < n < 24 else t
            k = body.find('. ')
            if k > 0 and re.search(r'(?:^|[\s(«"])(?:см|ур|выкл|вкл|напр|т|е|д|пикс|макс|мин|сек|с|м|кг|др)$', body[:k], re.I):
                problems.append('%s: #%s the summary of this setting would end at an abbreviation (%r): spell the word out or move it'
                                % (where, uid, body[max(0, k - 12):k + 1])); continue
            ns = src.find(': ')
            if 0 < n < 24 and not 0 < ns < 24 and len(body) < 30:
                problems.append('%s: #%s the summary of this setting would lose its beginning (%r is cut as a label)' % (where, uid, t[:n + 1])); continue
        if 'StatInfo::Describe' in u.get('scopes', ()) and src.startswith('{0}'):
            # the mod takes its own stat lines apart: SplitStat() cuts at the first space ("+5%" | "max health") and
            # StatName()/StatUnit() expect "+1", then "%" or nothing, then a space. A unit glued to the number in English
            # ("+3m aura radius") cannot be glued in Russian (the mod looks for the letters m and s), so it goes to the end.
            want = '{0}% ' if src.startswith('{0}%') else '{0} '
            if not t.startswith(want) or t[len(want):len(want) + 1] in ('', ' ') or re.match(r'^(м|с|сек\.?|мин\.?) ', t[len(want):]):
                problems.append('%s: #%s a stat line has to start with %r and then the name (the mod cuts it at the first space): %r -> %r'
                                % (where, uid, want, src[:60], t[:60])); continue
        if src in formed:
            # "Rarity: {0}" -> «Редкость: {0}» with the rarity in the feminine: only the add-on's own code can do that, so the
            # sentence stays as the mod wrote it and is translated when it is drawn
            pat_e[src] = t
            if any(NUM.match(h) for h in HOLE.findall(src)): numbers(src, u)
            continue
        m = TAGGED.match(src)
        if m and re.search('[A-Za-z]{2}', src[m.end():]):
            # "season|The run is complete.": a note the host puts into the state of the run, which goes to every player.
            # It stays as the mod wrote it inside the code (the receiving side cuts it to a fixed length, and a player
            # without the add-on should not get Russian text); what is shown is the part after the tag, and that part
            # gets an entry of its own, used at the moment it is drawn - on the host's screen as well.
            rest_s, rest_t = src[m.end():], t[m.end():]
            if any(NUM.match(h) for h in HOLE.findall(rest_s)): pat_e[rest_s] = rest_t; numbers(rest_s, u)
            else: term_e[rest_s] = rest_t
            checks.append(sample(rest_s, rest_t))
            continue
        if u['kind'] == 'lit':
            (term_e if u.get('term') else glob_e)[src] = t
            lit_by_text[src] = (t, u)
            if any(NUM.match(h) for h in HOLE.findall(src)): numbers(src, u)
            named = []
            for h in HOLE.findall(src):
                if SIMPLE_NAME.match(h) and h not in named: named.append(h)
            if named and not any(NUM.match(h) for h in HOLE.findall(src)):
                # "{cat} is worth 50% more.": the mod puts the name in with Replace("{cat}", ...). Where the code is not
                # rewritten the text arrives with the name already inside, so the same sentence is also kept as a pattern.
                ps, pt = src, t
                for k, h in enumerate(named): ps, pt = ps.replace(h, '{%d}' % k), pt.replace(h, '{%d}' % k)
                pat_e[ps] = pt
                checks.append(sample(ps, pt))
            if any(NUM.match(h) for h in HOLE.findall(src)) and len(re.findall('[A-Za-z]', HOLE.sub('', src))) < 3:
                # "{0}  Lv {1}": too little text for the add-on to treat it as a sentence on its own accord.
                # A whole word next to the number is still safe to recognise; "{0}s" or "x{0}" is not.
                if re.search(r'(?<![A-Za-z\x00])[A-Za-z]{2,}(?![A-Za-z\x00])', HOLE.sub('\x00', src)): pat_e[src] = t
                else: continue
            checks.append(sample(src, t))
            continue
        runs_s, holes_s = split_runs(src)
        runs_t, holes_t = split_runs(t)
        if holes_s: pat_e[src] = t; numbers(src, u)
        checks.append(sample(src, t))
        # the same sentence, piece by piece, for the places where the mod builds it
        if holes_t != holes_s or len(runs_t) != len(runs_s): continue
        pieces, ok = {}, True
        for a, b in zip(runs_s, runs_t):
            if a == b: continue
            if not a or not b or not re.search('[A-Za-z]', a): ok = False; break
            if pieces.get(a, b) != b: ok = False; break
            pieces[a] = b
        if not ok or not pieces: continue
        for sc in u['scopes']:
            if sc not in u.get('unsafe', ()): chain_scoped.append((sc, uid, pieces))

    # one piece, two translations inside one method: neither sentence is replaced in the code (both still work when drawn)
    by_scope = collections.defaultdict(list)
    for sc, uid, pieces in chain_scoped: by_scope[sc].append((uid, pieces))
    scoped = collections.OrderedDict()
    dropped = 0
    for sc, lst in by_scope.items():
        alive = list(lst)
        while True:
            seen, clash = {}, set()
            for uid, pieces in alive:
                for a, b in pieces.items():
                    if a in seen and seen[a][1] != b: clash.add(uid); clash.add(seen[a][0])
                    else: seen.setdefault(a, (uid, b))
                    g = lit_by_text.get(a)
                    if g and g[0] != b and sc in g[1]['scopes']: clash.add(uid)
            if not clash: break
            dropped += len(clash)
            if os.environ.get('MKPO_VERBOSE'):
                for uid in sorted(clash):
                    print('  draw time only: #%s in %s: %r' % (uid, sc, [p for u2, p in alive if u2 == uid]))
            alive = [(uid, p) for uid, p in alive if uid not in clash]
        for uid, pieces in alive:
            for a, b in pieces.items(): scoped.setdefault(sc + '#' + uid, collections.OrderedDict())[a] = b

    # +inside:contracts<TAB>Dispatch<TAB>Диспетчерская: a value replaced where it stands in that file of the mod's data
    # (only at the paths the [data] rules of tools/genrules.py allow). For a name that is two different things in English.
    inside = collections.OrderedDict()
    # +adj<TAB>Rare<TAB>Редкий|Редкая|Редкое|Редкие: a word the mod writes in front of a name (a rarity, "Starter"); it takes the
    #       form of the thing named - he, she, it, they (Loc.Built)
    # +noun<TAB>gear<TAB>n|снаряжение: a common word such a name may end with - its gender and its translation
    adj_e, noun_e, of_e = collections.OrderedDict(), collections.OrderedDict(), collections.OrderedDict()
    for kind, en, ru, where in extra:
        bad = check(en, ru)
        if bad: problems.append('%s: %s: %r' % (where, bad, en[:60])); continue
        if kind == 'adj':
            if len(ru.split('|')) != 4 or not all(x.strip() for x in ru.split('|')): problems.append('%s: +adj needs four forms: m|f|n|p' % where)
            else: adj_e[en] = ru
            continue
        if kind == 'noun':
            if not re.fullmatch(r'[mfnp]\|[^|]+', ru): problems.append('%s: +noun needs a gender and the translation: n|снаряжение' % where)
            else: noun_e[en] = ru
            continue
        if kind == 'term': term_e[en] = ru
        elif kind == 'pattern': pat_e[en] = ru
        elif kind == 'global': glob_e[en] = ru
        elif kind.startswith('@'): scoped.setdefault(kind[1:], collections.OrderedDict())[en] = ru
        elif kind.startswith('inside:'): inside.setdefault(kind[7:], collections.OrderedDict())[en] = (ru, where)
        else: problems.append('%s: unknown kind +%s' % (where, kind)); continue
        if kind in ('term', 'pattern', 'global'): checks.append(sample(en, ru))

    out = ['# RepoRPG_RU - Russian translation of the RepoRPG mod (ExtraCreamyMilk) for R.E.P.O.',
           '# Generated by tools/mkpo.py from translation/ru/*.tsv - edit those files, not this one.',
           '#',
           '#   no msgctxt        replaced inside the mod where that is safe, otherwise at the moment the text is drawn',
           '#   term              only at the moment the text is drawn (words the mod also uses as internal names)',
           '#   pattern           a sentence the mod puts together itself; {0} stands for the changing part',
           '#   shape             a text of the mod\'s data; {0}, {1} are its numbers, which change with ranks and settings',
           '#   adj, noun, of     the words of names the mod builds itself ("Rare Sharp Striker Module of the Brawler"):',
           '#                     adj - a word in front, its forms he|she|it|they; noun - the thing, its gender (m, f, n, p);',
           '#                     of - whose it is, in the genitive. The name is put together when it is drawn, in agreement',
           '#   data:guide        a value of the mod\'s guide.json, replaced inside the data (the Guide tab and its search)',
           '#   data:contracts    the same for contracts.json: only the values at the paths the rules file names',
           '#   @Type::Member     only inside that method of the mod; "#N" marks the pieces of one sentence (all replaced or none)',
           '#   numbers           not a translation: which placeholders of a sentence are numbers (worked out from the mod\'s code)',
           '#   words             not a translation: which placeholders hold one of the mod\'s own words (a rarity, a tab name);',
           '#                     a letter after the number is the form a rarity takes there - m he, f she, n it, p they',
           '#                     (a capital letter: nothing but a rarity may stand there)',
           'msgid ""', 'msgstr ""', '"Content-Type: text/plain; charset=UTF-8\\n"', '"Language: ru\\n"', '']
    def emit(ctx, table):
        for en, ru in table.items():
            if ctx: out.append('msgctxt ' + po(ctx))
            out.append('msgid ' + po(en)); out.append('msgstr ' + po(ru)); out.append('')
    # the mod's built-in data: names and descriptions (drawn), the Guide (replaced inside the data)
    shape_e, guide_e, data_stats = collections.OrderedDict(), collections.OrderedDict(), None
    data_json = os.path.join(os.path.dirname(os.path.abspath(sys.argv[1])), 'data.json')
    if os.path.isfile(data_json):
        known = dict(glob_e); known.update(term_e)
        d_terms, shape_e, guide_e, d_checks, data_stats = datapo.build(data_json, os.path.join(sys.argv[2], 'data'), known, problems,
                                                                        heads=adj_e, things=noun_e)
        for en, ru in d_terms.items(): term_e[en] = ru
        checks.extend(d_checks)
        adj_e, noun_e, of_e = data_stats['adj'], data_stats['noun'], data_stats['of']
    for en in noun_e:
        if '|' not in noun_e[en] and en not in term_e and en not in glob_e: problems.append('noun %r: the dictionary has no such name' % en)
    emit(None, glob_e); emit('term', term_e); emit('pattern', pat_e); emit('shape', shape_e)
    emit('adj', adj_e); emit('noun', noun_e); emit('of', of_e); emit('data:guide', guide_e)
    for stem, table in inside.items():
        if os.path.isfile(data_json):
            texts = set(u['text'] for u in json.load(open(data_json, encoding='utf-8'))
                        if u['file'] == stem or any(a.startswith(stem + ':') for a in u['also']))
            for en, (ru, where) in table.items():
                if en not in texts: problems.append('%s: %s.json has no text %r' % (where, stem, en[:60]))
        emit('data:' + stem, collections.OrderedDict((en, ru) for en, (ru, where) in table.items()))
    for sc in sorted(scoped): emit('@' + sc, scoped[sc])
    # which placeholders are numbers: "{0} players" is made from a count, so "more players" is another text altogether
    for en, idx, where in hand_num:
        if en not in pat_e and en not in glob_e: problems.append('%s: +numbers for a sentence that has no entry: %r' % (where, en[:60])); continue
        num_e[en] = set(num_e.get(en, ())) | idx
    word_e = {}
    for en, marks, where in hand_words:
        if en not in pat_e and en not in glob_e: problems.append('%s: +words for a sentence that has no entry: %r' % (where, en[:60])); continue
        if any(marks.values()) and en not in pat_e:
            problems.append('%s: +words with a form needs a sentence the mod puts together, not a text replaced inside its code: %r' % (where, en[:60])); continue
        word_e.setdefault(en, {}).update(marks)
    # the generated samples put numbers into every placeholder; where a word of the mod's own belongs, a number proves nothing
    # (those sentences are checked by hand-written cases in tests/drawn.tsv)
    skip = set(sample(en, (pat_e.get(en) or glob_e.get(en)))[0] for en in word_e)
    checks = [c for c in checks if c[0] not in skip]
    for ctx, table in (('numbers', num_e), ('words', word_e)):
        for en in list(glob_e) + list(pat_e):
            idx = table.get(en)
            if not idx: continue
            out.append('msgctxt ' + po(ctx)); out.append('msgid ' + po(en))
            out.append('msgstr ' + po(','.join(str(k) + (idx[k] if ctx == 'words' else '') for k in sorted(idx)))); out.append('')
    os.makedirs(os.path.dirname(sys.argv[3]), exist_ok=True)
    open(sys.argv[3], 'w', encoding='utf-8', newline='\n').write('\n'.join(out))
    if len(sys.argv) > 4:
        with open(sys.argv[4], 'w', encoding='utf-8', newline='\n') as f:
            for en, ru in checks:
                if '\t' in en or '\r' in en: continue
                f.write(en.replace('\\', '\\\\').replace('\n', '\\n') + '\t' + ru.replace('\\', '\\\\').replace('\n', '\\n') + '\n')
    total = sum(1 for u in units if u['group'] in ('display', 'maybe'))
    have = sum(1 for k in tr if by_id[k]['group'] in ('display', 'maybe'))
    print('translated units: %d (interface %d of %d)   entries: %d global, %d term, %d pattern, %d scoped in %d methods (%d sentences only at draw time)'
          % (len(tr), have, total, len(glob_e), len(term_e), len(pat_e), sum(len(v) for v in scoped.values()), len(scoped), dropped))
    if data_stats:
        # the data units nothing covers yet, for "data.py list --left"
        out = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(sys.argv[2]))), 'build')
        try:
            os.makedirs(out, exist_ok=True)
            with open(os.path.join(out, 'data-left.txt'), 'w', encoding='utf-8') as f: f.write('\n'.join(data_stats['left ids']) + '\n')
        except OSError: pass
        print('data: %d of %d texts translated; %d terms, %d shapes, %d guide values; generated: %d prototype names, %d boss names%s'
              % (data_stats['translated'], data_stats['units'], len(d_terms), len(shape_e), len(guide_e), data_stats['gear names'],
                 data_stats['boss names'], ('; left: ' + data_stats['left']) if data_stats['left'] else ''))
        print('names built when drawn: %d words in front, %d things, %d owners (%d samples)'
              % (len(adj_e), len(noun_e), len(of_e), data_stats['built samples']))
    for p in problems: print('  PROBLEM ' + p)
    return 1 if problems else 0


if __name__ == '__main__':
    sys.exit(main())
