Repeats per (engine, size): {'Lucene@100000': 3, 'Lucene@1000000': 2, 'Sharp@100000': 3, 'Sharp@1000000': 2, 'Tantivy@100000': 3, 'Tantivy@1000000': 2}. Every cell is the median over repeat runs of the per-run median.

## Hit counts (must agree)

| Query | Sharp @100,000 | Lucene @100,000 | Tantivy @100,000 | Sharp @1,000,000 | Lucene @1,000,000 | Tantivy @1,000,000 |
|---|---:|---:|---:|---:|---:|---:|
| term, no hit: zzzqqq | 0 | 0 | 0 | 0 | 0 | 0 |
| term: final | 1,291 | 1,291 | 1,291 | 13,505 | 13,505 | 13,505 |
| term: jpg (very common) | 10,555 | 10,555 | 10,555 | 105,458 | 105,458 | 105,458 |
| report AND pdf | 29 | 29 | 29 | 228 | 228 | 228 |
| jpg OR png | 15,831 | 15,831 | 15,831 | 157,960 | 157,960 | 157,960 |
| (jpg OR png) AND NOT img | 15,831 | 15,831 | 15,831 | 157,960 | 157,960 | 157,960 |
| prefix: rep* | 943 | 943 | 943 | 9,313 | 9,313 | 9,313 |
| suffix: *ing | 903 | 903 | 903 | 9,030 | 9,030 | 9,030 |
| infix: *port* | 2,594 | 2,594 | 2,594 | 25,755 | 25,755 | 25,755 |
| pattern: dsc????? | 3,955 | 3,955 | 3,955 | 40,043 | 40,043 | 40,043 |
| phrase: "report final" (Sharp: not supported) | n/a | 4 | 4 | n/a | 22 | 22 |

## Query latency, 100,000 names (merged to one segment)

### Count only (no ids)

| Query | hits | Sharp | Lucene | Tantivy | Sharp vs Lucene | Sharp vs Tantivy |
|---|---:|---:|---:|---:|---:|---:|
| term, no hit: zzzqqq | 0 | 0.07 µs | 3.80 µs | 2.04 µs | 52.0× faster | 28.0× faster |
| term: final | 1,291 | 0.08 µs | 8.57 µs | 2.11 µs | 111.3× faster | 27.4× faster |
| term: jpg (very common) | 10,555 | 0.08 µs | 44.0 µs | 2.07 µs | 564.7× faster | 26.5× faster |
| report AND pdf | 29 | 5.69 µs | 30.4 µs | 12.0 µs | 5.3× faster | 2.1× faster |
| jpg OR png | 15,831 | 15.5 µs | 243 µs | 28.5 µs | 15.7× faster | 1.8× faster |
| (jpg OR png) AND NOT img | 15,831 | 17.2 µs | 524 µs | 29.6 µs | 30.5× faster | 1.7× faster |
| prefix: rep* | 943 | 126 µs | 35.3 µs | 324 µs | 3.6× slower | 2.6× faster |
| suffix: *ing | 903 | 222 µs | 996 µs | 10.99 ms | 4.5× faster | 49.6× faster |
| infix: *port* | 2,594 | 696 µs | 2.66 ms | 11.96 ms | 3.8× faster | 17.2× faster |
| pattern: dsc????? | 3,955 | 219 µs | 271 µs | 1.88 ms | 1.2× faster | 8.6× faster |
| phrase: "report final" (Sharp: not supported) | 4 | – | 23.3 µs | 11.9 µs | – | – |

### Top 10 hits with ids

| Query | hits | Sharp | Lucene | Tantivy | Sharp vs Lucene | Sharp vs Tantivy |
|---|---:|---:|---:|---:|---:|---:|
| term, no hit: zzzqqq | 0 | 0.08 µs | 4.19 µs | 2.95 µs | 53.7× faster | 37.9× faster |
| term: final | 1,291 | 2.88 µs | 61.4 µs | 25.1 µs | 21.3× faster | 8.7× faster |
| term: jpg (very common) | 10,555 | 18.1 µs | 141 µs | 23.4 µs | 7.8× faster | 1.3× faster |
| report AND pdf | 29 | 6.61 µs | 81.7 µs | 33.8 µs | 12.4× faster | 5.1× faster |
| jpg OR png | 15,831 | 58.9 µs | 337 µs | 121 µs | 5.7× faster | 2.0× faster |
| (jpg OR png) AND NOT img | 15,831 | 61.8 µs | 606 µs | 168 µs | 9.8× faster | 2.7× faster |
| prefix: rep* | 943 | 245 µs | 82.2 µs | 346 µs | 3.0× slower | 1.4× faster |
| suffix: *ing | 903 | 229 µs | 1.02 ms | 10.34 ms | 4.4× faster | 45.0× faster |
| infix: *port* | 2,594 | 557 µs | 2.72 ms | 11.47 ms | 4.9× faster | 20.6× faster |
| pattern: dsc????? | 3,955 | 230 µs | 335 µs | 2.03 ms | 1.5× faster | 8.8× faster |
| phrase: "report final" (Sharp: not supported) | 4 | – | 38.9 µs | 21.8 µs | – | – |

