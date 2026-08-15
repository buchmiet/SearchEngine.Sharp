```

BenchmarkDotNet v0.15.2, Windows 11 (10.0.26200.9168)
Unknown processor
.NET SDK 11.0.100-preview.6.26359.118
  [Host] : .NET 10.0.11 (10.0.1126.37416), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

Toolchain=InProcessNoEmitToolchain  

```
| Method                                               | Mean     | Error     | StdDev    | Median   | Ratio | RatioSD | Gen0   | Gen1   | Allocated | Alloc Ratio |
|----------------------------------------------------- |---------:|----------:|----------:|---------:|------:|--------:|-------:|-------:|----------:|------------:|
| &#39;Production-style first bigram (legacy direct path)&#39; | 7.401 μs | 0.1395 μs | 0.3033 μs | 7.252 μs |  1.00 |    0.06 | 0.7477 | 0.0305 |  12.27 KB |        1.00 |
| &#39;Experimental rarest bigram among query bigrams&#39;     | 7.931 μs | 0.0634 μs | 0.0529 μs | 7.930 μs |  1.07 |    0.04 | 0.7477 | 0.0305 |  12.27 KB |        1.00 |
