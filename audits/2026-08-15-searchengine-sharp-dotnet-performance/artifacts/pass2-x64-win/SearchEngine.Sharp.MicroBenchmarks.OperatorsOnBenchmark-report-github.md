```

BenchmarkDotNet v0.15.2, Windows 11 (10.0.26200.9168)
Unknown processor
.NET SDK 11.0.100-preview.6.26359.118
  [Host] : .NET 10.0.11 (10.0.1126.37416), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

Toolchain=InProcessNoEmitToolchain  

```
| Method                       | Mean        | Error     | StdDev    | Median      | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|----------------------------- |------------:|----------:|----------:|------------:|------:|--------:|-------:|----------:|------------:|
| Exact_OperatorsOff           | 1,387.14 ns | 14.507 ns | 13.570 ns | 1,386.88 ns |  1.00 |    0.01 | 0.3605 |    6056 B |       1.000 |
| Exact_OperatorsOn            | 1,397.18 ns | 15.128 ns | 13.410 ns | 1,395.44 ns |  1.01 |    0.01 | 0.3681 |    6176 B |       1.020 |
| ExactFacet_OperatorsOff      | 2,881.16 ns | 32.372 ns | 30.281 ns | 2,875.54 ns |  2.08 |    0.03 | 0.3662 |    6168 B |       1.018 |
| ExactFacet_OperatorsOn       | 2,891.47 ns | 39.408 ns | 36.862 ns | 2,886.98 ns |  2.08 |    0.03 | 0.3738 |    6288 B |       1.038 |
| CountExact_OperatorsOff      |    23.66 ns |  0.506 ns |  1.233 ns |    23.01 ns |  0.02 |    0.00 | 0.0024 |      40 B |       0.007 |
| CountExact_OperatorsOn       |    34.47 ns |  0.700 ns |  0.584 ns |    34.44 ns |  0.02 |    0.00 | 0.0095 |     160 B |       0.026 |
| CountExactFacet_OperatorsOff | 1,374.28 ns | 27.198 ns | 73.066 ns | 1,344.96 ns |  0.99 |    0.05 | 0.0057 |     120 B |       0.020 |
| CountExactFacet_OperatorsOn  | 1,313.92 ns | 25.632 ns | 33.329 ns | 1,302.15 ns |  0.95 |    0.03 | 0.0134 |     240 B |       0.040 |
