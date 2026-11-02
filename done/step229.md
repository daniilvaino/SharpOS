# step 229 — SBOM чужого кода, бенч сканеров, лицензии в образе

## Почему

OpenSSF Scorecard (v5.6.0) против github.com/daniilvaino/SharpOS, коммит
39efef6: Vulnerabilities — «7 existing vulnerabilities», все
`SixLabors.ImageSharp 3.1.7` из `vendor/Terminal.Gui/UICatalog/UICatalog.csproj`
— примера Terminal.Gui, который не собирается. Проверка — `osvscanner.DoScan`
(osv-scanner v2.5.1) по архиву репозитория: манифесты пакетов, SBOM, вложенные
git, вендоренный C/C++ по хешам. Вендоренный C# (`vendor/*`, порты BCL)
сканерам не виден: манифестов нет. Решение пользователя — глобальный SBOM для
релиза 0.1, затем бенч бесплатных сканеров (SCA, лицензии, SAST) без SBOM и с ним.

## Что сделано

- **`sbom.cdx.json`** в корне (CycloneDX 1.6; `*.cdx.json` узнают osv-scanner и
  Grant): 29 компонентов образа, вложенные — у SharpCompress (ZstdSharp, XZ.NET,
  managed-lzma, Bouncy Castle, DotNetZip, JZlib, zlib) и BabyKusto (t-digest).
  У каждого: версия или коммит upstream, SPDX-лицензия, `sharpos:where`.
  `pkg:nuget` — только где код равен опубликованному пакету (проверено по
  api.nuget.org): SharpCompress 0.50.7, Iced 1.21.0, PeNet 6.1.2, TurboXml 2.1.0,
  Terminal.Gui 1.19.0, Microsoft.Azure.Kusto.Language 12.4.1, ShellSyntaxTree 0.3.5,
  STJ/Encodings.Web/Pipelines 10.0.8. Остальным — ссылка на репозиторий и коммит
  в `pedigree`, без purl. MSVC CRT (libcmt) и Windows SDK — `optional` (только
  ядро с CoreCLR); PowerShell 7.6.5 — `optional`. Инструменты сборки —
  `metadata.tools`. Примеры и тесты внутри `vendor/Terminal.Gui`,
  `vendor/XtermSharp`, игровые `payloads/` — не в списке (`metadata.properties`).
- **Версии, которых не было в `PROVENANCE.md`**, — по хешам git-объектов против
  тегов upstream: Iced v1.21.0 (`Instr.cs` — с 1.21, `FormatterOptions.cs` — до
  1.22), PeNet 6.1.2 (`main` 3bcbdc92f9 отличается от тега только `PeNet.csproj`),
  TurboXml 2.1.0 (`main` на дату снимка — коммит тега). Записаны в их `PROVENANCE.md`.
- **Канарейка** — копия SBOM с System.Text.Json 8.0.0: osv-scanner, Trivy и Grype
  находят GHSA-8g4q-xg66-9fp4 / GHSA-hh2w-p6rv-4g7w (CVE-2024-30105, -43485).
  Нашла две ошибки SBOM: osv матчит по `name`+`version`, а не по purl (Kusto
  назывался `Kusto.Language` — не NuGet id), и `pkg:github` относит к GitHub
  Actions. Исправлено.
- **Лицензии, найденные ScanCode:** `SharpCompress/src/Compressors/Deflate/*` — не
  MIT, а DotNetZip (Dino Chiesa и Microsoft, **MS-PL**) с частями JZlib
  (BSD-3-Clause) и zlib; шапки ссылаются на «License.txt», которого upstream не
  кладёт. Текст MS-PL (SPDX) — `vendor/SharpCompress/LICENSE-MS-PL.txt`; SBOM,
  README, `PROVENANCE.md` — дописаны. Фрагмент Stack Overflow (CC BY-SA 4.0) в
  `Vector128/256.cs` из BCL — в SBOM.
