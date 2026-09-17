#!/usr/bin/env python3
import re, sys
from pathlib import Path
PROTECTED=("release","deploy","publish")
def top(text,key): return re.search(rf"(?m)^{re.escape(key)}:\s*$",text) is not None
def validate(p):
 t=p.read_text(encoding='utf-8'); low=f'{p.name} {t}'.lower(); protected=any(x in low for x in PROTECTED); e=[]
 if 'workflow_dispatch:' not in t: e.append('missing workflow_dispatch recovery entry point')
 if not top(t,'permissions'): e.append('missing top-level permissions')
 if not top(t,'concurrency'): e.append('missing top-level concurrency')
 if protected and not re.search(r'(?m)^\s+cancel-in-progress:\s*false\s*$',t): e.append('release/deploy/publish must not cancel in progress')
 return e
bad=[]
for a in sys.argv[1:]:
 p=Path(a)
 if p.exists() and p.suffix in ('.yml','.yaml'):
  es=validate(p)
  if es: bad += [f'{p}: '+ '; '.join(es)]
if bad: print('\n'.join(bad),file=sys.stderr); raise SystemExit(1)
print('Actions policy check passed')
