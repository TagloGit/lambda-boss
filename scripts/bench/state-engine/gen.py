"""Generate the state-engine hot-loop benchmark inputs for the VM.
Writes <serve>/lb386-names.tsv (name \t refersTo) and <serve>/lb386-cases.tsv (id \t formula).
Usage: gen.py <serve> <label>=<lambda-boss checkout> [<label>=<checkout> ...]
Each label gets its own copy of the Loops LAMBDAs (GetVar_<label>, ...) and its own a_<label>_* cases,
so several versions of the library are timed in one workbook."""
import os, re, sys

SERVE = sys.argv[1]
CHECKOUTS = [a.split('=', 1) for a in sys.argv[2:]]
LOOPS = ['SetVars', 'SetVar', 'GetVar', 'NewState']


def body(path):
    src = open(path, encoding='utf-8').read()
    src = re.sub(r'/\*.*?\*/', '', src, flags=re.S)
    src = re.sub(r'^\s*//.*$', '', src, flags=re.M)
    i = src.index('LAMBDA(')
    j = src.rindex(')')
    return '=' + re.sub(r'\s*\n\s*', ' ', src[i:j + 1]).strip()


names = {}
for label, lb in CHECKOUTS:
    for n in LOOPS:
        t = body(os.path.join(lb, 'lambdas', 'loops', n + '.lambda'))
        for m in LOOPS:
            t = re.sub(r'\b%s\(' % m, f'{m}_{label}(', t)
        names[f'{n}_{label}'] = t

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
        for label, _ in CHECKOUTS:
            cases.append((f'a_{label}_{k}v_{N}', named_state(vs, N, f'SetVars_{label}', f'GetVar_{label}')))
        cases.append((f'c_text_{k}v_{N}', text(vs, N)))
        if k == 2:
            cases.append((f'c_packed_{k}v_{N}', packed2(N)))

with open(os.path.join(SERVE, 'lb386-names.tsv'), 'w', encoding='utf-8') as f:
    for n, t in names.items():
        f.write(f'{n}\t{t}\n')
with open(os.path.join(SERVE, 'lb386-cases.tsv'), 'w', encoding='utf-8') as f:
    for n, t in cases:
        f.write(f'{n}\t{t}\n')
for n, t in names.items():
    print(n, len(t))
print(len(cases), 'cases')

# Automated fix for issue #437: REDUCE state with a 1x1 VSTACK variable gives #N/A (regression of #407?)
