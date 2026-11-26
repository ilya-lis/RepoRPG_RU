#!/usr/bin/env python3
"""official.py [--from <folder>] [--lang ru-RU] <command>

The game's own texts, as its official localisation has them. The studio keeps a public mirror of the string tables
of the latest release for mod developers (github.com/Semiwork-Studios-AB/REPOAssets, Apache-2.0): Unity "StringTable"
assets, one per table (Game, HUD, Input, Menu, Subtitles, TTS) and language. This reads them without Unity.

    official.py dump                 every text: table/key <TAB> English <TAB> Russian
    official.py find <word> ...      the texts whose English or Russian side has one of the words (regular expressions)
    official.py check                translation/official.tsv against the tables: is every word listed there still
                                     what the game says? (run it after an update of the game)

--from: a checkout of the mirror (the folder that has Localization/Tables). Without it the mirror is cloned into
~/.cache/rtk/REPOAssets (needs github.com) and updated on every run.
"""
import os, re, subprocess, sys

MIRROR = 'https://github.com/Semiwork-Studios-AB/REPOAssets'
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ESCAPES = {'n': '\n', 't': '\t', 'r': '\r', '"': '"', '\\': '\\', '/': '/', '0': '\0', ' ': ' ', 'a': '\a', 'b': '\b',
           'e': '\x1b', 'f': '\f', 'v': '\v', '_': '\u00a0', 'N': '\u0085', 'L': '\u2028', 'P': '\u2029'}


def scalar(lines):
    """A YAML scalar as Unity writes it: plain, 'single' or "double" quoted, folded over several lines."""
    first = lines[0].strip()
    rest = [l.strip() for l in lines[1:]]
    if first.startswith('"'):
        # a line break folds into one space, an empty line is a line break, a backslash at the end of a line joins
        out, buf = [], first[1:]
        parts = [buf] + rest
        text = ''
        for n, part in enumerate(parts):
            last = n == len(parts) - 1
            if last:
                if not part.endswith('"'): raise ValueError('unterminated string: %r' % lines[0])
                part = part[:-1]
            # an odd number of backslashes at the end: the line break is escaped away
            tail = len(part) - len(part.rstrip('\\'))
            joined = tail % 2 == 1 and not last
            if joined: part = part[:-1]
            text += part
            if not last and not joined:
                text += '\n' if parts[n + 1] == '' else ('' if part == '' else ' ')
        i = 0
        while i < len(text):
            c = text[i]
            if c != '\\': out.append(c); i += 1; continue
            e = text[i + 1]
            if e == 'x': out.append(chr(int(text[i + 2:i + 4], 16))); i += 4
            elif e == 'u': out.append(chr(int(text[i + 2:i + 6], 16))); i += 6
            elif e == 'U': out.append(chr(int(text[i + 2:i + 10], 16))); i += 10
            else: out.append(ESCAPES[e]); i += 2
        s = ''.join(out)
        # surrogate pairs written as two \u escapes
        return s.encode('utf-16', 'surrogatepass').decode('utf-16')
    parts = [first] + rest
    text = ''
    for n, part in enumerate(parts):
        text += part
        if n < len(parts) - 1: text += '\n' if parts[n + 1] == '' else ('' if part == '' else ' ')
    if text.startswith("'"):
        if not text.endswith("'") or len(text) < 2: raise ValueError('unterminated string: %r' % lines[0])
        return text[1:-1].replace("''", "'")
    return text


def closed(lines):
    """Is the quoted scalar that began on lines[0] complete? (Unity may put the closing quote on a line of its own, unindented.)"""
    first = lines[0].lstrip()
    text = '\n'.join([first] + lines[1:]).rstrip()
    if first.startswith('"'):
        if len(text) < 2 or not text.endswith('"'): return False
        body = text[1:-1]
        return (len(body) - len(body.rstrip('\\'))) % 2 == 0          # the last quote is not an escaped one
    if first.startswith("'"):
        if len(text) < 2 or not text.endswith("'"): return False
        return (len(text) - len(text[1:].rstrip("'")) - 1) % 2 == 1    # '' inside is a quote character, not the end
    return None


def entries(path, field):
    """[(id, value)] of a "- m_Id: N / <field>: value" list."""
    out, cur, lines = [], None, None
    for line in open(path, encoding='utf-8').read().split('\n'):
        if lines is not None:
            done = closed(lines)
            # a quoted text runs to its closing quote; a plain one over the lines indented deeper than its key
            if done is False or (done is None and (line.startswith('      ') or (line.strip() == '' and cur is not None))):
                lines.append(line)
                continue
            out.append((cur, scalar(lines)))
            lines = None
        m = re.match(r'^  - m_Id: (-?\d+)\s*$', line)
        if m:
            cur = int(m.group(1))
            continue
        if cur is None: continue
        m = re.match(r'^    %s:(?: (.*))?$' % field, line)
        if m: lines = [m.group(1) or '']
        elif not line.startswith('    '): cur = None
    if lines is not None: out.append((cur, scalar(lines)))
    return out


