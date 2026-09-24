{
  # Сборка SharpOS на NixOS (и на любом Linux с nix).
  #
  #   nix develop            ядро, приложения, образ, запуск в QEMU
  #   nix develop .#fork     форк CoreCLR (FHS: Arcade качает свой SDK и
  #                          инструменты — обычные бинарники под /lib64)
  #   nix run .#fork -- -c "…"   то же самое одной командой, для скриптов
  #
  # Собирают прежние скрипты; окружение только даёт им инструменты и проверяется
  # через tools/Toolchain.ps1 по toolchain.json. Это альтернатива mise.toml для
  # тех, у кого nix; одновременно они не нужны.
  #
  # Версия LLVM в nixpkgs (22.1.8) совпадает с toolchain.json. Версия SDK —
  # любая 10.0.x: ядро и apps_native NoStdLib, кодогенерацию задаёт ILCompiler,
  # он закреплён в SharpOsNativeLink.props, а apps_managed привязаны к рантайму
  # форка (apps_managed/Directory.Build.props).

  description = "SharpOS build environment";

  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";

  outputs =
    { self, nixpkgs }:
    let
      systems = [ "x86_64-linux" "aarch64-linux" ];
      forAll = f: nixpkgs.lib.genAttrs systems (system: f nixpkgs.legacyPackages.${system});

      # tools/Toolchain.ps1 ищет clang-cl, lld-link, llvm-lib и llvm-rc в одном
      # каталоге (SHARPOS_LLVM_BIN) — так они и лежат в архивах llvm.org. В
      # nixpkgs они разнесены по трём пакетам, поэтому собираем их вместе.
      #
      # Инструменты берутся без обёрток nixpkgs: обёртка получает пути к libc и
      # gcc из окружения stdenv, которого в FHS нет, — зато там есть привычная
      # раскладка /usr, по которой clang сам находит и заголовки, и gcc (его
      # crtbeginS.o и libgcc нужны, когда хостовая часть линкуется в ELF).
      llvmBinFor =
        pkgs:
        "${
          pkgs.symlinkJoin {
            name = "sharpos-llvm-22.1.8";
            paths = with pkgs.llvmPackages_22; [ clang-unwrapped lld llvm ];
          }
        }/bin";

      # JWasm — ассемблер MASM-исходников форка, коммит из toolchain.json.
      # Готовых сборок нет; собирается тем же clang 22, что и всё остальное.
      jwasmFor =
        pkgs:
        pkgs.stdenv.mkDerivation {
          pname = "jwasm";
          version = "2.21-unstable-7f6f32e";
          src = pkgs.fetchFromGitHub {
            owner = "Baron-von-Riedesel";
            repo = "JWasm";
            rev = "7f6f32e78b79565d40bcce496756aadd1ff66900";
            hash = "sha256-/hvDuy+ERssc9m8W5VshsZFoTapkBMm8YCKR9Yk83bE=";
          };
          nativeBuildInputs = [ pkgs.llvmPackages_22.clang ];
          buildPhase = ''
            runHook preBuild
            clang -D__UNIX__ -std=gnu99 -DNDEBUG -O2 -w -Isrc/H \
              $(ls src/*.c | grep -v trmem) -o jwasm
            runHook postBuild
          '';
          installPhase = ''
            runHook preInstall
            install -Dm755 jwasm $out/bin/jwasm
            runHook postInstall
          '';
          meta.mainProgram = "jwasm";
        };

      # Форк CoreCLR — в FHS: его Arcade скачивает свой .NET SDK, собирает
      # crossgen2 и прочие инструменты, и всё это обычные бинарники,
      # ожидающие /lib64/ld-linux-x86-64.so.2.
      forkEnvFor =
        pkgs:
        pkgs.buildFHSEnv {
          name = "sharpos-fork";
          targetPkgs = p: [
            p.llvmPackages_22.clang-unwrapped
            p.llvmPackages_22.lld
            p.llvmPackages_22.llvm
            # Хостовая часть сборки (кросс-компоненты: clrjit для linux-x64,
            # которым crossgen2 компилирует CoreLib) линкуется в обычный ELF —
            # нужны ld, стартовые объекты и libgcc. gcc-unwrapped кладёт их в
            # /usr/lib/gcc/<триплет>/<версия>, где clang их и ищет.
            p.binutils
            p.gcc-unwrapped
            p.glibc.dev
            (jwasmFor p)
            p.powershell
            p.cmake
            p.ninja
            p.python3
            p.git
            p.which # его ищут скрипты Arcade
            # Хостовой части нужны и заголовки: в nixpkgs они в отдельном
            # выходе .dev, в /usr/include FHS попадают только оттуда.
            p.curl
            p.icu
            p.icu.dev
            p.openssl
            p.openssl.dev
            p.krb5
            p.krb5.dev
            p.lttng-ust
            p.lttng-ust.dev
            p.liburcu # urcu/compiler.h, его включает lttng/tracepoint.h
            p.liburcu.dev
            p.libuuid.dev
            p.numactl
            p.zlib
            p.zlib.dev
            p.libunwind
            p.libunwind.dev
            p.xwin
          ];
          profile = ''
            export SHARPOS_LLVM_BIN=${llvmBinFor pkgs}
            export SHARPOS_JWASM=${pkgs.lib.getExe (jwasmFor pkgs)}
            cat <<'EOF'
      SharpOS: форк CoreCLR (FHS).

        xwin --accept-license --cache-dir .xwin-cache --manifest-version 17 \
             --sdk-version 10.0.26100 --crt-version 14.44.17.14 --arch x86_64 \
             splat --preserve-ms-arch-notation --include-debug-libs \
             --output .xwin-cache/splat          заголовки и библиотеки MSVC,
                                                 если splat ещё не сделан
        cd dotnet-runtime-sharpos && pwsh ./build_clr_sharpos.ps1 -Clean
      EOF
          '';
          # bash с аргументами: `nix run .#fork -- -c "…"` выполняет команду
          # в FHS. `nix develop .#fork --command` так не умеет — внутрь
          # bubblewrap попадает только runScript.
          runScript = "bash";
        };
    in
    {
      packages = forAll (pkgs: {
        jwasm = jwasmFor pkgs;
        fork = forkEnvFor pkgs;
        default = jwasmFor pkgs;
      });

      devShells = forAll (pkgs: {
        # Ядро, приложения, образ — настоящий nix.
        default = pkgs.mkShell {
          packages = [
            pkgs.dotnetCorePackages.sdk_10_0_1xx
            pkgs.powershell
            pkgs.llvmPackages_22.lld # lld-link — им сшиваются ядро и приложения
            pkgs.qemu # прошивку UEFI (edk2-x86_64-code.fd) несёт он же
            pkgs.mtools
            pkgs.xwin # если splat ещё не сделан
            pkgs.git
          ];
          shellHook = ''
            export SHARPOS_LLVM_BIN=${pkgs.llvmPackages_22.lld}/bin
            export DOTNET_ROOT=${pkgs.dotnetCorePackages.sdk_10_0_1xx}/share/dotnet
            # ilc приезжает из NuGet обычным ELF и на nix не стартует. По этой
            # переменной SharpOsNativeLink.props правит его сам — после restore
            # и перед запуском (target SharpOsPatchNupkgs), вручную звать нечего.
            export SHARPOS_PATCH_NUPKGS=${pkgs.lib.getExe pkgs.dotnetCorePackages.patchNupkgs}
            cat <<'EOF'
      SharpOS: ядро, приложения, образ.

        ./build_launcher.ps1               и остальные build_*.ps1
        ./run_build.ps1 -UsbOnly           ядро, образ, QEMU

      Форк CoreCLR собирается в другой оболочке: nix develop .#fork
      EOF
          '';
        };

        fork = (forkEnvFor pkgs).env;
      });
    };
}
