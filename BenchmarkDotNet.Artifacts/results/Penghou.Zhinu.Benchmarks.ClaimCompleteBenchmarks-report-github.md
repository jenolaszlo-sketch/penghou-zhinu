```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.9168)
Unknown processor
.NET SDK 10.0.400
  [Host]     : .NET 10.0.11 (10.0.1126.37416), X64 RyuJIT AVX2
  Job-FYATXP : .NET 10.0.11 (10.0.1126.37416), X64 RyuJIT AVX2

IterationCount=2  LaunchCount=1  WarmupCount=1  

```
| Method            | Mean     | Error     | StdDev    | Allocated |
|------------------ |---------:|----------:|----------:|----------:|
| ClaimThenComplete | 8.201 ms | 38.407 ms | 0.0853 ms |  35.63 KB |
