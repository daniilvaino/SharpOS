# EXP_TEST_11 Runtime Result

Date: 2026-04-02

Command:

```powershell
./build_app_freestanding_wsl.ps1 -DefineConstants EXP_TEST_11
& "C:\Program Files\PowerShell\7\pwsh.exe" -Command "./run_build.ps1"
```

Outcome:
- build: `pass`
- run: `pass`

Observed COM1 lines:

```text
[info] app run start: \EFI\BOOT\HELLOCS.ELF
string exp start
abi=2
test_id=11
test_result=423
[info] process exit code = 21
[info] exit source = service
```

Conclusion:
- UTF-8 encoding path over `string` indexer (`EXP_TEST_11`) works in runtime.
