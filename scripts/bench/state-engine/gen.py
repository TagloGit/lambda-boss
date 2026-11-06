"""Generate the lb386 GetVar hot-loop benchmark inputs for the VM.
Writes <serve>/lb386-names.tsv (name \t refersTo) and <serve>/lb386-cases.tsv (id \t formula)."""
import os, re, sys

LB = sys.argv[1]      # lambda-boss worktree
SERVE = sys.argv[2]   # vmps serve dir


def body(path):
    src = open(path, encoding='utf-8').read()
    src = re.sub(r'/\*.*?\*/', '', src, flags=re.S)
    src = re.sub(r'^\s*//.*$', '', src, flags=re.M)
    i = src.index('LAMBDA(')
    j = src.rindex(')')
    return '=' + re.sub(r'\s*\n\s*', ' ', src[i:j + 1]).strip()


def lean(text):
    # Drop the Help binding and the Help? branch: LET(Help, TEXTSPLIT(...), ... IF(Help?, Help, result)
    text = re.sub(r'LET\(Help, TEXTSPLIT\(.*?"→", "¶" \),', 'LET(', text, flags=re.S)
    text = text.replace('IF(Help?, Help, result)', 'result').replace('IF(Help?, result, Help)', 'result')
    text = re.sub(r'Help\?, ISOMITTED\([^)]*\),', '', text)
    return text


names = {}
for n in ['SetVars', 'SetVar', 'GetVar', 'NewState']:
    names[n] = body(os.path.join(LB, 'lambdas', 'loops', n + '.lambda'))
# Lean copies: same code with the Help block removed (tests whether Help costs per call).
for n in ['SetVars', 'SetVar', 'GetVar', 'NewState']:
    t = lean(names[n])
    for m in ['SetVars', 'SetVar', 'GetVar', 'NewState']:
        t = re.sub(r'\b%s\(' % m, m + 'L(', t)
    names[n + 'L'] = t
# Lazy-help copies: Help text moved from a LET binding into the IF's TRUE branch.
def lazy(text):
    m = re.search(r'LET\(Help, (TEXTSPLIT\(.*?"→", "¶" \)),', text, flags=re.S)
    help_expr = m.group(1)
    text = text[:m.start()] + 'LET(' + text[m.end():]
    return text.replace('IF(Help?, Help, result)', f'IF(Help?, {help_expr}, result)').replace(
        'IF(Help?, result, Help)', f'IF(Help?, result, {help_expr})')


for n in ['SetVars', 'SetVar', 'GetVar', 'NewState']:
    t = lazy(names[n])
    for m in ['SetVars', 'SetVar', 'GetVar', 'NewState']:
        t = re.sub(r'\b%s\(' % m, m + 'Z(', t)
    names[n + 'Z'] = t
# Candidate fast forms, same state format.
names['GetVarF'] = ('=LAMBDA(state, name, LET(us, CHAR(31), rs, CHAR(30), gs, CHAR(29), s, @state, '
                    'hit, TEXTAFTER(us & s, us & name & rs, 1, 1, 0, ""), '
                    'entry, TEXTBEFORE(hit & us, us), tag, LEFT(entry, 1), payload, MID(entry, 3, LEN(entry)), '
                    'raw, IF(ISNUMBER(FIND(gs, payload)), TOCOL(TEXTSPLIT(payload, gs)), payload), '
                    'IF(hit = "", NA(), IF(tag = "n", --raw, IF(tag = "b", raw = "TRUE", raw)))))')
enc = ('LAMBDA(n, x, LET(f, @INDEX(x, 1, 1), t, TYPE(f), '
       'n & CHAR(30) & IF(t = 1, "n", IF(t = 4, "b", "t")) & CHAR(30) & '
       'IF(ROWS(x) * COLUMNS(x) > 1, TEXTJOIN(CHAR(29), FALSE, x), f & "")))')
pairs = ', '.join(f'[n_{k}], [v_{k}]' for k in range(1, 11))
parts = ', '.join(['e(n_1, v_1)'] + [f'IF(ISOMITTED(n_{k}), "", e(n_{k}, v_{k}))' for k in range(2, 11)])
names['SetVarsF'] = f'=LAMBDA({pairs}, LET(e, {enc}, TEXTJOIN(CHAR(31), TRUE, {parts})))'

