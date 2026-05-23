# Step 105 — ExecStubBuffer collision (BigStack vs InterfaceDispatchBridge)

## Корень

Оба `BigStack` и `InterfaceDispatchBridge` писали shellcode в **один и тот же**
`ExecStubBuffer[128..]`. Размеры:

- `InterfaceDispatchBridge.StubOffset = 128`, `MaxStubSize = 384` → пишет в `[128..512)`
- `BigStack.StubOffset = 128`, `StubSize = 32` → пишет в `[128..160)`

Phase 2 (init) — bridge пишет 195-байтный шеллкод resolver'а в `[128..323)`.
Phase 4/5 (перед CoreCLR) — BigStack пишет свой 32-байтный шеллкод в `[128..160)`,
**перетирая** первые 32 байта bridge'а.

Каждый interface call внутри CoreCLR'а теперь прыгает на offset 128 → выполняет
**BigStack**'овский шеллкод вместо bridge resolver'а:
```
push rbp; mov rbp, rsp; mov rsp, rcx;   ← RSP := this (catastrophic — stack теперь в куче)
sub rsp, 0x20; call rdx                 ← прыжок куда попало
```

Стек хвостом смотрит в heap, call'ы прыгают на mangled адреса. Heap пухнет
garbage'ом → GC при обходе натыкается на "объект" в JIT-stub VA → fault.
Regex.IsMatch — последняя капля (он interface-dispatch-heavy после
ConcurrentDictionary lazy-init).

## Фикс

BigStack теперь живёт в **собственном** 64-байтном `EfiLoaderCode` буфере
(`BootInfo.BigStackStubBuffer`). Bridge получает весь свой [128..512) обратно.

- `UefiBootInfoBuilder.cs` — `AllocatePool(EfiLoaderCode, 64, &bigStackStubAlloc)` рядом с тремя другими EfiLoaderCode allocation'ами
- `BootInfo.cs` — добавлены поля `BigStackStubBuffer` / `BigStackStubBufferSize`
- `BigStack.cs` — убран `StubOffset = 128`, теперь stub пишется с offset 0 в свой собственный буфер
- `BootSequence.cs:RunCoreClrSession` — передаёт `bootInfo.BigStackStubBuffer` вместо `bootInfo.ExecStubBuffer`

Дизайн ровно тот же что и для других EfiLoaderCode pool'ов (ExecStubBuffer,
JumpStubExecBuffer, IDT buffer) — гарантированно executable даже при W^X.

## Lesson learned

Layout shared-buffer'ов — это **owner contract**. Bridge legitimately claim'ит
`[128..512)`. BigStack залезал на чужую территорию step72-design'ом, который не
учёл что bridge туда уже пишет. Дизайн исправлен — у каждого свой буфер.

## Tail issue

Расследование велось через memory dump (QMP `pmemsave`) и Python parser
(`tools/heap_dump_analyze.py`) — оставлены в дереве для будущих heap walks.
