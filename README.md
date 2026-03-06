# DiscoveryTool (Snaffler)

## Overview
A high-performance utility for compliance auditing and sensitive data discovery in Windows/Active Directory environments. It identifies exposed credentials and configuration risks across network shares.

## Quick Start
snaffler.exe -s -o audit_results.log

## Usage Options
| Option | Description |
| :--- | :--- |
| -o | Output File: Saves results to a specific path. |
| -s | Stdout: Real-time console output. |
| -v | Verbosity: Detail level (Trace, Debug, Info, Data). |
| -m | Mirroring: Saves local copies of flagged files. |
| -l | Size Limit: Max size for mirrored files (Default 10MB). |
| -i | Direct Path: Scans a specific directory, bypassing AD discovery. |
| -n | Target List: Scans specific hosts or a provided input file. |
| -f | DFS Only: Limits discovery to DFS namespaces. |
| -u | Account Check: Searches for strings matching AD account names. |
| -z | Config: -z generate creates a reusable .toml configuration. |
| -x | Threads: Set concurrency level (min 4). |
| -p | Rules: Path to custom .toml rule directory. |

## Data Classification
The tool uses "Classifiers" to categorize findings:
1. Metadata: Flagging by extension (.kdbx, .key) or name (web.config).
2. Content: Searching inside files for strings like "password=" or "connectionString".
3. Triage: Findings are rated (Black, Red, Yellow, Green) based on priority.

## Example Rules (.toml)

Exclude noisy directories:
[[ClassifierRules]]
EnumerationScope = "DirectoryEnumeration"
RuleName = "ExcludeLogs"
MatchAction = "Discard"
MatchLocation = "FilePath"
WordListType = "Contains"
WordList = ["node_modules", "temp", "cache"]

Find database configs:
[[ClassifierRules]]
EnumerationScope = "ContentsEnumeration"
RuleName = "DatabaseConfig"
MatchAction = "Snaffle"
MatchLocation = "FileContentAsString"
WordListType = "Contains"
WordList = ["sqlConnectionString", "db_password"]
Triage = "Red"

## Ultra Build
The Ultra version adds support for deep-parsing binary formats:
- MS Office: .docx, .xlsx, .pptx
- PDF: Documents and forms.
- Email: .eml archives.

## Performance Tuning
To reduce network load, utilize the -f (DFS) or -i (Targeted) flags. For large-scale audits, adjust thread counts via -x and limit file inspection size via -r.

## Support
For issues or new classification rules, please consult your internal security compliance team or the project repository.
