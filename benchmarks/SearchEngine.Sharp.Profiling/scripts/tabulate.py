#!/usr/bin/env python3
"""Turns the harness JSONL logs into Markdown tables (medians over repetitions).
Usage: tabulate.py KIND FILE [FILE...]   KIND = find | variants | breakdown | querypaths | scale | persistent | unicode-cost
"""
import json, sys, collections, statistics as st


def load(files):
    rows = []
    for f in files:
        for line in open(f):
            line = line.strip()
            if line.startswith('{'):
                try:
                    r = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if not r.get('failed'):
                    rows.append(r)
    return rows


def med(rs, f):
    vals = [r[f] for r in rs if r.get(f) is not None]
    return st.median(vals) if vals else float('nan')


def vname(r):
    v = r['variant']
    lib = r['label'].split(':')[1] if ':' in (r.get('label') or '') else ''
    if v in ('commander-policy', 'commander-single-pass') and r.get('policy') and r['policy'] != 'adaptive':
        v += '-' + r['policy']
    elif v == 'commander-policy':
        v += '-adaptive-' + str(r.get('batch'))
    return (lib + ' ' + v).strip()


def find(rows):
    g = collections.defaultdict(list)
    for r in rows:
        q = ('re:' if r.get('regex') else '') + (r.get('query') or '*')
        lab = r['label'].split(':')[0]
        g[(lab, q, vname(r))].append(r)
    for lab in sorted({k[0] for k in g}):
        print(f'\n### {lab}\n')
        print('| zapytanie | wariant | n | trafienia | hash | pierwszy wynik [ms] | całość [ms] | alokacje [MB] | gen0/1/2 | pauzy GC [ms] | szczyt RSS [MB] | publikacje |')
        print('|---|---|---:|---:|---|---:|---:|---:|---|---:|---:|---:|')
        for k in sorted(k for k in g if k[0] == lab):
            rs = g[k]
            hashes = {r.get('hitsHash') for r in rs if r.get('hitsHash')}
            pubs = med(rs, 'publishes') if any('publishes' in r for r in rs) else ''
            print(f"| `{k[1]}` | {k[2]} | {len(rs)} | {rs[0]['hits']} | {','.join(sorted(hashes))[:8]} | {med(rs,'firstHitMs'):.1f} | {med(rs,'totalMs'):.1f} | {med(rs,'allocMB'):.1f} | {med(rs,'gc0'):.0f}/{med(rs,'gc1'):.0f}/{med(rs,'gc2'):.0f} | {med(rs,'gcPauseMs'):.1f} | {med(rs,'peakRssMB'):.0f} | {pubs if pubs=='' else f'{pubs:.0f}'} |")


def breakdown(rows):
    print('| drzewo | zapytanie | całość A [ms] | enumeracja | Directory.Exists | FileInfo (stat) | wpis+fakty | przebudowy (suma) | publikacje | zapytania (suma) | deduplikacja | pauzy GC | alokacje [MB] |')
    print('|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|')
    g = collections.defaultdict(list)
    for r in rows:
        if r['mode'] == 'find':
            g[(r['label'], r['query'])].append(r)
    for (lab, q), rs in sorted(g.items()):
        print(f"| {lab} | `{q}` | {med(rs,'totalMs'):.0f} | {med(rs,'enumMs'):.0f} | {med(rs,'existsMs'):.0f} | {med(rs,'statMs'):.0f} | {med(rs,'facetEntryMs'):.0f} | {med(rs,'rebuildMs'):.0f} | {med(rs,'publishes'):.0f} | {med(rs,'queryMs'):.0f} | {med(rs,'dedupMs'):.1f} | {med(rs,'gcPauseMs'):.0f} | {med(rs,'allocMB'):.0f} |")
    print()
    print('| drzewo | zapytanie | pliki | enumeracja | Exists (wszystkie) | FileInfo | FSE nazwy | FSE + metadane | wpisy [ms / MB] | przebudowy idealne [ms / MB] | zapytania | budowa FileMask [ms] | w tym tokenizacja | pierwszy Within [ms] | drugi Within [ms] | alok. 1 budowy [MB] |')
    print('|---|---|---:|---:|---:|---:|---:|---:|---|---|---:|---:|---:|---:|---:|---:|')
    for r in rows:
        if r['mode'] != 'decompose':
            continue
        print(f"| {r['label']} | `{r['query']}` | {r['files']} | {r['enumOnlyMs']} | {r['existsAllEntriesMs']} | {r['fileInfoStatMs']} | {r['fseNamesOnlyMs']} | {r['fseWithMetadataMs']} | {r['entryConstructionMs']} / {r['entryConstructionAllocMB']} | {r['buildsMs']} / {r['buildsAllocMB']} | {r['requeriesMs']} | {r['full_buildMs']} | {r['full_tokenizeOnlyMs']} | {r['full_firstWithinMs']} | {r['full_secondWithinMs']} | {r['full_allocMB']} |")


def querypaths(rows):
    g = collections.defaultdict(dict)
    for r in rows:
        g[(r['preset'], r['q'], r['n'])][r['label']] = r
    labels = sorted({r['label'] for r in rows})
    print('| preset | zapytanie | N | trafienia | ' + ' | '.join(f'{l} [µs]' for l in labels) + ' | ' + ' | '.join(f'{l} alok. [B]' for l in labels) + ' |')
    print('|---|---|---:|---:|' + '---:|' * (2 * len(labels)))
    for k in sorted(g):
        d = g[k]
        hits = next(iter(d.values()))['hits']
        print(f'| {k[0]} | {k[1]} | {k[2]} | {hits} | ' + ' | '.join(f"{d[l]['medUs']:.1f}" if l in d else '' for l in labels) + ' | ' + ' | '.join(f"{d[l]['allocB']:.0f}" if l in d else '' for l in labels) + ' |')
    b = collections.defaultdict(dict)
    for r in rows:
        b[(r['preset'], r['n'])][r['label']] = r['buildMs']
    print('\n| preset | N | ' + ' | '.join(f'budowa {l} [ms]' for l in labels) + ' |')
    print('|---|---:|' + '---:|' * len(labels))
    for k in sorted(b):
        print(f'| {k[0]} | {k[1]} | ' + ' | '.join(f"{b[k].get(l, float('nan')):.1f}" for l in labels) + ' |')


def simple(rows, keys):
    print('| ' + ' | '.join(keys) + ' |')
    print('|' + '---|' * len(keys))
    for r in rows:
        print('| ' + ' | '.join(str(r.get(k, '')) for k in keys) + ' |')


if __name__ == '__main__':
    kind, files = sys.argv[1], sys.argv[2:]
    rows = load(files)
    if kind in ('find', 'variants'):
        find([r for r in rows if r['mode'] == 'find'])
    elif kind == 'breakdown':
        breakdown(rows)
    elif kind in ('querypaths', 'unicode-cost'):
        querypaths(rows)
    elif kind == 'scale':
        simple(rows, ['label', 'n', 'preset', 'facets', 'unicode', 'buildMs', 'buildAllocMB', 'gc0', 'gc1', 'gc2', 'gcPauseMs', 'lastFullGcPauseMs', 'entriesMB', 'heapWithIndexMB', 'indexBytesPerEntry', 'lohMB', 'words', 'firstQueryMs', 'addEntryMs', 'addEntryAllocMB', 'peakRssMB'])
    elif kind == 'persistent':
        keys = sorted({k for r in rows for k in r} - {'mode'})
        simple(rows, ['label', 'n'] + [k for k in keys if k not in ('label', 'n')])
    else:
        simple(rows, sorted({k for r in rows for k in r}))
