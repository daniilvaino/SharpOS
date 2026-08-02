<#
.SYNOPSIS
  Builds termrace and runs the shitty corpus against both C# terminal engines.

.EXAMPLE
  ./run.ps1                                  # default suites, both engines
  ./run.ps1 -Suite all -Json results.json    # everything, machine-readable
  ./run.ps1 -Suite all -Label matchcolor      # results land in runs/<timestamp>-matchcolor/
  ./run.ps1 -Suite xtermjs -Engine xtermnet -Filter DECSTBM
#>
param(
	[string] $Suite = "xtermjs,libvterm,corpus",
	[string] $Engine = "",
	[string] $Filter = "",
	[int]    $Limit = 0,
	[string] $Json = "",
	[string] $Out = "",
	[string] $Label = "",
	[switch] $Quiet,
	[switch] $Verbose,
	[switch] $NoBuild
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$project = Join-Path $root "src/TermRace/TermRace.csproj"
$tests = Join-Path $root "../shitty/tests"

if (-not $NoBuild) {
	# XTerm.NET packs on build by default; the pack step is noise for a test run.
	dotnet build $project -c Release -p:GeneratePackageOnBuild=false -v q --nologo
	if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$arguments = @("--tests", $tests, "--suite", $Suite)
if ($Engine)  { $arguments += @("--engine", $Engine) }
if ($Filter)  { $arguments += @("--filter", $Filter) }
if ($Limit)   { $arguments += @("--limit", $Limit) }
if ($Json)    { $arguments += @("--json", $Json) }
if ($Out)     { $arguments += @("--out", $Out) }
if ($Label)   { $arguments += @("--label", $Label) }
if ($Quiet)   { $arguments += "--quiet" }
if ($Verbose) { $arguments += "--verbose" }

dotnet run --project $project -c Release --no-build -- @arguments
exit $LASTEXITCODE