VARS2 = [('p', '0', 'p+IF(b>0,MOD(v,3)-1,0)'), ('b', '4', 'MEDIAN(0,6,IF(MOD(v,5)=0,b+2,b-1))')]
VARS5 = VARS2 + [('c', '0', 'c+MOD(v,7)'), ('d', '0', 'MAX(d,p)'), ('e', '0', 'e+(b=0)')]


def nexts(vs):
    return ', '.join(f'next{n.upper()}, {x}' for n, _, x in vs)


def named_state(vs, N, sv, gv):
    seed = f'{sv}(' + ', '.join(f'"{n}", {i}' for n, i, _ in vs) + ')'
    gets = ', '.join(f'{n}, {gv}(a, "{n}")' for n, _, _ in vs)
    out = f'{sv}(' + ', '.join(f'"{n}", next{n.upper()}' for n, _, _ in vs) + ')'
    fin = '+'.join(f'{gv}(r, "{n}")' for n, _, _ in vs)
    return f'=LET(r, REDUCE({seed}, SEQUENCE({N}), LAMBDA(a, v, LET({gets}, {nexts(vs)}, {out}))), {fin})'


def hstack(vs, N):
    seed = 'HSTACK(' + ', '.join(i for _, i, _ in vs) + ')'
    gets = ', '.join(f'{n}, INDEX(a, {k + 1})' for k, (n, _, _) in enumerate(vs))
    out = 'HSTACK(' + ', '.join(f'next{n.upper()}' for n, _, _ in vs) + ')'
    return f'=LET(r, REDUCE({seed}, SEQUENCE({N}), LAMBDA(a, v, LET({gets}, {nexts(vs)}, {out}))), SUM(r))'


def text(vs, N):
    seed = '"' + ','.join(i for _, i, _ in vs) + '"'
    gets = ', '.join(f'{n}, INDEX(s, {k + 1})' for k, (n, _, _) in enumerate(vs))
    out = 'TEXTJOIN(",", FALSE, ' + ', '.join(f'next{n.upper()}' for n, _, _ in vs) + ')'
    return (f'=LET(r, REDUCE({seed}, SEQUENCE({N}), LAMBDA(a, v, LET(s, --TEXTSPLIT(a, ","), {gets}, '
            f'{nexts(vs)}, {out}))), SUM(--TEXTSPLIT(r, ",")))')


def packed2(N):
    # b is held in 0..6 by MEDIAN, so a = p*10 + b round-trips (MOD is non-negative for divisor 10).
    return (f'=LET(r, REDUCE(4, SEQUENCE({N}), LAMBDA(a, v, LET(b, MOD(a, 10), p, (a - b) / 10, '
            f'{nexts(VARS2)}, nextP * 10 + nextB))), MOD(r, 10) + (r - MOD(r, 10)) / 10)')


cases = []
for N in [1000, 10000]:
    for k, vs in [(2, VARS2), (5, VARS5)]:
        cases.append((f'b_hstack_{k}v_{N}', hstack(vs, N)))
        cases.append((f'a_setvars_{k}v_{N}', named_state(vs, N, 'SetVars', 'GetVar')))
        cases.append((f'c_text_{k}v_{N}', text(vs, N)))
        if k == 2:
            cases.append((f'c_packed_{k}v_{N}', packed2(N)))
        cases.append((f'd_lean_{k}v_{N}', named_state(vs, N, 'SetVarsL', 'GetVarL')))
        cases.append((f'h_lazyhelp_{k}v_{N}', named_state(vs, N, 'SetVarsZ', 'GetVarZ')))
        cases.append((f'e_fast_{k}v_{N}', named_state(vs, N, 'SetVarsF', 'GetVarF')))
        cases.append((f'f_fastget_{k}v_{N}', named_state(vs, N, 'SetVars', 'GetVarF')))
        cases.append((f'g_fastset_{k}v_{N}', named_state(vs, N, 'SetVarsF', 'GetVar')))

with open(os.path.join(SERVE, 'lb386-names.tsv'), 'w', encoding='utf-8') as f:
    for n, t in names.items():
        f.write(f'{n}\t{t}\n')
with open(os.path.join(SERVE, 'lb386-cases.tsv'), 'w', encoding='utf-8') as f:
    for n, t in cases:
        f.write(f'{n}\t{t}\n')
for n, t in names.items():
    print(n, len(t))
print(len(cases), 'cases')
