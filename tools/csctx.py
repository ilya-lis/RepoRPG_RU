"""Context analyzer for ILSpy-decompiled C#: for every string literal find scope (type chain + member)
and 'sink' (enclosing call / assignment / return / case / initializer)."""
import re, sys, bisect, json
from cstok import scan

TOKEN_RE = re.compile(r'''
    (?P<ws>\s+)
  | (?P<id>@?[A-Za-z_][A-Za-z_0-9]*)
  | (?P<num>(?:0[xX][0-9a-fA-F]+|\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)[fFdDmMuUlL]*)
  | (?P<op>=>|==|!=|<=|>=|&&|\|\||\?\?=|\?\?|\?\.|\+\+|--|\+=|-=|\*=|/=|%=|&=|\|=|\^=|<<=|>>=|<<|->|::|[{}()\[\];,.:?=+\-*/%&|^!~<>@$\#\\])
''', re.X)

class Tok:
    __slots__=('k','s','pos','lit')
    def __init__(s,k,t,pos,lit=None): s.k=k; s.s=t; s.pos=pos; s.lit=lit
    def __repr__(s): return f"{s.k}:{s.s!r}"

def tokenize(src):
    """token stream; string literals are single tokens (k='str'); interpolated-string holes are NOT expanded
    (nested literals inside holes are reported separately by scan but share the parent's sink)."""
    lits=scan(src)
    top=[]; last_end=-1
    for L in lits:
        if L.start>=last_end:
            top.append(L); last_end=L.end
    toks=[]; i=0; n=len(src); li=0
    while i<n:
        if li<len(top) and top[li].start==i:
            L=top[li]; toks.append(Tok('str',src[L.start:L.end],i,L)); i=L.end; li+=1; continue
        c=src[i]
        if c=='/' and src.startswith('//',i):
            j=src.find('\n',i); i=n if j<0 else j; continue
        if c=='/' and src.startswith('/*',i):
            j=src.find('*/',i+2); i=n if j<0 else j+2; continue
        if c=="'":
            j=i+1
            if src[j]=='\\':
                j+=2
                while src[j]!="'": j+=1
            else: j+=1
            toks.append(Tok('chr',src[i:j+1],i)); i=j+1; continue
        m=TOKEN_RE.match(src,i)
        if not m: raise ValueError(f"tokenize error at {i}: {src[i:i+30]!r}")
        if m.lastgroup!='ws': toks.append(Tok(m.lastgroup,m.group(0),i))
        i=m.end()
    return toks, lits, top

OPEN={'(' :')','[':']','{':'}'}
CLOSE={')':'(',']':'[','}':'{'}

def match_brackets(toks):
    """return arrays: partner index for brackets, and for each token the index of its enclosing open bracket (-1)."""
    partner=[-1]*len(toks); encl=[-1]*len(toks); st=[]
    for i,t in enumerate(toks):
        if t.k=='op' and t.s in OPEN:
            encl[i]=st[-1] if st else -1; st.append(i)
        elif t.k=='op' and t.s in CLOSE:
            if st and toks[st[-1]].s==CLOSE[t.s]:
                o=st.pop(); partner[o]=i; partner[i]=o
            encl[i]=st[-1] if st else -1
        else:
            encl[i]=st[-1] if st else -1
    return partner, encl

KEYWORDS_STMT={'if','while','for','foreach','switch','catch','using','lock','fixed','when'}
TYPE_DECL=re.compile(r'^(\t+)(?:\[[^\]]*\]\s*)*(?:(?:public|internal|private|protected|static|sealed|abstract|partial|readonly|unsafe|ref|new)\s+)*(class|struct|enum|interface)\s+([A-Za-z_]\w*)')
MODS={'public','internal','private','protected','static','sealed','abstract','partial','readonly','unsafe','ref','new','override','virtual','extern','async','volatile','const','event','implicit','explicit','operator'}

def build_scopes(src):
    """returns list per line (1-based index): (type_chain tuple, member_name, member_kind)"""
    lines=src.split('\n'); out=[None]*(len(lines)+2)
    stack=[]  # (indent, name)
    cur=(None,None)  # member name, kind
    cur_indent=-1
    for idx,line in enumerate(lines):
        ln=idx+1
        stripped=line.lstrip('\t'); ind=len(line)-len(stripped)
        if not stripped:
            out[ln]=(tuple(n for _,n in stack),cur[0],cur[1]); continue
        m=TYPE_DECL.match(line)
        if m and not stripped.startswith('//'):
            k=len(m.group(1))
            while stack and stack[-1][0]>=k: stack.pop()
            stack.append((k,m.group(3))); cur=(None,None); cur_indent=-1
            out[ln]=(tuple(n for _,n in stack),None,None); continue
        # closing of types
        while stack and ind<stack[-1][0]: stack.pop(); cur=(None,None)
        if stack and ind==stack[-1][0] and stripped.startswith('}'):
            out[ln]=(tuple(n for _,n in stack),cur[0],cur[1])
            stack.pop(); cur=(None,None); cur_indent=-1
            continue
        if stack and ind==stack[-1][0]+1 and stripped[0] not in '{}[/' :
            # member declaration line
            name,kind=parse_member(stripped, stack[-1][1])
            cur=(name,kind); cur_indent=ind
        out[ln]=(tuple(n for _,n in stack),cur[0],cur[1])
    return out

