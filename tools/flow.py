#!/usr/bin/env python3
"""Where does each string literal of the decompiled mod end up?

For every literal: the final consumer (looking through concatenation, formatting, the mod's own string->string helpers,
locals and StringBuilders) and a verdict:
    DISPLAY  reaches the screen
    LOG      only reaches the log / exceptions
    KEY      compared, looked up, used as an identifier
    UNKNOWN  stored or returned: may well be shown later
The patcher makes its own decisions on the real IL; this is the translator's work list.

usage: flow.py <decompiled.cs> <out.json>
"""
import json, re, sys, os, collections
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from csctx import tokenize, match_brackets, build_scopes, qualified_callee, OPEN, CLOSE
from methods import index as method_index

THROUGH_CALLS = {'string.Format', 'String.Format', 'string.Concat', 'String.Concat', 'string.Join', 'String.Join'}
THROUGH_RECV = {'ToUpperInvariant', 'ToUpper', 'ToLower', 'ToLowerInvariant', 'Trim', 'TrimEnd', 'TrimStart', 'Replace', 'Substring',
                'PadLeft', 'PadRight', 'ToString', 'Insert', 'Remove', 'Normalize'}
TEST_RECV = {'StartsWith', 'EndsWith', 'Contains', 'IndexOf', 'LastIndexOf', 'Equals', 'Split', 'CompareTo', 'IndexOfAny'}
APPENDERS = {'Append', 'AppendLine', 'AppendFormat', 'Add', 'Insert', 'AddRange', 'Enqueue', 'Push'}

LOG_RX = re.compile(r'^(Plugin\.Log\.|Log\.Log|Debug\.Log|Console\.|.*\.Log(Info|Warning|Error|Debug|Message|Fatal)$|new \w*Exception$)')
DISPLAY_RX = re.compile(r'^(GUI\.(Label|Button|Box|Toggle|TextField|RepeatButton|Window)|GUILayout\.\w+|new GUIContent|MenuAPI\.Create\w+|.*\.SetText)$')
KEY_RX = re.compile(r'^(AccessTools\.|Path\.|File\.|Directory\.|Type\.GetType|Enum\.|Shader\.|Resources\.|PlayerPrefs\.|new GameObject|new Regex|Regex\.'
                    r'|new Harmony|HarmonyPatch|DllImport|BepInPlugin|BepInDependency|BepInProcess|Traverse\.|.*\.(TryGetValue|ContainsKey|GetMethod|GetField|GetProperty|GetNestedType|GetComponent|Find|SetInt|SetFloat|SetColor|HasProperty|GetType|Invoke|RPC|InvokeRepeating|CompareTag|StartCoroutine)$'
                    r'|typeof|nameof|new Guid|int\.Parse|float\.Parse|double\.Parse|Convert\.|Encoding\.|new FieldInfo|GetManifestResourceStream|.*\.GetManifestResourceStream)')


# What reading the helpers by hand established (Type::Method(param)); "*" = any type.
SEEDS = {}
for line in """
DISPLAY RpgRuntime::Toast(text) RpgRuntime::Notify(text) RpgRuntime::Banner(text) RpgRuntime::PresetNote(text)
DISPLAY RpgRuntime::AskConfirm(title) RpgRuntime::AskConfirm(body) RpgRuntime::AskConfirm(okLabel) RpgRuntime::AskPurchase(title) RpgRuntime::AskPurchase(body)
DISPLAY RpgRuntime::SetTip(title) RpgRuntime::SetTip(body) RpgRuntime::SetTip(foot) RpgRuntime::BarAct(label) RpgRuntime::BarAct(why)
DISPLAY RpgRuntime::UBtn(label) RpgRuntime::Btn(label) RpgRuntime::AddTerm(term) RpgRuntime::Chip(label) RpgRuntime::ChipRaw(label)
DISPLAY RpgRuntime::TabDetail(emptyTitle) RpgRuntime::TabDetail(emptyText) RpgRuntime::SecondaryAction(label) RpgRuntime::NiceLabel(label)
DISPLAY RpgRuntime::ValueGroup(label) RpgRuntime::ValueGroup(value) RpgRuntime::HudChoice(label) RpgRuntime::NoteSetting(label)
KEY RpgRuntime::Notify(key) RpgRuntime::FitWidth(key) RpgRuntime::Spaced(key) RpgRuntime::EmptyState(icon) RpgRuntime::Placeholder(icon)
LOG Game::WarnOnce(msg) RpgRuntime::LcIssue(kind) RpgRuntime::LcIssue(what)
""".split('\n'):
    parts = line.split()
    for item in parts[1:]: SEEDS[item] = parts[0]


