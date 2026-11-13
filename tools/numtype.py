#!/usr/bin/env python3
"""Is the expression that fills a placeholder a number?

"{0} players" is made as  $"{room.PlayerCount} players"; "{0} gear" as  rarity.name + " gear". On the screen both are
"<something> <word>", and a text nobody translated ("more players", "Spawn gear") fits both. The first one can be told
apart: what fills it is a number, so anything with a letter in it is another sentence. This module reads the decompiled
code the way a person would: names of numeric locals and parameters, fields declared as numbers, .Count / .Length,
arithmetic, casts, Mathf helpers, ToString("0.#").

Deliberately one-sided: True only when every reading of the expression is a number. Unknown stays False.
"""
import re

NUMERIC = {'int', 'float', 'double', 'long', 'uint', 'ulong', 'short', 'ushort', 'byte', 'sbyte', 'decimal'}
NUM_T = '(?:' + '|'.join(sorted(NUMERIC)) + ')'
MEMBER = re.compile(r'^\t+(?:\[[^\]]+\]\s*)*(?:(?:public|internal|private|protected|static|readonly|const|volatile|new|override|virtual|sealed|abstract)\s+)+'
                    r'([\w<>\[\]?,.]+)\s+(\w+)\s*(?:;|=|\{|=>|\()', re.M)
NUM_FMT = re.compile(r'^"[0#.,;+\-% NFPDnfpd0-9]*"$')          # not X (hex) and E (exponent): those print letters
NUM_CALL = re.compile(r'^(?:Mathf|Math|Convert)\.(?:RoundToInt|FloorToInt|CeilToInt|Abs|Max|Min|Clamp|Clamp01|Round|Floor|Ceil|Ceiling|Sqrt|Pow|Sign|Lerp|'
                      r'ToInt32|ToInt64|ToSingle|ToDouble)$')
LITERAL = re.compile(r'^-?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?[fFdDmMuUlL]*$')
IDENT = re.compile(r'^[A-Za-z_]\w*$')


class Types:
    """Every field, property and method of the mod by name: the set of types declared under that name."""

    def __init__(self, src):
        self.members = {}
        for m in MEMBER.finditer(src):
            t, n = m.group(1), m.group(2)
            if t in ('return', 'new', 'throw', 'else', 'case', 'using', 'namespace', 'class', 'struct', 'enum', 'interface', 'delegate', 'event'): continue
            self.members.setdefault(n, set()).add(t.rstrip('?'))

    def member_is_number(self, name):
        ts = self.members.get(name)
        return bool(ts) and ts <= NUMERIC


def strip_parens(e):
    e = e.strip()
    while e.startswith('(') and e.endswith(')'):
        depth = 0
        for i, c in enumerate(e):
            if c == '(': depth += 1
            elif c == ')':
                depth -= 1
                if depth == 0 and i < len(e) - 1: return e        # "(a) + (b)": the first one closes early
        e = e[1:-1].strip()
    return e


def top_level(e, seps):
    """Positions of the separators that are not inside brackets or strings; seps are single characters."""
    out = []; depth = 0; i = 0; n = len(e)
    while i < n:
        c = e[i]
        if c == '"':
            i += 1
            while i < n and e[i] != '"':
                if e[i] == '\\': i += 1
                i += 1
        elif c == "'":
            i += 1
            while i < n and e[i] != "'":
                if e[i] == '\\': i += 1
                i += 1
        else:
            if depth == 0 and c in seps: out.append(i)
            if c in '([{': depth += 1
            elif c in ')]}': depth -= 1
        i += 1
    return out


