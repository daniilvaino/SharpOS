# Phase 2 — WSL2 setup + dotnet fork plan

Concrete steps для starting Phase 2 spike. Per `done/phase6-architecture.md`
architectural decisions.

## Repository topology

Two separate repos, loose coupling:

```
~/work/SharpOS/                    ← THIS repo (Windows side, primary dev)
   OS/
   std/
   done/
   ...

# WSL2 Ubuntu 24.04:
~/work/sharpos-wsl/                 ← Mirror (kept in sync)
   (Same content as Windows side, can use git or symlink)

~/work/dotnet-runtime-sharpos/      ← Fork repo (separate from SharpOS)
   src/coreclr/
      pal/
         linux/                     ← upstream, не touch
         sharpos/                   ← NEW directory (our patches)
         sharposhost-backend/       ← NEW directory (Linux backend для spike)
   ...
```

**Why two repos**:
- SharpOS repo: strict Invariant 1 (C# only), small fast iteration
- CoreCLR fork: C/C++ heavy, slow rebuild, separate upstream tracking

**Coupling pattern**:
- SharpOS publishes `libsharposhost.a` + `sharposhost.h` somewhere accessible
- CoreCLR fork's CMake reads `-DSHARPOSHOST_LIB=path/to/libsharposhost.a`
  + include path для sharposhost.h
- Iteration: rebuild SharpOS C# → rebuild fork pal/sharpos → re-link
  libcoreclr.so → run hello

## Step 0: WSL2 setup

```bash
# Windows side (PowerShell as Admin):
wsl --install -d Ubuntu-24.04
wsl -d Ubuntu-24.04

# Inside WSL2:
sudo apt update
sudo apt install -y \
    cmake ninja-build clang lld llvm \
    python3 python3-venv \
    git git-lfs \
    build-essential \
    libicu-dev liblttng-ust-dev libunwind-dev libssl-dev \
    libkrb5-dev libnuma-dev \
    curl wget

# .NET 10 SDK:
wget https://dot.net/v1/dotnet-install.sh
chmod +x dotnet-install.sh
./dotnet-install.sh --channel 10.0
echo 'export PATH="$HOME/.dotnet:$PATH"' >> ~/.bashrc
echo 'export DOTNET_ROOT="$HOME/.dotnet"' >> ~/.bashrc
source ~/.bashrc
dotnet --version  # confirm 10.0.x
```

## Step 1: SharpOS repo access in WSL2

Two options:

**Option A: Cross-OS git clone** (cleaner, recommended):
```bash
mkdir -p ~/work
cd ~/work
git clone /mnt/c/work/OS/.git sharpos-wsl
# Or clone fresh from origin if SharpOS is on GitHub
```

**Option B: Direct mount** (faster but file IO over 9P is slow):
```bash
ln -s /mnt/c/work/OS ~/work/sharpos-wsl
```

Recommend Option A. Sync via git push/pull.

## Step 2: dotnet-runtime fork

```bash
cd ~/work
git clone https://github.com/dotnet/runtime.git dotnet-runtime-sharpos
cd dotnet-runtime-sharpos
git checkout release/10.0
git remote rename origin upstream
# Add your fork remote when GitHub fork created:
# git remote add origin https://github.com/<user>/dotnet-runtime-sharpos.git
```

**Decision**: создать GitHub fork от `dotnet/runtime` под user account?
Plus: track our patches in commits, easy diff vs upstream, public for
reference. Minus: requires GitHub repo.

For Phase 2 spike — **local clone достаточно**. GitHub fork позже когда
Phase 6 starts (if ever public).

## Step 3: Build CoreCLR Debug for Linux x64

First time: 3-6 hours. Subsequent rebuilds: 20-60 min depending on scope.

```bash
cd ~/work/dotnet-runtime-sharpos
./build.sh -c Debug -s clr -arch x64

# Output:
# artifacts/bin/coreclr/linux.x64.Debug/
#    libcoreclr.so
#    libclrjit.so
#    System.Private.CoreLib.dll
#    corerun
#    ...
```

**Sanity check** что stock CoreCLR работает:

```bash
# Build minimal Hello.dll
mkdir -p /tmp/hello
cd /tmp/hello

cat > Hello.csproj <<'XML'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
  </PropertyGroup>
</Project>
XML

cat > Program.cs <<'CS'
using System;
public static class Program
{
    public static int Main(string[] args)
    {
        Console.WriteLine("hello");
        return 42;
    }
}
CS

dotnet build -c Debug

# Run with our built CoreCLR + force JIT:
COMPlus_ReadyToRun=0 \
COMPlus_ZapDisable=1 \
COMPlus_TieredCompilation=0 \
~/work/dotnet-runtime-sharpos/artifacts/bin/coreclr/linux.x64.Debug/corerun \
    bin/Debug/net10.0/Hello.dll

# Expected: "hello" printed, exit code 42.
# If this works — baseline established.
```

## Step 4: pal/sharpos/ skeleton (~2-3 days)

Per `done/phase6-architecture.md` spec:

1. `cd ~/work/dotnet-runtime-sharpos/src/coreclr/pal/`
2. `cp -r linux sharpos`  (start from Linux PAL)
3. `mkdir sharposhost-backend`
4. Manually substitute bottom calls в pal/sharpos/*.cpp:
   - `mmap(...)` → `SharpOSHost_ReservePages(...)`
   - `pthread_create(...)` → `SharpOSHost_CreateThread(...)`
   - etc.
5. Create `sharposhost.h` (~40 declarations)
6. Create `sharposhost-backend/linux.c` — POSIX-backed implementation
   of SharpOSHost_* (~500 LOC)
7. CMake glue:

```cmake
# pal/sharpos/CMakeLists.txt
add_library(palsharpos OBJECT
    map/virtual.cpp
    thread/thread.cpp
    synch*/...
    file/file.cpp
    time/time.cpp
    signal/signal.cpp
    # ...
)

