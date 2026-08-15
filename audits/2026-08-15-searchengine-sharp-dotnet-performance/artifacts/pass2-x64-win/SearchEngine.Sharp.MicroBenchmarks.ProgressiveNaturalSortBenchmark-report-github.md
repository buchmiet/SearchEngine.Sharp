```

BenchmarkDotNet v0.15.2, Windows 11 (10.0.26200.9168)
Unknown processor
.NET SDK 11.0.100-preview.6.26359.118
  [Host] : .NET 10.0.11 (10.0.1126.37416), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

Toolchain=InProcessNoEmitToolchain  InvocationCount=1  UnrollFactor=1  

```
| Method                                                       | Mean        | Error     | StdDev      | Median      | Ratio | RatioSD | Gen0      | Allocated   | Alloc Ratio |
|------------------------------------------------------------- |------------:|----------:|------------:|------------:|------:|--------:|----------:|------------:|------------:|
| &#39;7 cold NaturalSort requeries (fresh snapshot each publish)&#39; | 48,388.5 μs | 960.87 μs | 1,984.36 μs | 47,605.5 μs | 1.002 |    0.06 | 1000.0000 | 24570.13 KB |        1.00 |
| &#39;7 SnapshotOrder requeries (fresh snapshots, no sort build)&#39; |    134.5 μs |   4.27 μs |    12.05 μs |    130.2 μs | 0.003 |    0.00 |         - |   262.48 KB |        0.01 |
