# Builds the worker image (if needed) and runs chunk_floor.py inside it against the clip ladder.
#
# Reads HF_TOKEN out of deploy/.env without echoing it. Models are cached in a named volume, so
# the first run downloads several GB and later runs start warm.
#
#   .\run-local.ps1 -Build                      # build the image first
#   .\run-local.ps1                             # full ladder, 3 repeats
#   .\run-local.ps1 -ClipsDir clips-smoke -Repeats 1
#
# On a card with limited VRAM, drop -BatchSize. An 8 GB card runs the pipeline but is memory-bound
# and cannot sustain realtime (see docs/Streaming_Capture_and_Live_Transcript.md section 3.3).

param(
  [string] $ClipsDir  = "clips",
  [int]    $Repeats   = 3,
  [int]    $BatchSize = 16,
  [string] $Model     = "large-v3",
  [string] $JsonOut   = "",
  [string] $EnvFile   = "",
  [string] $Image     = "diariz-worker:bench",
  [switch] $Build
)

$ErrorActionPreference = "Stop"

$here     = $PSScriptRoot
$repoRoot = (Resolve-Path (Join-Path $here "..\..")).Path
if (-not $EnvFile) { $EnvFile = Join-Path $repoRoot "deploy\.env" }

if (-not (Test-Path $EnvFile)) { throw "env file not found: $EnvFile (copy deploy\.env.example)" }
$hf = (Get-Content $EnvFile | Where-Object { $_ -match '^\s*HF_TOKEN\s*=' } |
       Select-Object -First 1) -replace '^\s*HF_TOKEN\s*=\s*', ''
if (-not $hf) { throw "HF_TOKEN is not set in $EnvFile - pyannote diarization cannot load without it" }

$clips = Join-Path $here $ClipsDir
if (-not (Test-Path $clips)) { throw "no such clips dir: $clips (run make-audio.ps1 then slice.py)" }

if ($Build) {
  Write-Output "building $Image from src\Diariz.Worker ..."
  docker build -t $Image (Join-Path $repoRoot "src\Diariz.Worker")
  if ($LASTEXITCODE -ne 0) { throw "docker build failed" }
}

# $dockerArgs, not $args - $args is an automatic variable in PowerShell.
$dockerArgs = @(
  "run", "--rm", "--gpus", "all",
  "-e", "HF_TOKEN=$hf",
  "-e", "DEVICE=cuda",
  "-e", "COMPUTE_TYPE=float16",
  "-e", "BATCH_SIZE=$BatchSize",
  "-e", "WHISPER_MODEL=$Model",
  "-e", "ENABLE_SPEAKER_EMBEDDINGS=1",
  "-e", "ASR_BACKEND=whisperx",
  "-v", "diariz-bench-cache:/root/.cache",
  "-v", "${clips}:/clips:ro",
  "-v", "$here\chunk_floor.py:/bench/chunk_floor.py:ro",
  "-v", "${here}:/out"
)
$inner = "python3 /bench/chunk_floor.py /clips --repeats $Repeats"
if ($JsonOut) { $inner += " --json /out/$JsonOut" }
# Model downloads and whisperx emit progress bars on stderr; keep the summary readable by
# writing everything to a log inside the container and printing the filtered tail.
$dockerArgs += @($Image, "sh", "-c", "$inner > /tmp/bench.log 2>&1; grep -v 'B/s\]\|it/s\]' /tmp/bench.log | tail -50")

Write-Output "clips=$ClipsDir repeats=$Repeats batch=$BatchSize model=$Model"
& docker @dockerArgs
if ($LASTEXITCODE -ne 0) { throw "benchmark run failed (exit $LASTEXITCODE)" }
