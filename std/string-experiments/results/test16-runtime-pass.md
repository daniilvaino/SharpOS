# EXP_TEST_16 Runtime Result

Date: 2026-04-02

Command:

```powershell
./build_app_freestanding_wsl.ps1 -DefineConstants EXP_TEST_16
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
test_id=16
test_result=4
[info] process exit code = 21
[info] exit source = service
```

Conclusion:
- `new string(char, int)` path executes successfully in freestanding app runtime for `EXP_TEST_16`.
- Current `EXP_TEST_16` checks:
  - `Length == 4`
  - all characters are `'A'`
  - `fixed(char* p = s)` has null terminator at `p[4]`
  - success code remains `test_result=4`