- **Тексты лицензий в образе:** `run_build.ps1` кладёт `\sharpos\licence\` — по
  файлу на компонент SBOM (27): MIT/BSD/Apache/MS-PL просят их рядом с
  бинарником. Отсутствовавшие в дереве — добавлены: `std-no-runtime/LICENSE-dotnet-runtime.TXT`
  и `THIRD-PARTY-NOTICES-dotnet-runtime.TXT` (форк — в `.gitignore`, а порты
  BCL в std есть и без него), `Security/Cryptography/LICENSE-referencesource.txt`,
  `OS/src/Hal/LICENSE-MOOS.txt`, `LICENSE-font8x8.txt`. Нет источника (подмодуль
  не скачан) — предупреждение, не ошибка. `-SkipCoreClr` каталог оставляет.
- README «Вендоринг и библиотеки» — ссылка на SBOM и `\sharpos\licence\`;
  CLAUDE.md, правило вендоринга — пункт 7 (новый вендор — и в SBOM).
- donext: копии без правок (Iced, ManagedDoom) — в подмодули на upstream.

## Бенч сканеров

Ветка `bench/scanners` (6390c9e — рабочее дерево, 3b4258f — готовый SBOM),
`git archive` (без подмодулей, как архив GitHub); A — без SBOM, B — с ним.
Платное (Snyk, Socket, Endor, Aikido, Black Duck, Mend, FOSSA) — не гонялось.

| Инструмент | Что нашёл | SBOM |
|---|---|---|
| ScanCode 32.5.0 | 5328 файлов: MS-PL/JZlib/zlib в SharpCompress, CC BY-SA в векторах — **единственный**; ложные: «proprietary» в примере Wizard, BSD-1 в `Fat32.cs` (ссылка), TurboXml «unknown» | читает как текст |
| ORT 97.3.0 | 51 пакет с транзитивными и неявными (ILCompiler 8.0.27); NETCore.App 2.1.0 ×8 в `XtermSharp/Tests` — **единственный** | не понимает CycloneDX |
| OSV-Scanner 2.7.0 | ImageSharp ×7 (UICatalog) | сам из дерева, +10 NuGet, чисто |
| Scorecard 5.6.0 | 2.7; те же 7; SAST/Security-Policy 0 | через osv |
| Trivy 0.76.1 | `.csproj` не читает; Dockerfile UICatalog ×8; Zlib в SharpCompress | только `trivy sbom` |
| Grype 0.121 / Syft 1.55 | только 18 GitHub Actions | только `sbom:` |
| Grant 0.6.8 | LICENSE-файлы не узнаёт | только явно; 33 пакета |
| CodeQL 2.27.3 | 9212 о качестве; 10 «security» — ложные | — |
| Semgrep 1.183 | 3 ECB в тестовых векторах — ложные; `vendor/` пропускает | — |
| SecurityCodeScan 5.6.7 | 56 мест — реализация AES/Random/MD5 в порте BCL, ложные | — |
| FOSSology | не собран: `nomossa` требует PostgreSQL в CMake | — |

Уязвимостей в коде образа не нашёл никто. Запуск: всё в `C:\scanbench`
(результаты, сводка `results.md`) и `~/scanbench` в WSL.

## Проверка

`grant list sbom.cdx.json`: 33 пакета, без лицензии 0. osv-scanner по SBOM — 10
пакетов NuGet, чисто; по канарейке — обе GHSA. Блок `\sharpos\licence` в
`run_build.ps1`: разбор без ошибок, сухой прогон блока — 27 из 27 файлов,
175 КБ. Сборкой образа не прогонялось.

## Не сделано

- `SixLabors.ImageSharp 3.1.7` в UICatalog остаётся (пример Editor из
  UICatalog — основа редактора, release.md §3).
- Коммит ManagedDoom не записан (donext, вместе с подмодулем).
- Лицензии внутри каждого PE (ресурс) — не делали; хватает каталога на ESP.

## Файлы

`sbom.cdx.json` (новый), `vendor/SharpCompress/LICENSE-MS-PL.txt`,
`std-no-runtime/LICENSE-dotnet-runtime.TXT`,
`std-no-runtime/THIRD-PARTY-NOTICES-dotnet-runtime.TXT`,
`std-no-runtime/Security/Cryptography/LICENSE-referencesource.txt`,
`OS/src/Hal/LICENSE-MOOS.txt`, `OS/src/Hal/LICENSE-font8x8.txt` (новые);
`run_build.ps1`, `README.md`, `CLAUDE.md`, `release.md`, `donext.md`,
`vendor/{Iced,PeNet,TurboXml,SharpCompress}/PROVENANCE.md`.