target_include_directories(palsharpos PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/../inc
    ${SHARPOSHOST_INCLUDE}
)

# Linux backend для spike:
add_library(sharposhost-backend STATIC
    ../sharposhost-backend/linux.c
)
```

Build invocation:
```bash
./build.sh -c Debug -s clr -arch x64 \
    --cmakeargs "-DSHARPOS_PAL=ON \
                 -DSHARPOSHOST_INCLUDE=/abs/path/to/sharposhost.h.dir \
                 -DSHARPOSHOST_LIB=/abs/path/to/libsharposhost.a"
```

(Exact CMake patches per Sage 2 Q4.1 в `done/phase2-sage-queries-followup.md`.)

## Step 5: SharpOS HOST publication

Add new project `OS/src/PAL/SharpOSHost.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PublishAot>true</PublishAot>
    <NativeLib>Static</NativeLib>
    <SelfContained>true</SelfContained>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>
</Project>
```

Each HOST primitive в C# через `[UnmanagedCallersOnly(EntryPoint="SharpOSHost_*")]`:

```csharp
public static unsafe class HostExports
{
    [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_ReservePages")]
    public static void* ReservePages(nuint size, nuint alignment, int* err)
    {
        // No allocation, no exceptions, no GC pressure.
        // Calls existing kernel APIs (KernelHeap, Pager).
        return KernelHeap.ReserveAddressSpace(size, alignment);
    }

    // ... 30-40 more ...
}
```

Publish:
```bash
cd ~/work/sharpos-wsl/OS/src/PAL
dotnet publish -c Release -r linux-x64
# Output: bin/Release/net10.0/linux-x64/native/libSharpOSHost.a
```

Path передаётся в CoreCLR fork build через `-DSHARPOSHOST_LIB=`.

## Step 6: Direct host wrapper

Per Sage 2 (`done/phase2-sage-queries-followup.md` Q4.6) — minimal C
host (~80 LOC) что:
1. Loads libsharposhost first
2. Calls `SharpOSHost_Initialize` explicitly
3. dlopen libcoreclr.so
4. Calls coreclr_initialize / execute_assembly / shutdown_2

Skeleton already provided в Sage 2 answer. Adapt to our path layout.

## Step 7: Iterate

Per `done/phase6-architecture.md` spike pass criteria:
1. SharpOSHost_Initialize succeeds
2. coreclr_initialize returns S_OK
3. JIT compile log appears (`COMPlus_JitDisasm=Program:Main`)
4. "hello" в stdout
5. Exit code 42
6. PAL counters reasonable (per Sage 2 expected ranges)

При failure — diagnostic table в `done/phase2-sage-queries-followup.md`
points к likely cause.

## Iteration cycle estimate

After initial setup + first successful run:
- Add new HOST primitive: bump table size + add C# function + add C++ wrapper в pal/sharpos
- libsharposhost.a rebuild: ~30 sec
- pal/sharpos relink в libcoreclr.so: 1-3 min (incremental)
- Hello run: <1 sec

Adding ~5 missing primitives per iteration → spike completes within 1-2 weeks.

## Decisions deferred

1. Whether to GitHub-fork dotnet-runtime publicly (post-spike, before Phase 6 starts)
2. EH path A/B/C choice (post-spike, on real measurements)
3. C++ stdlib subset strategy (post-spike, when Phase 6 bare-metal starts)
4. libunwind keep/drop (depends on EH path choice)

## What this enables

Successful spike validates Phase 6 architecture:
- HOST API design proven via Hello.dll round-trip
- Static init ordering pattern proven workable
- Function table iteration pattern proven
- Migration path к pure C# clear (incrementally as bottom-of-stack
  components migrate)

Phase 6 estimate refined: original 9-18 months, possibly aggressive
6-12 months if EH inversion (Path B) виable.
