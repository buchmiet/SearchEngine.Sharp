#!/usr/bin/env python3
"""Top inclusive/self frames of the busiest thread in a speedscope (evented) profile. Usage: speedscope_top.py FILE [N]"""
import json, sys, collections
d = json.load(open(sys.argv[1])); n = int(sys.argv[2]) if len(sys.argv) > 2 else 25
frames = [f['name'] for f in d['shared']['frames']]
best = None
for p in d['profiles']:
    if p['type'] != 'evented': continue
    dur = p['endValue'] - p['startValue']
    if best is None or dur > best[0]: best = (dur, p)
p = best[1]
incl = collections.Counter(); self_ = collections.Counter(); stack = []; last = None
for e in p['events']:
    t = e['at']
    if stack and last is not None:
        dt = t - last
        self_[stack[-1]] += dt
        for f in set(stack): incl[f] += dt
    last = t
    if e['type'] == 'O': stack.append(e['frame'])
    else: stack.pop()
total = p['endValue'] - p['startValue']
print(f"thread '{p['name']}' {total:.0f} {p['unit']}")
print("-- inclusive"); [print(f"{v/total*100:5.1f}%  {frames[k][:150]}") for k, v in incl.most_common(n)]
print("-- self"); [print(f"{v/total*100:5.1f}%  {frames[k][:150]}") for k, v in self_.most_common(n)]
