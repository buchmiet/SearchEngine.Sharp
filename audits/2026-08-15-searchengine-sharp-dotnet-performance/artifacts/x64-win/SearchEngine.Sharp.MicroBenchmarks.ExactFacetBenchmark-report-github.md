```

BenchmarkDotNet v0.15.2, Windows 11 (10.0.26200.9168)
Unknown processor
.NET SDK 11.0.100-preview.6.26359.118
  [Host] : .NET 10.0.11 (10.0.1126.37416), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

Toolchain=InProcessNoEmitToolchain  

```
| Method               | Mean       | Error     | StdDev    | Ratio  | RatioSD | Gen0    | Gen1   | Gen2   | Allocated | Alloc Ratio |
|--------------------- |-----------:|----------:|----------:|-------:|--------:|--------:|-------:|-------:|----------:|------------:|
| ExactOnly            |   1.150 μs | 0.0198 μs | 0.0227 μs |   1.00 |    0.03 |  0.3605 |      - |      - |   5.91 KB |        1.00 |
| ExactWithFacetFilter |   1.683 μs | 0.0228 μs | 0.0213 μs |   1.46 |    0.03 |  0.3681 |      - |      - |   6.02 KB |        1.02 |
| FilterOnly           | 252.258 μs | 5.0021 μs | 8.4939 μs | 219.41 |    8.42 | 10.2539 | 2.4414 | 2.4414 | 256.45 KB |       43.36 |