def tables(folder, lang):
    """{'HUD/BIG_MESSAGE.EXTRACTION.ACTIVATED': (english, translation or None)}, in the order of the tables."""
    base = os.path.join(folder, 'Localization', 'Tables')
    if not os.path.isdir(base): sys.exit('no Localization/Tables in ' + folder)
    out = {}
    for table in sorted(os.listdir(base)):
        d = os.path.join(base, table)
        shared = os.path.join(d, table + ' Shared Data.asset')
        if not os.path.isfile(shared): continue
        en = dict(entries(os.path.join(d, '%s_en-US.asset' % table), 'm_Localized'))
        tr_path = os.path.join(d, '%s_%s.asset' % (table, lang))
        tr = dict(entries(tr_path, 'm_Localized')) if os.path.isfile(tr_path) else {}
        for i, key in entries(shared, 'm_Key'):
            if i in en: out[table + '/' + key] = (en[i], tr.get(i) or None)
    return out


def mirror():
    cache = os.path.join(os.environ.get('RTK') or os.path.expanduser('~/.cache/rtk'), 'REPOAssets')
    if os.path.isdir(os.path.join(cache, '.git')):
        subprocess.run(['git', '-C', cache, 'pull', '-q', '--ff-only'], check=False)
    else:
        os.makedirs(os.path.dirname(cache), exist_ok=True)
        subprocess.run(['git', 'clone', '-q', '--depth', '1', MIRROR, cache], check=True)
    return cache


def flat(s):
    return s.replace('\\', '\\\\').replace('\t', '\\t').replace('\n', '\\n').replace('\r', '')


def glossary(path=None):
    """translation/official.tsv: [(english, russian, key, avoid, unless, line)]"""
    path = path or os.path.join(ROOT, 'translation', 'official.tsv')
    out = []
    for n, line in enumerate(open(path, encoding='utf-8'), 1):
        line = line.rstrip('\n').rstrip('\r')
        if not line.strip() or line.startswith('#'): continue
        c = line.split('\t') + [''] * 5
        out.append((c[0].strip(), c[1].strip(), c[2].strip(), c[3].strip(), c[4].strip(), n))
    return out


def main():
    args = sys.argv[1:]
    folder, lang = None, 'ru-RU'
    while args and args[0].startswith('--'):
        if args[0] == '--from' and len(args) > 1: folder = args[1]; args = args[2:]
        elif args[0] == '--lang' and len(args) > 1: lang = args[1]; args = args[2:]
        else: sys.exit(__doc__)
    if not args or args[0] not in ('dump', 'find', 'check'): sys.exit(__doc__)
    folder = folder or mirror()
    try:
        rev = subprocess.run(['git', '-C', folder, 'log', '-1', '--format=%h %ad', '--date=short'], capture_output=True, text=True).stdout.strip()
    except OSError: rev = ''
    texts = tables(folder, lang)
    if args[0] == 'dump':
        for key, (en, tr) in texts.items(): print('%s\t%s\t%s' % (key, flat(en), flat(tr or '')))
        print('%d texts, %d of them translated (%s); mirror at %s' % (len(texts), sum(1 for v in texts.values() if v[1]), lang, rev or '?'), file=sys.stderr)
        return 0
    if args[0] == 'find':
        if len(args) < 2: sys.exit(__doc__)
        rx = [re.compile(a, re.I) for a in args[1:]]
        for key, (en, tr) in texts.items():
            if any(r.search(en) or (tr and r.search(tr)) for r in rx): print('%s\t%s\t%s' % (key, flat(en), flat(tr or '')))
        return 0
    bad = 0
    for en, ru, key, avoid, unless, n in glossary():
        if not key: continue
        for k in key.split(','):
            k = k.strip()
            if k not in texts:
                print('official.tsv:%d: the game has no text %s any more' % (n, k)); bad += 1; continue
            have_en, have = texts[k]
            if have is None:
                print('official.tsv:%d: %s has no translation in the game' % (n, k)); bad += 1
            elif ru.lower() not in have.lower():
                print('official.tsv:%d: %s reads %r now, the table says %r' % (n, k, have, ru)); bad += 1
            elif en.lower() not in have_en.lower():
                print('official.tsv:%d: %s is %r in English now, the table says %r' % (n, k, have_en, en)); bad += 1
    # the monsters' names the add-on puts into the mod's notices (+game in translation/ru/extra.tsv)
    enemies = dict((en, tr) for key, (en, tr) in texts.items() if key.startswith('HUD/ENEMY.'))
    named = 0
    extra = os.path.join(ROOT, 'translation', 'ru', 'extra.tsv')
    for n, line in enumerate(open(extra, encoding='utf-8'), 1):
        c = line.rstrip('\n').split('\t')
        if len(c) != 3 or c[0] != '+game': continue
        named += 1
        if c[1] not in enemies: print('extra.tsv:%d: the game has no monster called %r' % (n, c[1])); bad += 1
        elif enemies[c[1]] != c[2]: print('extra.tsv:%d: %r is %r in the game, the add-on says %r' % (n, c[1], enemies[c[1]], c[2])); bad += 1
    for en in enemies:
        if not any(l.startswith('+game\t' + en + '\t') for l in open(extra, encoding='utf-8')):
            print('extra.tsv: the game has a monster the add-on does not know: %r (%r)' % (en, enemies[en])); bad += 1
    print('%d words and %d names of monsters checked against the game (%s, mirror at %s): %s'
          % (sum(1 for g in glossary() if g[2]), named, lang, rev or '?', 'all as listed' if not bad else '%d differ' % bad))
    return 1 if bad else 0


if __name__ == '__main__':
    sys.exit(main())
