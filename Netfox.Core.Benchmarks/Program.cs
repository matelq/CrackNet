using BenchmarkDotNet.Running;

// Run everything:        dotnet run -c Release --project Netfox.Core.Benchmarks
// Run one set of cases:  dotnet run -c Release --project Netfox.Core.Benchmarks -- --filter *Snapshot*
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

public partial class Program { }