def run(path):
    src = open(path, encoding='utf-8').read()
    toks, lits, top = tokenize(src)
    partner, encl = match_brackets(toks)
    scopes = build_scopes(src)
    N = len(toks)

    # ---- token index by line, methods with their token ranges
    line_of = [0] * N
    starts = [0]
    for m in re.finditer('\n', src): starts.append(m.end())
    import bisect
    for i, t in enumerate(toks): line_of[i] = bisect.bisect_right(starts, t.pos)
    first_tok_of_line = {}
    for i in range(N - 1, -1, -1): first_tok_of_line[line_of[i]] = i

    methods = method_index(path)
    by_name = collections.defaultdict(list)
    for m in methods:
        m['short'] = m['type'].split('.')[-1] if m['type'] else ''
        i = first_tok_of_line.get(m['line'])
        m['body'] = None
        if i is not None:
            j = i
            while j < N and not (toks[j].k == 'op' and toks[j].s in ('{', ';')) : j += 1
            # expression-bodied members end with ';' after '=>'
            if j < N and toks[j].s == '{' and partner[j] > 0: m['body'] = (j, partner[j])
            elif j < N:
                m['body'] = (i, j)
        m['sparams'] = [(k, n) for k, (t, n) in enumerate(m['params']) if t.rstrip('?') == 'string']
        by_name[m['name']].append(m)
    method_at = {}
    spans = sorted([(m['body'][0], m['body'][1], m) for m in methods if m['body']], key=lambda x: x[0])
    span_starts = [s[0] for s in spans]

    def owner(i):
        k = bisect.bisect_right(span_starts, i) - 1
        while k >= 0:
            s, e, m = spans[k]
            if s <= i <= e: return m
            if e < i and k < len(spans) - 1 and spans[k][0] < i - 200000: break
            k -= 1
        return None

    def resolve(callee, argi, caller):
        """decompiled callee text + argument index -> (method, param index) or None"""
        if callee.startswith('new '): return None
        parts = callee.split('.')
        name = parts[-1]
        cands = [m for m in by_name.get(name, []) if len(m['params']) > argi]
        if not cands: return None
        if len(parts) >= 2 and parts[-2] not in ('Instance', '(…)'):
            typed = [m for m in cands if m['short'] == parts[-2]]
            if typed: cands = typed
            elif parts[-2][:1].isupper() and len(parts) == 2 and not any(m['short'] == parts[-2] for m in methods):
                # Something.Method where Something is not one of the mod's types: a field/property or a foreign type
                pass
        elif len(parts) == 1 and caller is not None:
            same = [m for m in cands if m['type'] == caller['type'] or caller['type'].startswith(m['type'] + '.')]
            if same: cands = same
        elif len(parts) >= 3 and parts[-2] == 'Instance':
            typed = [m for m in cands if m['short'] == parts[-3]]
            if typed: cands = typed
        names = {m['params'][argi][1] for m in cands}
        types = {m['params'][argi][0].rstrip('?') for m in cands}
        if len(cands) > 1 and (len(names) > 1 or len({m['type'] for m in cands}) > 1): return None
        if types != {'string'}: return None
        return (cands[0], argi)

    through = set()      # (method line, param index) : the parameter comes back in the return value

    def callee_start(o):
        k = o - 1
        if k >= 0 and toks[k].k == 'op' and toks[k].s == '>':
            d = 0
            while k >= 0:
                if toks[k].s == '>': d += 1
                elif toks[k].s == '<':
                    d -= 1
                    if d == 0: break
                k -= 1
            k -= 1
        while k >= 2 and toks[k - 1].k == 'op' and toks[k - 1].s in ('.', '?.') and toks[k - 2].k == 'id': k -= 2
        if k >= 1 and toks[k - 1].k == 'id' and toks[k - 1].s == 'new': k -= 1
        return k

    def climb(s, e, caller):
        """Consumer of the value spanning tokens s..e -> (kind, name, arg, whole)"""
        whole = True
        for _ in range(60):
            prev = toks[s - 1] if s > 0 else None
            nxt = toks[e + 1] if e + 1 < N else None
            if nxt is not None and nxt.k == 'op' and nxt.s in ('.', '?.') and e + 2 < N and toks[e + 2].k == 'id':
                name = toks[e + 2].s
                if e + 3 < N and toks[e + 3].s == '(' and partner[e + 3] >= 0:
                    if name in THROUGH_RECV:
                        if name in ('Replace', 'Substring', 'Insert', 'Remove'): whole = False
                        e = partner[e + 3]
                        continue
                    if name in TEST_RECV:
                        a0 = toks[e + 4] if e + 4 < N else None
                        if a0 is not None and (a0.k == 'chr' or (a0.k == 'str' and not a0.lit.holes and not re.search('[A-Za-z]', a0.lit.value))):
                            return ('CHECK', name, '', whole)       # looks for markup or punctuation, not for words
                        return ('TEST', name, '', whole)
                    return ('RECV', name, '', whole)
                return ('CHECK' if name == 'Length' else 'RECV', name, '', whole)
            if (prev is not None and prev.k == 'op' and prev.s in ('==', '!=')) or (nxt is not None and nxt.k == 'op' and nxt.s in ('==', '!=')):
                other = toks[e + 2] if (nxt is not None and nxt.k == 'op' and nxt.s in ('==', '!=')) else toks[s - 2]
                if (other.k == 'str' and other.lit.value == '' and not other.lit.holes) or (other.k == 'id' and other.s == 'null'):
                    return ('CHECK', 'empty', '', whole)
                return ('COMPARE', 'literal' if other.k == 'str' else '', '', whole)
            if prev is not None and prev.k == 'id' and prev.s == 'case': return ('CASE', '', '', whole)
            o = encl[s]
            if o < 0: return ('TOP', '', '', whole)
            ot = toks[o].s
            a = s - 1
            while a > o and not (encl[a] == o and toks[a].k == 'op' and toks[a].s in (',', ';')): a -= 1
            c = partner[o] if partner[o] >= 0 else N - 1
            b = e + 1
            while b < c and not (encl[b] == o and toks[b].k == 'op' and toks[b].s in (',', ';')): b += 1
            seg_s, seg_e = a + 1, b - 1
            core = seg_s
            named = ''
            if core + 1 <= seg_e and toks[core].k == 'id' and toks[core + 1].s == ':' and ot == '(':
                named = toks[core].s; core += 2
            while core + 2 <= seg_e and toks[core].s == '(' and partner[core] == core + 2 and toks[core + 1].k == 'id': core += 3
            if not (core == s and seg_e == e): whole = False
            argi = sum(1 for x in range(o + 1, s) if encl[x] == o and toks[x].k == 'op' and toks[x].s == ',')
            if ot == '(':
                cal, kind = qualified_callee(toks, o)
                if kind in ('call', 'ctor'):
                    is_through = cal in THROUGH_CALLS
                    if not is_through and kind == 'call':
                        r = resolve(cal, argi, caller)
                        if r is not None and (r[0]['line'], r[1]) in through: is_through = True
                    if is_through:
                        if cal in THROUGH_CALLS: whole = False
                        s, e = callee_start(o), partner[o]
                        if e < 0: return ('TOP', '', '', whole)
                        continue
                    return ('CALL', cal, str(argi) + ((':' + named) if named else ''), whole)
                if kind == 'stmt': return ('STMT', cal, '', whole)
                if partner[o] < 0: return ('TOP', '', '', whole)
                s, e = o, partner[o]
                continue
            if ot == '[':
                k = o - 1
                return ('INDEX', toks[k].s if k >= 0 and toks[k].k == 'id' else '', '', whole)
            close = partner[o] if partner[o] >= 0 else N - 1
            is_block = any(encl[x] == o and toks[x].k == 'op' and toks[x].s == ';' for x in range(o + 1, min(close, o + 4000)))
            if not is_block:
                if seg_e - seg_s >= 1 and toks[seg_s].k == 'id' and toks[seg_s + 1].k == 'op' and toks[seg_s + 1].s == '=':
                    return ('MEMBER', toks[seg_s].s, '', whole)
                # switch expression:  x switch { "pattern" => "result", _ => "other" }
                arrow = [x for x in range(seg_s, seg_e + 1) if encl[x] == o and toks[x].k == 'op' and toks[x].s == '=>']
                if arrow:
                    if e < arrow[0]: return ('CASE', '', '', whole)
                    return ('RETURN', 'switch', '', whole)
                k = o - 1
                if k >= 0 and toks[k].k == 'op' and toks[k].s in ('{', ','):
                    s, e = o, close          # element of a nested initializer: { { "a", 1 }, ... }
                    continue
                # what is being initialised?
                j = k; desc = []
                while j >= 0 and len(desc) < 24 and not (toks[j].k == 'op' and toks[j].s in (';', '{', '}')):
                    desc.append(toks[j].s); j -= 1
                return ('COLL', ' '.join(reversed(desc))[-120:], str(argi), whole)
            # statement
            a = s - 1
            while a > o and not (encl[a] == o and toks[a].k == 'op' and toks[a].s in (';', '{', '}')): a -= 1
            first = toks[a + 1].s if a + 1 < N else ''
            if first == 'return': return ('RETURN', '', '', whole)
            if first == 'throw': return ('THROW', '', '', whole)
            if first == 'yield': return ('RETURN', '', '', whole)
            for x in range(a + 1, s):
                if encl[x] == o and toks[x].k == 'op' and toks[x].s in ('=', '+=', '??='):
                    y = x - 1; name = []
                    while y > a:
                        if toks[y].k == 'id' and (not name or name[-1] in ('.', '?.')): name.append(toks[y].s)
                        elif toks[y].k == 'op' and toks[y].s in ('.', '?.') and name and name[-1] not in ('.', '?.'): name.append(toks[y].s)
                        else: break
                        y -= 1
                    if y > a and toks[y].s == ']': name.append('[]')
                    return ('ASSIGN', ''.join(reversed(name[:6])) if name else '?', toks[x].s, whole)
            # lambda body: x => "text"
            for x in range(a + 1, s):
                if encl[x] == o and toks[x].k == 'op' and toks[x].s == '=>': return ('RETURN', '', '', whole)
            return ('EXPR', first, '', whole)
        return ('TOP', '', '', whole)

    # ------------------------------------------------------------ verdicts
    ORDER = {'NONE': 0, 'KEY': 1, 'LOG': 2, 'UNKNOWN': 3, 'DISPLAY': 4}
    param_label = {}       # (method line, param index) -> label
    seeded = {}
    for m in methods:
        for k, n in m['sparams']:
            for key in (m['short'] + '::' + m['name'] + '(' + n + ')', '*::' + m['name'] + '(' + n + ')'):
                if key in SEEDS: seeded[(m['line'], k)] = SEEDS[key]

    ident_pos = collections.defaultdict(list)
    for i, t in enumerate(toks):
        if t.k == 'id': ident_pos[t.s].append(i)
    member_cache = {}

    def member_label(name, depth):
        """A field or property that is written here: what do its readers do with it? (by name, across the file)"""
        if name in member_cache: return member_cache[name]
        member_cache[name] = 'UNKNOWN'          # recursion guard
        best = 'NONE'; n = 0
        for i in ident_pos.get(name, ()):
            nx = toks[i + 1] if i + 1 < N else None
            if nx is not None and nx.k == 'op' and nx.s in ('=', '+=', '??=', ';', ':'): continue      # written / declared
            if nx is not None and nx.k == 'op' and nx.s in ('.', '?.') and toks[i + 2].k == 'id' and toks[i + 2].s in APPENDERS | {'Clear', 'Count', 'Length', 'RemoveAt', 'RemoveAll', 'Sort'}: continue
            s = i
            while s >= 2 and toks[s - 1].k == 'op' and toks[s - 1].s in ('.', '?.') and toks[s - 2].k == 'id': s -= 2
            n += 1
            if n > 120: best = max(best, 'UNKNOWN', key=ORDER.get); break
            lab = verdict(climb(s, i, owner(i)), owner(i), depth + 1, i)
            if ORDER[lab] > ORDER[best]: best = lab
            if best == 'DISPLAY': break
        member_cache[name] = best if n else 'UNKNOWN'
        return member_cache[name]

    def local_label(name, m, depth, skip):
        """What happens to a local variable / StringBuilder / list inside its method."""
        if m is None or m['body'] is None or depth > 2: return 'UNKNOWN'
        s, e = m['body']
        best = 'NONE'
        n = 0
        for i in range(s, e + 1):
            t = toks[i]
            if t.k != 'id' or t.s != name or i == skip: continue
            if i > 0 and toks[i - 1].k == 'op' and toks[i - 1].s in ('.', '?.'): continue
            nx = toks[i + 1] if i + 1 < N else None
            if nx is not None and nx.k == 'op' and nx.s in ('=', '+=', '??='): continue        # another definition
            if nx is not None and nx.k == 'op' and nx.s in ('.', '?.') and toks[i + 2].k == 'id' and toks[i + 2].s in APPENDERS | {'Clear', 'Length', 'Count', 'Capacity', 'Sort', 'Reverse'}: continue
            if i > 0 and toks[i - 1].k == 'id' and toks[i - 1].s in ('string', 'StringBuilder', 'var', 'out', 'ref'): continue  # declaration
            n += 1
            if n > 60: return 'UNKNOWN'
            lab = verdict(climb(i, i, m), m, depth + 1, i)
            if ORDER[lab] > ORDER[best]: best = lab
            if best == 'DISPLAY': break
        return best if n else 'UNKNOWN'

    def verdict(sink, m, depth=0, tok=None):
        kind, name, arg, whole = sink
        if kind in ('COMPARE', 'CASE', 'INDEX', 'TEST', 'STMT'): return 'KEY'
        if kind == 'CHECK': return 'NONE'
        if kind == 'THROW': return 'LOG'
        if kind == 'CALL':
            argi = int(arg.split(':')[0])
            if LOG_RX.match(name): return 'LOG'
            if name in ('string.IsNullOrEmpty', 'string.IsNullOrWhiteSpace', 'String.IsNullOrEmpty'): return 'NONE'
            if DISPLAY_RX.match(name): return 'DISPLAY'
            if name == 'c.Bind' or name.endswith('.Bind'): return 'DISPLAY' if argi == 3 else 'KEY'
            if name == 'new ConfigDescription': return 'DISPLAY' if argi == 0 else 'KEY'
            if name.endswith('.Replace'): return 'KEY' if argi == 0 else 'UNKNOWN'
            if KEY_RX.match(name): return 'KEY'
            last = name.split('.')[-1]
            if last in APPENDERS and '.' in name and not name.startswith('new '):
                recv = name.split('.')[-2]
                if recv and recv[0].islower() and recv[0] != '_' and '(' not in recv and len(name.split('.')) == 2:
                    return local_label(recv, m, depth, tok)
                return 'UNKNOWN'
            r = resolve(name, argi, m)
            if r is not None:
                key = (r[0]['line'], r[1])
                return seeded.get(key) or param_label.get(key, 'UNKNOWN')
            return 'UNKNOWN'
        if kind == 'ASSIGN':
            if name.endswith('.text') or name.endswith('.tooltip'): return 'DISPLAY'
            if name and '.' not in name and name[0].islower() and name[0] != '_' and name != '?':
                return local_label(name, m, depth, tok)
            last = name.split('.')[-1] if name else ''
            if last and last != '[]' and last != '?' and depth < 3 and (last[0] == '_' or '.' in name or last[0].isupper()) and len(last) > 2:
                return member_label(last, depth)
            return 'UNKNOWN'
        if kind == 'MEMBER' and depth < 3 and len(name) > 2:
            return member_label(name, depth)
        return 'UNKNOWN'      # RETURN, COLL, RECV, EXPR, TOP

    # ---- parameters: two rounds to learn which helpers hand their argument back, then labels to a fixpoint
    uses = {}      # (line, k) -> [sink, ...]
    def collect():
        uses.clear()
        for m in methods:
            if not m['body'] or not m['sparams']: continue
            s, e = m['body']
            names = {n: k for k, n in m['sparams']}
            for i in range(s, e + 1):
                t = toks[i]
                if t.k == 'id' and t.s in names:
                    if i > 0 and toks[i - 1].k == 'op' and toks[i - 1].s in ('.', '?.'): continue
                    nx = toks[i + 1] if i + 1 < N else None
                    if nx is not None and nx.k == 'op' and nx.s == ':' and encl[i] >= 0 and toks[encl[i]].s == '(': continue   # named argument
                    uses.setdefault((m['line'], names[t.s]), []).append((climb(i, i, m), m, i))
                elif t.k == 'str' and t.lit.holes:
                    # parameters used inside $"...{param}..."
                    for expr, _, _ in t.lit.holes:
                        for w in re.findall(r'[A-Za-z_]\w*', expr):
                            if w in names:
                                uses.setdefault((m['line'], names[w]), []).append((climb(i, i, m), m, i))
    for _ in range(3):
        collect()
        before = len(through)
        for key, lst in uses.items():
            if any(sk[0] == 'RETURN' for sk, _, _ in lst): through.add(key)
        if len(through) == before: break
    collect()
    for _ in range(12):
        changed = False
        for key, lst in uses.items():
            best = 'NONE'
            for sk, m, i in lst:
                lab = 'UNKNOWN' if sk[0] == 'RETURN' else verdict(sk, m, 0, i)
                if ORDER[lab] > ORDER[best]: best = lab
            best = seeded.get(key, best)
            if param_label.get(key) != best:
                param_label[key] = best; changed = True
        member_cache.clear()
        if not changed: break
    for key, lab in seeded.items(): param_label[key] = lab

    # ---- second question, for the patcher's rules: is the parameter used as an identifier / written to the log?
    #      A caption that is drawn and also looked up in a table must not be replaced inside the code.
    #      strong evidence: compared with a literal, switch, StartsWith/Contains/Split..., looked up in a table, file/reflection API;
    #      weak evidence:   compared with a variable (mostly "did it change?"), looked up in a text cache.
    CACHE_RX = re.compile(r'(?i)cache|^_?fit|_fitText|_ctFit|_niceLabel|_chipUpper|memo|_lineH|_seen|_once|_warned|_told')
    strong, weak, anylog, why_key = set(), set(), set(), {}
    field_cache = {}

    def field_key(name, depth):
        """A field the parameter is stored in: do its readers treat it as an identifier?"""
        if name in field_cache: return field_cache[name]
        field_cache[name] = None
        n = 0; res = None
        for i in ident_pos.get(name, ()):
            nx = toks[i + 1] if i + 1 < N else None
            if nx is not None and nx.k == 'op' and nx.s in ('=', '+=', '??=', ';', ':'): continue
            s0 = i
            while s0 >= 2 and toks[s0 - 1].k == 'op' and toks[s0 - 1].s in ('.', '?.') and toks[s0 - 2].k == 'id': s0 -= 2
            n += 1
            if n > 80: break
            m = owner(i)
            r = direct(climb(s0, i, m), m, i, depth + 1)
            if r[0] == 'KEY':
                res = 'field ' + name + ' ' + r[1]; break
        field_cache[name] = res
        return res

    def direct(sk, m, i, depth=0):
        kind, name, arg, whole = sk
        if kind == 'COMPARE': return ('KEY', 'compared with a literal') if name == 'literal' else ('WEAK', 'compared')
        if kind in ('CASE', 'TEST'): return 'KEY', kind + (':' + name if name else '')
        if kind == 'STMT': return ('KEY', 'switch') if name == 'switch' else (None, '')
        if kind == 'INDEX': return ('WEAK', 'index ' + name) if CACHE_RX.search(name or '') else ('KEY', 'index ' + name)
        if kind == 'THROW': return 'LOG', 'throw'
        if kind == 'CALL':
            argi = int(arg.split(':')[0])
            if LOG_RX.match(name): return 'LOG', name
            if name in ('string.IsNullOrEmpty', 'string.IsNullOrWhiteSpace', 'String.IsNullOrEmpty') or DISPLAY_RX.match(name): return None, ''
            if name.endswith('.Bind'): return (None, '') if argi == 3 else ('KEY', name)
            if name.endswith('.Replace'): return None, ''
            if KEY_RX.match(name):
                recv = name.rsplit('.', 1)[0]
                return ('WEAK', name) if CACHE_RX.search(recv) else ('KEY', name)
            r = resolve(name, argi, m)
            if r is not None:
                key = (r[0]['line'], r[1])
                if key in strong: return 'KEY', '-> ' + r[0]['short'] + '::' + r[0]['name'] + '(' + r[0]['params'][r[1]][1] + ')'
                if key in anylog and key not in seeded: return 'LOG', '-> ' + r[0]['name']
            return None, ''
        if kind == 'ASSIGN' and depth < 2 and name and name != '?' and m is not None and m['body']:
            if '.' not in name and name[0].islower() and name[0] != '_':
                s0, e0 = m['body']; n = 0
                for j in range(s0, e0 + 1):
                    t = toks[j]
                    if t.k != 'id' or t.s != name or j == i: continue
                    if j > 0 and toks[j - 1].k == 'op' and toks[j - 1].s in ('.', '?.'): continue
                    nx = toks[j + 1] if j + 1 < N else None
                    if nx is not None and nx.k == 'op' and nx.s in ('=', '+=', '??='): continue
                    n += 1
                    if n > 40: break
                    r = direct(climb(j, j, m), m, j, depth + 1)
                    if r[0] == 'KEY': return 'KEY', 'local ' + name + ' ' + r[1]
                return None, ''
            last = name.split('.')[-1]
            # only private fields of the same class (names such as Text / Name / Label are shared by too many types to follow)
            if last not in ('[]', '?') and len(last) > 3 and last[0] == '_' and '.' not in name:
                r = field_key(last, depth)
                if r: return 'KEY', r
        return None, ''

    for _ in range(8):
        grew = False
        field_cache.clear()
        for key, lst in uses.items():
            for sk, m, i in lst:
                lab, why = direct(sk, m, i)
                if lab == 'KEY' and key not in strong:
                    strong.add(key); why_key[key] = why; grew = True
                elif lab == 'WEAK': weak.add(key)
                elif lab == 'LOG' and key not in anylog:
                    anylog.add(key); grew = True
        if not grew: break
    anykey = strong

    # ---- "a " + x + " b": the pieces of one sentence, put back together as  a {0} b
    BOUND = {',', ';', '=', '+=', '-=', '??=', '?', ':', '??', '==', '!=', '<', '>', '<=', '>=', '&&', '||', '=>', '{', '}'}
    KW_BOUND = {'return', 'throw', 'case', 'in', 'is', 'as', 'await', 'yield', 'else'}
    chain_of = {}        # token index of a literal -> (chain id, piece index)
    chains = []

    def bound(p, step):
        L = encl[p]
        j = p + step
        lo = L
        hi = partner[L] if L >= 0 and partner[L] >= 0 else N
        while lo < j < hi:
            t = toks[j]
            if t.k == 'op' and ((step < 0 and t.s in CLOSE) or (step > 0 and t.s in OPEN)) and partner[j] >= 0:
                j = partner[j] + step
                continue
            if (t.k == 'op' and t.s in BOUND) or (t.k == 'id' and t.s in KW_BOUND): break
            j += step
        return j - step

    done_plus = set()
    for p, t in enumerate(toks):
        if not (t.k == 'op' and t.s == '+') or p in done_plus: continue
        a, b = bound(p, -1), bound(p, +1)
        L = encl[p]
        ops = []; cur = a; j = a
        while j <= b:
            tj = toks[j]
            if tj.k == 'op' and tj.s in OPEN and partner[j] >= 0 and partner[j] <= b:
                j = partner[j] + 1; continue
            if tj.k == 'op' and tj.s == '+' and encl[j] == L:
                done_plus.add(j)
                ops.append((cur, j - 1)); cur = j + 1
            j += 1
        ops.append((cur, b))
        parts = []      # ('L', token index) | ('H', (s, e))
        for (s0, e0) in ops:
            if s0 > e0: parts = []; break
            if s0 == e0 and toks[s0].k == 'str' and not toks[s0].lit.holes: parts.append(('L', s0))
            else: parts.append(('H', (s0, e0)))
        lits = [x for k, x in parts if k == 'L']
        if not lits or len(lits) == len(parts): continue
        if not any(re.search('[A-Za-z]', toks[x].lit.value) for x in lits): continue
        pat = []; hole = 0; last_hole = False; pieces = []
        for k, x in parts:
            if k == 'L':
                v = toks[x].lit.value
                pat.append(v.replace('{', '{{').replace('}', '}}')); pieces.append(v); last_hole = False
            else:
                if not last_hole:
                    pat.append('{%d}' % hole); hole += 1; pieces.append(None)
                last_hole = True
        cid = len(chains)
        for n, x in enumerate(lits): chain_of[x] = (cid, n)
        chains.append({'pattern': ''.join(pat), 'pieces': pieces, 'line': line_of[lits[0]], 'tok': lits[0], 'span': (a, b)})

    # literals sitting inside a hole of a chain ( "Cost: " + (free ? "free" : price) ) are shown together with it
    inside = {}
    for cid, ch in enumerate(chains):
        a, b = ch['span']
        for j in range(a, b + 1):
            if toks[j].k == 'str' and j not in chain_of: inside[j] = cid

    # ---- literals
    out = []
    for i, t in enumerate(toks):
        if t.k != 'str': continue
        L = t.lit
        m = owner(i)
        sink = climb(i, i, m)
        lab = verdict(sink, m, 0, i)
        sc = scopes[L.line] or ((), None, None)
        rec = {'line': L.line, 'value': L.value, 'types': list(sc[0]), 'member': sc[1], 'mkind': sc[2],
               'sink': [sink[0], sink[1], sink[2]], 'whole': sink[3], 'use': lab, 'interp': bool(L.holes)}
        if i in chain_of: rec['chain'], rec['piece'] = chain_of[i]
        elif i in inside: rec['inside'] = inside[i]
        out.append(rec)
    for ch in chains:
        del ch['tok']; del ch['span']
    params = []
    for m in methods:
        for k, n in m['sparams']:
            params.append({'type': m['type'], 'method': m['name'], 'line': m['line'], 'param': n, 'index': k,
                           'label': param_label.get((m['line'], k), 'NONE'), 'through': (m['line'], k) in through,
                           'anykey': (m['line'], k) in anykey, 'weakkey': (m['line'], k) in weak, 'anylog': (m['line'], k) in anylog,
                           'whykey': why_key.get((m['line'], k), '')})
    return out, params, chains


if __name__ == '__main__':
    lits, params, chains = run(sys.argv[1])
    json.dump({'literals': lits, 'params': params, 'chains': chains}, open(sys.argv[2], 'w', encoding='utf-8'), ensure_ascii=False)
    print('chains', len(chains), 'literals in chains', sum(1 for l in lits if 'chain' in l))
    c = collections.Counter(l['use'] for l in lits if re.search('[A-Za-z]{2,}', l['value']))
    print('literals', len(lits), dict(c))
    print('params', collections.Counter(p['label'] for p in params), 'through', sum(p['through'] for p in params))
