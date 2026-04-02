# String Runtime Matrix Results

Дата: 2026-04-02 22:30:00

| test | name | symbol | expected | actual id | actual result | app exit | status | reason |
|---|---|---|---:|---:|---:|---:|---|---|
| 01 | Length | EXP_TEST_01 | 3 | 1 | 3 | 21 | pass | - |
| 02 | Indexer_FirstChar | EXP_TEST_02 | 97 | 2 | 97 | 21 | pass | - |
| 03 | Indexer_LoopSum | EXP_TEST_03 | 294 | 3 | 294 | 21 | pass | - |
| 09 | AsciiEncode_Indexer | EXP_TEST_09 | 198 | 9 | 198 | 21 | pass | - |
| 10 | Utf16LeEncode_Indexer | EXP_TEST_10 | 131 | 10 | 131 | 21 | pass | - |
| 11 | Utf8Encode_Bmp_NoPin | EXP_TEST_11 | 423 | 11 | 423 | 21 | pass | - |
| 12 | FixedString | EXP_TEST_12 | 294 | 12 | 294 | 21 | pass | - |
| 13 | GetPinnableReference | EXP_TEST_13 | 97 | 13 | 97 | 21 | pass | - |
| 16 | NewStringRepeatChar | EXP_TEST_16 | 4 | 16 | 4 | 21 | pass | temporary RhNewString bridge active |
| 18 | ConcatVariableLiteral | EXP_TEST_18 | 2 | 18 | 2 | 21 | pass | temporary RhNewString bridge active |
| 90 | StringLayoutDiagnostics | EXP_TEST_90 | 1 | 90 | 1 | 21 | pass | layout invariants confirmed |

Summary: pass=11, fail=0

