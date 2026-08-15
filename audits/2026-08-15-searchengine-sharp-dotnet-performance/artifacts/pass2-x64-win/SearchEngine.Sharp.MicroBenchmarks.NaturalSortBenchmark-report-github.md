```

BenchmarkDotNet v0.15.2, Windows 11 (10.0.26200.9168)
Unknown processor
.NET SDK 11.0.100-preview.6.26359.118
  [Host] : .NET 10.0.11 (10.0.1126.37416), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

Toolchain=InProcessNoEmitToolchain  

```
| Method                                             | Job        | InvocationCount | UnrollFactor | Mean         | Error      | StdDev       | Median       | Ratio | RatioSD | Gen0   | Gen1   | Allocated   | Alloc Ratio |
|--------------------------------------------------- |----------- |---------------- |------------- |-------------:|-----------:|-------------:|-------------:|------:|--------:|-------:|-------:|------------:|------------:|
| &#39;SnapshotOrder baseline (no sort build)&#39;           | Job-PONJLD | Default         | 16           |     48.29 μs |   0.419 μs |     0.372 μs |     48.29 μs |     ? |       ? | 7.8125 | 1.1597 |   128.63 KB |           ? |
|                                                    |            |                 |              |              |            |              |              |       |         |        |        |             |             |
| &#39;Cold — fresh snapshot, first NaturalSort query&#39;   | Job-HRFCSV | 1               | 1            | 23,426.26 μs | 457.424 μs | 1,236.672 μs | 23,151.35 μs | 1.003 |    0.07 |      - |      - | 10888.51 KB |        1.00 |
| &#39;Warm — same snapshot, permutation already cached&#39; | Job-HRFCSV | 1               | 1            |     77.28 μs |   3.456 μs |    10.080 μs |     71.65 μs | 0.003 |    0.00 |      - |      - |   133.21 KB |        0.01 |
