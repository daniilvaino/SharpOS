# step 228 — `project` в KQL не стирает типы записей

## Почему

Пользователь: «project стирает типы и превращает их в Expando — `project Build`
не даёт тип, а даёт Expando». `FETCH | KQL 'project Build'` отдавал
`Expando { Build = Expando {…} }`: всё, кроме тождественного `project` и запросов
из `where`/`take`/`sort`/`extend` на месте, собирал `Materialize`, а вложенная
запись шла туда через `dynamic` — `JsonObject` на входе, `Expando` на выходе.
Решение пользователя: одна колонка-запись — сообщение и есть эта запись; при
нескольких колонках — Expando, записи внутри своих типов. Делать как правки на
месте — из блока, без перевода в кучу.

## Что сделано

- **std, `ExpandoEdits`:** обход достижимого (`Reach`, `GraphBytes`, копия со
  сдвигом ссылок — бывший `Compact`) идёт от любой записи любого блока.
  На нём:
  - `ExpandoEdits.CopyOut(View)` — граф записи в новый блок обмена, запись — его
    корень: сообщение её собственного типа по той же схеме;
  - `region.SetGraph(expando, name, View)` — граф записи другого блока в хвост
    этого (место — как для строк: хвост, рост, уплотнение), элемент Expando
    ссылается на копию. Тип каждой записи графа должен быть в описании блока.
- **std:** `RegionSchema.Merge` — два описания в одно (ключ в обоих — один тип);
  `RawRegion.Lay(message, shapes)` — сообщение своего каталога в блок, читаемый
  по склеенному описанию; `View.IsRecord`.
- **KQL (`Projection`, `Projected`):** над классом `project`, `project-keep`,
  `-away`, `-rename`, `-reorder` в конце запроса после `where`/`take`/`sort`.
  Запрос идёт с номером строки, протащенным через `project` (`, __row`), строка
  находит своё сообщение. Колонка — поле-запись входа (объект, массив) под своим
  или новым именем (`B = Build`) — берётся графом из блока. Одна такая колонка —
  `CopyOut`, тип выхода — тип поля из схемы входа; иначе Expando: скаляры и
  вычисленные колонки — как прежде, записи — `SetGraph`, описание выхода —
  каталог KQL и схема входа.
- README и limits: строки KQL; в README заодно снято устаревшее «без
  `matches regex`, `extract()`» (вернулись в step225).

## Проверка

Рабочая копия, WHPX, прогоны 250–251 (пересобран KqlApp):
- `FETCH | KQL 'project Build'` → `BuildFacts {…}`; следующий `KQL --complete
  'where Fo'` дополняет `Fork` — тип объявлен на трубе;
  `project B = Build` — то же (`HostedRuntime`);
- `project Cpu | KQL 'project Vendor, Processors'` — поля `CpuFacts`;
- `project Storage` → `StorageFacts[1]`;
- `project Build, Cpu, N = 1`, `project-keep Build, Cpu`, `project-away …` —
  Expando с `BuildFacts`/`CpuFacts` внутри;
- `where 1 == 0 | project Build | PIPECNT` → 0;
- `SAMPLE_PIPEGEN 3 | KQL 'project Level, Text'` — по-прежнему `LogEntry`.
Полной батареей не прогонялось.

## Не сделано

Записи по-прежнему становятся Expando через `JsonNode`, где строки результата
не сообщения входа или вход — Expando: `project` после `summarize`/`extend`/
`project-reorder`; `extend` с `dynamic` над Expando (`extend P = Cpu.Processors`
пересобирает сообщение); одна колонка-запись над Expando даёт Expando с ней.
Одна скалярная колонка (`project Text`) — Expando, как и была.

## Файлы

std: `Pipes/ExpandoEdits.cs`, `Pipes/View.cs`, `Pipes/Pipe.cs`,
`Exchange/RegionSchema.cs`. KQL: `apps_native/KqlApp/Program.cs`. Документы:
`README.md`, `docs/nativeaot-nostd-kernel-limits.md`.
