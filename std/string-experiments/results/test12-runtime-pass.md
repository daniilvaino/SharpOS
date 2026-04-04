# EXP_TEST_12 Runtime Result

Date: 2026-04-02

Command:

```powershell
./build_app_freestanding_wsl.ps1 -DefineConstants EXP_TEST_12
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
test_id=12
test_result=294
[info] process exit code = 21
[info] exit source = service
```

Conclusion:
- `fixed(char* p = s)` path is working for non-empty string.
