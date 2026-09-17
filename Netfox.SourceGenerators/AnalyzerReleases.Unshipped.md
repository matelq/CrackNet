; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|--------------------
NFX001 | Netfox | Error | A type with synced properties or a spawnable type has to be partial
NFX002 | Netfox | Error | A type with synced properties or a spawnable type has to be top-level and non-generic
NFX003 | Netfox | Error | A spawnable class needs a scene
NFX004 | Netfox | Error | A spawnable class is the root of more than one scene
NFX005 | Netfox | Error | Spawn data has to fit in a Variant