### All matching ids

| Query | hits | Sharp | Lucene | Tantivy | Sharp vs Lucene | Sharp vs Tantivy |
|---|---:|---:|---:|---:|---:|---:|
| term, no hit: zzzqqq | 0 | 0.08 µs | 3.84 µs | 2.06 µs | 50.5× faster | 27.1× faster |
| term: final | 1,291 | 3.46 µs | 14.1 µs | 1.65 ms | 4.1× faster | 476.6× faster |
| term: jpg (very common) | 10,555 | 23.0 µs | 89.1 µs | 17.29 ms | 3.9× faster | 753.2× faster |
| report AND pdf | 29 | 8.43 µs | 30.7 µs | 64.9 µs | 3.6× faster | 7.7× faster |
| jpg OR png | 15,831 | 68.7 µs | 294 µs | 25.52 ms | 4.3× faster | 371.4× faster |
| (jpg OR png) AND NOT img | 15,831 | 69.2 µs | 536 µs | 24.39 ms | 7.7× faster | 352.7× faster |
| prefix: rep* | 943 | 140 µs | 39.5 µs | 1.92 ms | 3.5× slower | 13.8× faster |
| suffix: *ing | 903 | 243 µs | 973 µs | 22.86 ms | 4.0× faster | 94.0× faster |
| infix: *port* | 2,594 | 535 µs | 2.70 ms | 26.88 ms | 5.0× faster | 50.3× faster |
| pattern: dsc????? | 3,955 | 234 µs | 288 µs | 10.04 ms | 1.2× faster | 42.9× faster |
| phrase: "report final" (Sharp: not supported) | 4 | – | 23.9 µs | 38.1 µs | – | – |

### Tantivy, all ids read from stored fields instead of the fast column

| Query | stored-field path |
|---|---:|
| term, no hit: zzzqqq | 2.06 µs |
| term: final | 7.43 ms |
| term: jpg (very common) | 38.45 ms |
| report AND pdf | 83.7 µs |
| jpg OR png | 64.96 ms |
| (jpg OR png) AND NOT img | 66.44 ms |
| prefix: rep* | 4.51 ms |
| suffix: *ing | 26.44 ms |
| infix: *port* | 32.58 ms |
| pattern: dsc????? | 16.32 ms |
| phrase: "report final" (Sharp: not supported) | 34.9 µs |


## Query latency, 1,000,000 names (merged to one segment)

### Count only (no ids)

| Query | hits | Sharp | Lucene | Tantivy | Sharp vs Lucene | Sharp vs Tantivy |
|---|---:|---:|---:|---:|---:|---:|
| term, no hit: zzzqqq | 0 | 0.10 µs | 4.81 µs | 3.13 µs | 49.9× faster | 32.5× faster |
| term: final | 13,505 | 0.10 µs | 76.5 µs | 2.15 µs | 750.3× faster | 21.0× faster |
| term: jpg (very common) | 105,458 | 0.07 µs | 395 µs | 2.98 µs | 5407.9× faster | 40.8× faster |
| report AND pdf | 228 | 70.3 µs | 348 µs | 75.8 µs | 5.0× faster | 1.1× faster |
| jpg OR png | 157,960 | 151 µs | 2.11 ms | 248 µs | 14.0× faster | 1.6× faster |
| (jpg OR png) AND NOT img | 157,960 | 213 µs | 4.65 ms | 246 µs | 21.8× faster | 1.2× faster |
| prefix: rep* | 9,313 | 1.34 ms | 322 µs | 820 µs | 4.1× slower | 1.6× slower |
| suffix: *ing | 9,030 | 2.87 ms | 11.25 ms | 84.45 ms | 3.9× faster | 29.4× faster |
| infix: *port* | 25,755 | 8.49 ms | 25.77 ms | 95.30 ms | 3.0× faster | 11.2× faster |
| pattern: dsc????? | 40,043 | 1.69 ms | 2.60 ms | 9.73 ms | 1.5× faster | 5.8× faster |
| phrase: "report final" (Sharp: not supported) | 22 | – | 182 µs | 82.9 µs | – | – |

### Top 10 hits with ids

