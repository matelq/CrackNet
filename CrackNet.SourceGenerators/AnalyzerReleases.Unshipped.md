; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|--------------------
CRN001 | CrackNet | Error | A type with synced properties or a spawnable type has to be partial
CRN002 | CrackNet | Error | A type with synced properties or a spawnable type has to be top-level and non-generic
CRN003 | CrackNet | Error | A spawnable class needs a scene
CRN004 | CrackNet | Error | A spawnable class is the root of more than one scene
CRN005 | CrackNet | Error | Spawn data has to fit in a Variant
CRN006 | CrackNet | Error | A node used over the network needs a NetworkObject in its scene
