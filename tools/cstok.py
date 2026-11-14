"""Minimal C# tokenizer for ILSpy output: extracts string literals with position/context.
Handles: "..." regular, @"..." verbatim, $"..." interpolated (nested), $@"..." / @$"...", char literals, // and /* */ comments."""
import re, sys

ESC = {'n':'\n','t':'\t','r':'\r','0':'\0','\\':'\\','"':'"',"'":"'",'a':'\a','b':'\b','f':'\f','v':'\v'}

def unescape_regular(body):
    out=[];i=0;n=len(body)
    while i<n:
        c=body[i]
        if c=='\\':
            i+=1;d=body[i]
            if d in ESC: out.append(ESC[d]); i+=1
            elif d=='u':
                out.append(chr(int(body[i+1:i+5],16))); i+=5
            elif d=='U':
                out.append(chr(int(body[i+1:i+9],16))); i+=9
            elif d=='x':
                m=re.match(r'[0-9a-fA-F]{1,4}',body[i+1:]); out.append(chr(int(m.group(0),16))); i+=1+len(m.group(0))
            else: raise ValueError('bad escape \\'+d)
        else: out.append(c); i+=1
    return ''.join(out)

class Lit:
    __slots__=('kind','start','end','line','value','parts','holes','raw')
    def __repr__(self): return f"Lit({self.kind},{self.line},{self.value!r})"

def scan(src):
    """yield Lit objects for every string literal in src (top-level and nested inside interpolation holes)."""
    lits=[]
    n=len(src)
    # precompute line starts
    def parse_code(i, stop_chars=None, depth=0):
        """scan code from i until end or (if stop_chars) an unbalanced stop char at nesting 0. returns index."""
        par=0
        while i<n:
            c=src[i]
            if stop_chars is not None and par==0 and c in stop_chars:
                return i
            if c=='/' and i+1<n and src[i+1]=='/':
                j=src.find('\n',i); i = n if j<0 else j; continue
            if c=='/' and i+1<n and src[i+1]=='*':
                j=src.find('*/',i+2); i = n if j<0 else j+2; continue
            if c=="'":
                # char literal
                j=i+1
                if src[j]=='\\':
                    j+=2
                    while src[j]!="'": j+=1
                else: j+=1
                assert src[j]=="'", (src[i:i+20])
                i=j+1; continue
            if c=='"':
                i=parse_string(i,'regular'); continue
            if c=='@' and i+1<n and src[i+1]=='"':
                i=parse_string(i,'verbatim'); continue
            if c=='$' and i+1<n and src[i+1]=='"':
                i=parse_string(i,'interp'); continue
            if c=='$' and src[i+1:i+3]=='@"' or c=='@' and src[i+1:i+3]=='$"':
                i=parse_string(i,'interp_verbatim'); continue
            if stop_chars is not None:
                if c in '([{': par+=1
                elif c in ')]}': par-=1
            i+=1
        return i
    def parse_string(i,kind):
        L=Lit(); L.kind=kind; L.start=i; L.parts=None; L.holes=None
        if kind=='regular':
            j=i+1
            while True:
                c=src[j]
                if c=='\\': j+=2; continue
                if c=='"': break
                if c=='\n': raise ValueError('newline in regular string at %d'%i)
                j+=1
            L.raw=src[i+1:j]; L.value=unescape_regular(L.raw); L.end=j+1
        elif kind=='verbatim':
            j=i+2
            while True:
                c=src[j]
                if c=='"':
                    if src[j+1]=='"': j+=2; continue
                    break
                j+=1
            L.raw=src[i+2:j]; L.value=L.raw.replace('""','"'); L.end=j+1
        else:
            verb = kind=='interp_verbatim'
            j=i+(3 if verb else 2)
            parts=[];holes=[];cur=[]
            while True:
                c=src[j]
                if c=='{':
                    if src[j+1]=='{': cur.append('{'); j+=2; continue
                    # hole
                    k=parse_code(j+1, stop_chars='}:,')
                    # careful: ':' or ',' may be inside ternary/generic; ILSpy wraps ternaries in parens. accept.
                    expr=src[j+1:k]
                    fmt='';align=''
                    if src[k]==',':
                        k2=k+1
                        while src[k2] not in ':}': k2+=1
                        align=src[k+1:k2].strip(); k=k2
                    if src[k]==':':
                        k2=k+1
                        while src[k2]!='}': k2+=1
                        fmt=src[k+1:k2]; k=k2
                    assert src[k]=='}', src[j:j+80]
                    parts.append(''.join(cur)); cur=[]
                    holes.append((expr,align,fmt))
                    j=k+1; continue
                if c=='}':
                    assert src[j+1]=='}', src[j-40:j+40]
                    cur.append('}'); j+=2; continue
                if verb:
                    if c=='"':
                        if src[j+1]=='"': cur.append('"'); j+=2; continue
                        break
                    cur.append(c); j+=1; continue
                if c=='\\':
                    d=src[j+1]
                    if d in ESC: cur.append(ESC[d]); j+=2
                    elif d=='u': cur.append(chr(int(src[j+2:j+6],16))); j+=6
                    elif d=='x':
                        m=re.match(r'[0-9a-fA-F]{1,4}',src[j+2:]); cur.append(chr(int(m.group(0),16))); j+=2+len(m.group(0))
                    else: raise ValueError('bad esc')
                    continue
                if c=='"': break
                cur.append(c); j+=1
            parts.append(''.join(cur))
            L.parts=parts; L.holes=holes; L.end=j+1
            L.raw=src[i:j+1]
            # reconstructed composite-format string: the compiler numbers every hole in turn, also when the same expression
            # stands in two of them ($"{a} and {a}" is "{0} and {1}" in the DLL)
            fs=[]
            for n,(p,(e,a,f)) in enumerate(zip(parts,holes)):
                fs.append(p.replace('{','{{').replace('}','}}'))
                h='{'+str(n)
                if a: h+=','+a
                if f: h+=':'+f
                fs.append(h+'}')
            fs.append(parts[-1].replace('{','{{').replace('}','}}'))
            L.value=''.join(fs)
        lits.append(L)
        return L.end
    parse_code(0)
    lits.sort(key=lambda l:l.start)
    # line numbers
    import bisect
    starts=[0]
    for m in re.finditer('\n',src): starts.append(m.end())
    for L in lits: L.line=bisect.bisect_right(starts,L.start)
    return lits

if __name__=='__main__':
    src=open(sys.argv[1],encoding='utf-8').read()
    lits=scan(src)
    from collections import Counter
    print(len(lits), Counter(l.kind for l in lits))
