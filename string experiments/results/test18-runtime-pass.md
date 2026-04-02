# EXP_TEST_18 Runtime Result

Date: 2026-04-02

Command:

```powershell
./build_app_freestanding_wsl.ps1 -DefineConstants EXP_TEST_18
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
test_id=18
test_result=2
[info] process exit code = 21
[info] exit source = service
```

Conclusion:
- `string.Concat` path used by `s + "b"` is working in current freestanding runtime profile.