def parse_member(s, typename):
    toks=re.findall(r'[A-Za-z_]\w*|[^\sA-Za-z_]',s)
    mods=set()
    i=0
    while i<len(toks) and toks[i] in MODS:
        mods.add(toks[i]); i+=1
    static = 'static' in mods or 'const' in mods
    # a tuple type in front of the name: "(string, string)[] Names = ..." - step over it
    while i<len(toks) and toks[i]=='(':
        d=0; j=i
        while j<len(toks):
            if toks[j]=='(': d+=1
            elif toks[j]==')':
                d-=1
                if d==0: break
            j+=1
        i=j+1
        while i<len(toks) and toks[i] in '[]?,': i+=1
    # find first '(' or '=' or '=>' or '{' at top level (skip generics <>)
    depth=0; j=i; first=None
    while j<len(toks):
        t=toks[j]
        if t=='<': depth+=1
        elif t=='>' and depth>0: depth-=1
        elif depth==0 and t in '(={;':
            first=t; break
        j+=1
    if first=='(':
        # name is identifier before '(' (skip generic params)
        k=j-1
        if toks[k]=='>':
            d=0
            while k>=0:
                if toks[k]=='>': d+=1
                elif toks[k]=='<':
                    d-=1
                    if d==0: break
                k-=1
            k-=1
        name=toks[k] if k>=0 else '?'
        if name==typename and (k==i):  # constructor
            return ('.cctor' if 'static' in mods else '.ctor','ctor')
        if k>=1 and toks[k-1]=='~': return ('Finalize','method')
        if 'operator' in mods or (k>=1 and toks[k-1]=='operator'): return ('op_'+name,'method')
        if name=='this': return ('Item','property')
        return (name,'method')
    if 'const' in mods:
        k=j-1; return ('const:'+toks[k],'const')
    if first=='=' and not (j+1<len(toks) and toks[j+1]=='>'):
        # field with initializer (or auto-property initializer handled below)
        return ('.cctor' if static else '.ctor','fieldinit:'+toks[j-1])
    if first=='=' :  # '=>' expression-bodied property
        return (toks[j-1],'property')
    if first=='{':
        # auto-property on one line: "public int X { get; set; } = 5;" or just accessor decl
        name=toks[j-1]
        if '=' in toks[j:]: return ('.cctor' if static else '.ctor','fieldinit:'+name)
        return (name,'property')
    if first==';':
        return ('.cctor' if static else '.ctor','field:'+toks[j-1])
    # property/event header without braces on the line
    name=toks[-1] if toks else '?'
    if name=='this' or (len(toks)>=2 and toks[-1]==']'): return ('Item','property')
    return (name,'property')

def qualified_callee(toks, o):
    """o = index of '(' ; return (callee string, kind) or (None, why)"""
    k=o-1
    if k<0: return None,'none'
    t=toks[k]
    if t.k=='op' and t.s=='>':
        d=0
        while k>=0:
            s=toks[k].s
            if toks[k].k=='op' and s=='>': d+=1
            elif toks[k].k=='op' and s=='<':
                d-=1
                if d==0: break
            elif toks[k].k=='op' and s in ';{}': return None,'expr'
            k-=1
        k-=1
        if k<0: return None,'expr'
        t=toks[k]
    if t.k!='id': return None,'expr'
    if t.s in KEYWORDS_STMT: return t.s,'stmt'
    if t.s in ('return','throw','in','is','as','await','else','case','yield'): return None,'expr'
    name=[t.s]; k2=k-1
    while k2>=1 and toks[k2].k=='op' and toks[k2].s in ('.','?.') and toks[k2-1].k=='id':
        name.append(toks[k2-1].s); k2-=2
    # 'new Type(' ?
    if k2>=0 and toks[k2].k=='id' and toks[k2].s=='new':
        return 'new '+'.'.join(reversed(name)),'ctor'
    recv_complex = k2>=0 and toks[k2].k=='op' and toks[k2].s in ('.','?.')
    q='.'.join(reversed(name))
    if recv_complex: q='(…).'+q
    return q,'call'