| Query | hits | Sharp | Lucene | Tantivy | Sharp vs Lucene | Sharp vs Tantivy |
|---|---:|---:|---:|---:|---:|---:|
| term, no hit: zzzqqq | 0 | 0.09 µs | 5.12 µs | 2.96 µs | 54.7× faster | 31.6× faster |
| term: final | 13,505 | 25.0 µs | 192 µs | 31.8 µs | 7.7× faster | 1.3× faster |
| term: jpg (very common) | 105,458 | 372 µs | 1.11 ms | 29.2 µs | 3.0× faster | 12.7× slower |
| report AND pdf | 228 | 85.9 µs | 399 µs | 118 µs | 4.6× faster | 1.4× faster |
| jpg OR png | 157,960 | 1.02 ms | 2.78 ms | 1.11 ms | 2.7× faster | 1.1× faster |
| (jpg OR png) AND NOT img | 157,960 | 992 µs | 7.30 ms | 1.42 ms | 7.4× faster | 1.4× faster |
| prefix: rep* | 9,313 | 1.50 ms | 407 µs | 1.07 ms | 3.7× slower | 1.4× slower |
| suffix: *ing | 9,030 | 3.39 ms | 11.08 ms | 83.81 ms | 3.3× faster | 24.7× faster |
| infix: *port* | 25,755 | 9.18 ms | 22.04 ms | 89.32 ms | 2.4× faster | 9.7× faster |
| pattern: dsc????? | 40,043 | 2.04 ms | 2.92 ms | 9.81 ms | 1.4× faster | 4.8× faster |
| phrase: "report final" (Sharp: not supported) | 22 | – | 247 µs | 111 µs | – | – |

### All matching ids

| Query | hits | Sharp | Lucene | Tantivy | Sharp vs Lucene | Sharp vs Tantivy |
|---|---:|---:|---:|---:|---:|---:|
| term, no hit: zzzqqq | 0 | 0.09 µs | 4.64 µs | 2.06 µs | 50.4× faster | 22.4× faster |
| term: final | 13,505 | 31.7 µs | 117 µs | 20.42 ms | 3.7× faster | 644.4× faster |
| term: jpg (very common) | 105,458 | 469 µs | 1.02 ms | 201 ms | 2.2× faster | 428.4× faster |
| report AND pdf | 228 | 67.8 µs | 354 µs | 613 µs | 5.2× faster | 9.1× faster |
| jpg OR png | 157,960 | 1.16 ms | 2.96 ms | 281 ms | 2.6× faster | 242.4× faster |
| (jpg OR png) AND NOT img | 157,960 | 1.23 ms | 5.42 ms | 339 ms | 4.4× faster | 275.2× faster |
| prefix: rep* | 9,313 | 1.55 ms | 376 µs | 16.07 ms | 4.1× slower | 10.4× faster |
| suffix: *ing | 9,030 | 2.76 ms | 10.46 ms | 186 ms | 3.8× faster | 67.3× faster |
| infix: *port* | 25,755 | 9.26 ms | 22.09 ms | 225 ms | 2.4× faster | 24.3× faster |
| pattern: dsc????? | 40,043 | 2.25 ms | 2.80 ms | 109 ms | 1.2× faster | 48.2× faster |
| phrase: "report final" (Sharp: not supported) | 22 | – | 232 µs | 281 µs | – | – |

### Tantivy, all ids read from stored fields instead of the fast column

| Query | stored-field path |
|---|---:|
| term, no hit: zzzqqq | 2.05 µs |
| term: final | 103 ms |
| term: jpg (very common) | 452 ms |
| report AND pdf | 2.02 ms |
| jpg OR png | 770 ms |
| (jpg OR png) AND NOT img | 744 ms |
| prefix: rep* | 47.50 ms |
| suffix: *ing | 218 ms |
| infix: *port* | 262 ms |
| pattern: dsc????? | 180 ms |
| phrase: "report final" (Sharp: not supported) | 246 µs |

## Build, update and footprint

| Metric | Sharp @100,000 | Lucene @100,000 | Tantivy @100,000 | Sharp @1,000,000 | Lucene @1,000,000 | Tantivy @1,000,000 |
|---|---:|---:|---:|---:|---:|---:|
| Index build, 1 thread, in memory (s) | 0.23 | 0.49 | 0.85 | 1.75 | 3.65 | 6.65 |
| Merge to one segment (s) | 0.00 | 0.25 | 0.00 | 0.00 | 1.56 | 0.00 |
| Process RSS growth after build (MB) | 51 | 137 | 27 | 208 | 206 | 76 |
| Managed heap growth after build (MB) | 15 | 4 | 0 | 140 | 33 | 0 |
| Add 1 doc → visible to a query, median | 30.1 µs | 665 µs | 9.40 ms | 21.2 µs | 613 µs | 7.11 ms |
| Add 1 doc → visible to a query, p95 | 90.1 µs | 2.43 ms | 20.15 ms | 69.8 µs | 2.97 ms | 12.83 ms |
| Build to disk (s) | – | 0.40 | 0.88 | – | 4.02 | 6.90 |
| Index on disk (MiB) | – | 3.3 | 3.6 | – | 31.6 | 34.6 |
| Open from disk + first query (ms) | – | 8.3 | 1.3 | – | 10.0 | 1.1 |
