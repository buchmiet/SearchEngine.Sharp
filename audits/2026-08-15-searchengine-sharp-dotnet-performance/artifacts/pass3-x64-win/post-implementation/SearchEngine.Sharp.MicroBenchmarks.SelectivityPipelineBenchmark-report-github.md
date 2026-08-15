```

BenchmarkDotNet v0.15.2, Windows 11 (10.0.26200.9168)
Unknown processor
.NET SDK 11.0.100-preview.6.26359.118
  [Host] : .NET 10.0.11 (10.0.1126.37416), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

Toolchain=InProcessNoEmitToolchain  InvocationCount=1  IterationCount=5  
UnrollFactor=1  WarmupCount=1  

```
| Method                                                          | Mean      | Error       | StdDev     | Median    | Ratio | RatioSD | Allocated | Alloc Ratio |
|---------------------------------------------------------------- |----------:|------------:|-----------:|----------:|------:|--------:|----------:|------------:|
| &#39;0 text hits + NaturalSort cold&#39;                                |  29.27 μs |    13.25 μs |   2.050 μs |  29.90 μs |  1.00 |    0.09 |  16.38 KB |        1.00 |
| &#39;Within+Facet+NaturalSort cold (see SelectivityPipelineCounts)&#39; | 155.64 μs |    81.14 μs |  21.071 μs | 163.70 μs |  5.34 |    0.75 |   85.8 KB |        5.24 |
| &#39;Within+Facet SnapshotOrder (see SelectivityPipelineCounts)&#39;    | 356.28 μs | 1,387.89 μs | 360.431 μs | 109.00 μs | 12.22 |   11.39 |  85.17 KB |        5.20 |