def analyze(src):
    toks,lits,top=tokenize(src)
    partner,encl=match_brackets(toks)
    scopes=build_scopes(src)
    res=[]
    for i,t in enumerate(toks):
        if t.k!='str': continue
        L=t.lit
        sc=scopes[L.line] or ((),None,None)
        info={'line':L.line,'value':L.value,'kind':L.kind,'types':list(sc[0]),'member':sc[1],'mkind':sc[2]}
        prev=toks[i-1] if i>0 else None; nxt=toks[i+1] if i+1<len(toks) else None
        sink=None
        if (prev and prev.k=='op' and prev.s in ('==','!=')) or (nxt and nxt.k=='op' and nxt.s in ('==','!=')):
            sink=('COMPARE','','')
        elif prev and prev.k=='id' and prev.s=='case':
            sink=('CASE','','')
        elif nxt and nxt.k=='op' and nxt.s in ('.','?.') and (i+2<len(toks)):
            sink=('RECV',toks[i+2].s,'')   # "literal".Method(...)
        pos=i; o=encl[i]; whole=True; hops=0
        while sink is None:
            if o<0: sink=('TOP','',''); break
            ot=toks[o].s
            # segment bounds at depth o
            a=pos-1
            while a>o and not (encl[a]==o and toks[a].k=='op' and toks[a].s in (',',';')): a-=1
            c=partner[o] if partner[o]>=0 else len(toks)-1
            b=pos+1
            while b<c and not (encl[b]==o and toks[b].k=='op' and toks[b].s in (',',';')): b+=1
            seg=toks[a+1:b]
            argi=sum(1 for x in range(o+1,pos) if encl[x]==o and toks[x].k=='op' and toks[x].s==',')
            if hops==0:
                core=[x for x in seg]
                # strip named arg prefix and simple casts
                if len(core)>=2 and core[0].k=='id' and core[1].s==':' : core=core[2:]
                while len(core)>=4 and core[0].s=='(' and core[2].s==')' and core[1].k=='id': core=core[3:]
                whole = (len(core)==1 and core[0] is t)
            if ot=='(':
                cal,kind=qualified_callee(toks,o)
                if kind in ('call','ctor'):
                    named=seg[0].s if (len(seg)>=2 and seg[0].k=='id' and seg[1].s==':') else ''
                    sink=('CALL',cal,f"{argi}" + (f":{named}" if named else '')); break
                if kind=='stmt': sink=('STMT',cal,''); break
                pos=o; o=encl[o]; hops+=1; continue   # parenthesized / tuple / cast -> climb
            if ot=='[':
                k=o-1
                recv=toks[k].s if k>=0 and toks[k].k=='id' else ''
                sink=('INDEX',recv,''); break
            # '{'
            close=partner[o] if partner[o]>=0 else len(toks)-1
            is_block=any(encl[x]==o and toks[x].k=='op' and toks[x].s==';' for x in range(o+1,close))
            if not is_block:
                # initializer
                if len(seg)>=2 and seg[0].k=='id' and seg[1].k=='op' and seg[1].s=='=':
                    # object initializer member; find 'new Type' before '{'
                    k=o-1; ty=''
                    if k>=0 and toks[k].s==')': k=partner[k]-1
                    if k>=0 and toks[k].k=='op' and toks[k].s=='>':
                        d=0
                        while k>=0:
                            if toks[k].s=='>': d+=1
                            elif toks[k].s=='<':
                                d-=1
                                if d==0: break
                            k-=1
                        k-=1
                    if k>=0 and toks[k].k=='id': ty=toks[k].s
                    sink=('MEMBER',seg[0].s,ty); break
                # collection element: find what's before '{'
                k=o-1; desc=[]
                # nested element braces -> climb to parent initializer but remember index
                if k>=0 and toks[k].k=='op' and toks[k].s in ('{',','):
                    sink_idx=argi; pos=o; o=encl[o]; hops+=1
                    # continue loop but mark as nested element
                    info.setdefault('nest',[]).append(sink_idx); continue
                j=k
                while j>=0 and len(desc)<40 and not (toks[j].k=='op' and toks[j].s in (';','{','}')) :
                    desc.append(toks[j].s); j-=1
                desc=' '.join(reversed(desc))
                sink=('COLL',desc[-160:],str(argi)); break
            # statement block: find statement start
            a=pos-1
            while a>o and not (encl[a]==o and toks[a].k=='op' and toks[a].s in (';','{','}',':')): a-=1
            st=toks[a+1:pos+1]
            words=[x.s for x in st[:8]]
            if words and words[0]=='return': sink=('RETURN','',''); break
            if words and words[0]=='throw': sink=('THROW','',''); break
            # assignment: find first '=' / '+=' at this depth in statement before literal
            tgt=None
            for x in range(a+1,pos+1):
                if encl[x]==o and toks[x].k=='op' and toks[x].s in ('=','+=','??='):
                    y=x-1
                    name=[]
                    while y>a and (toks[y].k=='id' or (toks[y].k=='op' and toks[y].s in ('.','?.'))):
                        name.append(toks[y].s); y-=1
                    if y>a and toks[y].s==']':  # indexer assign x["k"] = ...
                        name.append('[]')
                    tgt=''.join(reversed(name[:6])) if name else '?'
                    sink=('ASSIGN',tgt,toks[x].s); break
            if sink is None: sink=('EXPR',' '.join(words[:4]),'')
            break
        info['sink']=list(sink); info['whole']=whole; info['hops']=hops
        res.append(info)
    # nested literals inside interpolation holes: attach as separate entries with parent's sink
    topset={id(x.lit) for x in toks if x.k=='str'}
    return res, lits

if __name__=='__main__':
    src=open(sys.argv[1],encoding='utf-8').read()
    res,lits=analyze(src)
    json.dump(res,open(sys.argv[2],'w',encoding='utf-8'),ensure_ascii=False)
    print('top-level literals',len(res),'all literals',len(lits))