def is_number(expr, local, types, depth=0):
    """local: names declared as numbers in the method (locals and parameters); types: Types of the whole file."""
    if depth > 6: return False
    e = strip_parens(expr)
    if not e or '"' in e and not re.search(r'\.ToString\(', e): return False
    if LITERAL.match(e): return True

    # a ? b : c  - a number when both branches are
    q = top_level(e, '?')
    if q and not e[q[0] + 1:q[0] + 2] in ('?', '.', '['):
        colon = [i for i in top_level(e, ':') if i > q[0]]
        if colon:
            return is_number(e[q[0] + 1:colon[0]], local, types, depth + 1) and is_number(e[colon[0] + 1:], local, types, depth + 1)
        return False
    if top_level(e, '?'): return False                              # ?? and ?. chains: may be anything

    # (int)x, (float)(a / b)
    m = re.match(r'^\(\s*(' + NUM_T + r')\s*\)', e)
    if m: return True

    # a - b, a * b, a / b, a % b, a + b: numbers when each side is
    ops = [i for i in top_level(e, '+-*/%') if i > 0 and e[i - 1] not in '+-*/%(eE' or (i > 0 and e[i - 1] in 'eE' and not e[i - 2:i - 1].isdigit())]
    ops = [i for i in ops if e[:i].strip()]
    if ops:
        parts = []; last = 0
        for i in ops:
            parts.append(e[last:i]); last = i + 1
        parts.append(e[last:])
        return all(p.strip() and is_number(p, local, types, depth + 1) for p in parts)
    if e[0] in '-+': return is_number(e[1:], local, types, depth + 1)

    # x.ToString("0.#"), x.ToString("N0", culture)
    m = re.match(r'^(.*)\.ToString\((.*)\)$', e, re.S)
    if m:
        args = m.group(2).strip()
        first = args[:top_level(args, ',')[0]] if top_level(args, ',') else args
        if NUM_FMT.match(first.strip()): return True
        if args == '' or args.startswith('CultureInfo'): return is_number(m.group(1), local, types, depth + 1)
        return False

    # a call: Mathf.RoundToInt(...), Leveling.XpToNext(...), list.Count()
    if e.endswith(')'):
        opens = top_level(e, '(')
        if not opens: return False
        callee = e[:opens[0]].strip()
        if NUM_CALL.match(callee): return True
        name = callee.split('.')[-1]
        if name in ('Count', 'Sum', 'IndexOf', 'LastIndexOf', 'CompareTo'): return True
        return IDENT.match(name) is not None and types.member_is_number(name)

    # a[i]: what the collection holds is not known
    if e.endswith(']'): return False

    name = e.split('.')[-1]
    if not IDENT.match(name): return False
    if '.' not in e:
        if name in local: return True
        if name in local.get('__other__', ()): return False
        return types.member_is_number(name)
    if name in ('Count', 'Length'): return True
    if name in ('PlayerCount', 'ActorNumber', 'childCount', 'frameCount'): return True
    return types.member_is_number(name)


# "int num = ...", "List<string> list;", "foreach (KeyValuePair<string, int> item in ...)", "(ClassDef c) =>" - but not "num13, num15"
DECL_LOCAL = re.compile(r'(?<![\w.])([A-Za-z_][\w.]*(?:<[^<>;(){}=]*(?:<[^<>;(){}=]*>[^<>;(){}=]*)*>)?(?:\[,*\])*\??)\s+([A-Za-z_]\w*)\s*(?==[^=]|;|,|\)|\bin\b)')


def locals_of(signature_params, body):
    """Names declared in a method: numbers, and (under '__other__') everything else that was declared."""
    nums = {}; other = set()
    for t, n in signature_params:
        if t.rstrip('?') in NUMERIC: nums[n] = True
        else: other.add(n)
    for m in DECL_LOCAL.finditer(body):
        t, n = m.group(1).rstrip('?'), m.group(2)
        if t in ('return', 'new', 'throw', 'else', 'case', 'in', 'is', 'as', 'out', 'ref', 'await', 'yield', 'goto', 'using'): continue
        if t in NUMERIC:
            if n not in other: nums[n] = True
        elif re.match(r'^[A-Za-z_]', t) and t not in ('var',):
            other.add(n); nums.pop(n, None)
    nums['__other__'] = other
    return nums


if __name__ == '__main__':
    t = Types('')
    t.members = {'rewardXp': {'int'}, 'name': {'string'}, 'level': {'int', 'float'}, 'XpToNext': {'int'}, 'Price': {'string'}}
    loc = {'num': True, 'count': True, '__other__': {'text'}}
    for e, want in [('num', True), ('text', False), ('c.rewardXp', True), ('rd.name', False), ('list.Count', True), ('(float)x / 3f', True),
                    ('num.ToString("0.#")', True), ('x.ToString()', False), ('num.ToString()', True), ('Mathf.RoundToInt(a * 100f)', True),
                    ('(num == 1) ? "" : "s"', False), ('flag ? num : count', True), ('count - 1', True), ('Scrap.Price(num)', False),
                    ('Leveling.XpToNext(i)', True), ('PhotonNetwork.CurrentRoom.PlayerCount', True), ('p.level', True), ('a ?? b', False),
                    ('array[0]', False), ('-num', True), ('num * 100f', True), ('x.Value', False), ('5', True), ('1.5f', True),
                    ('d.ToString("0.####", CultureInfo.InvariantCulture)', True), ('Elites.TierName(info.Tier)', False)]:
        got = is_number(e, loc, t)
        print('ok  ' if got == want else 'BAD ', e, got)
