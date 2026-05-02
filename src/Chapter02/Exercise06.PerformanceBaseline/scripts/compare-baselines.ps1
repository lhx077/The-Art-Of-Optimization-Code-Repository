# ---------------------------------------------------------------------------
# compare-baselines.ps1 — Automated Performance Regression Detection
# ---------------------------------------------------------------------------
# This script automates performance regression detection by comparing two
# BenchmarkDotNet JSON exports: a known-good baseline and the current run.
# In a CI pipeline, this would execute after each build and fail the pipeline
# if any benchmark exceeds the regression threshold, preventing performance
# degradations from reaching production undetected.
#
# Usage:
#   pwsh compare-baselines.ps1 -BaselinePath baseline.json -CurrentPath current.json
#   pwsh compare-baselines.ps1 -BaselinePath baseline.json -CurrentPath current.json -Threshold 0.10
#
# Exit codes:
#   0 — All benchmarks within acceptable range
#   1 — One or more benchmarks exceeded the regression threshold
#   2 — Script error (missing files, parse failure, etc.)
# ---------------------------------------------------------------------------

param(
    [Parameter(Mandatory = $true, HelpMessage = "Path to the baseline BenchmarkDotNet JSON result file.")]
    [string]$BaselinePath,

    [Parameter(Mandatory = $true, HelpMessage = "Path to the current BenchmarkDotNet JSON result file.")]
    [string]$CurrentPath,

    [Parameter(HelpMessage = "Regression threshold as a decimal fraction (default 0.20 = 20%).")]
    [double]$Threshold = 0.20
)

# ---------------------------------------------------------------------------
# Validate input files exist before attempting to parse them.
# ---------------------------------------------------------------------------
if (-not (Test-Path $BaselinePath)) {
    Write-Error "Baseline file not found: $BaselinePath"
    exit 2
}
if (-not (Test-Path $CurrentPath)) {
    Write-Error "Current results file not found: $CurrentPath"
    exit 2
}

# ---------------------------------------------------------------------------
# Parse the BenchmarkDotNet JSON exports. The "Full" JSON exporter produces
# a structure with a top-level "Benchmarks" array. Each element contains a
# "FullName" (the fully qualified benchmark method name) and a "Statistics"
# object whose "Mean" field holds the average execution time in nanoseconds.
# ---------------------------------------------------------------------------
try {
    $baselineData = Get-Content -Path $BaselinePath -Raw | ConvertFrom-Json
    $currentData  = Get-Content -Path $CurrentPath  -Raw | ConvertFrom-Json
}
catch {
    Write-Error "Failed to parse JSON files: $_"
    exit 2
}

# ---------------------------------------------------------------------------
# Build lookup tables keyed by benchmark method name. We use the "Method"
# field for a concise display name and fall back to "FullName" if "Method"
# is unavailable. The mean execution time is extracted from the Statistics
# object.
# ---------------------------------------------------------------------------
$baselineLookup = @{}
foreach ($b in $baselineData.Benchmarks) {
    $name = if ($b.Method) { $b.Method } else { $b.FullName }
    $mean = $b.Statistics.Mean
    $baselineLookup[$name] = $mean
}

$currentLookup = @{}
foreach ($b in $currentData.Benchmarks) {
    $name = if ($b.Method) { $b.Method } else { $b.FullName }
    $mean = $b.Statistics.Mean
    $currentLookup[$name] = $mean
}

# ---------------------------------------------------------------------------
# Compare each benchmark present in both the baseline and current results.
# Calculate the percentage change and flag regressions that exceed the
# configured threshold.
# ---------------------------------------------------------------------------
$regressionDetected = $false
$results = @()

foreach ($name in $baselineLookup.Keys | Sort-Object) {
    if (-not $currentLookup.ContainsKey($name)) {
        Write-Warning "Benchmark '$name' found in baseline but missing from current results — skipped."
        continue
    }

    $baselineNs = $baselineLookup[$name]
    $currentNs  = $currentLookup[$name]

    # Guard against division by zero (should not happen with valid data).
    if ($baselineNs -eq 0) {
        $changePercent = 0.0
    }
    else {
        $changePercent = ($currentNs - $baselineNs) / $baselineNs
    }

    $status = "OK"
    if ($changePercent -gt $Threshold) {
        $status = "REGRESSION"
        $regressionDetected = $true
    }
    elseif ($changePercent -lt -$Threshold) {
        $status = "IMPROVED"
    }

    $results += [PSCustomObject]@{
        Benchmark    = $name
        BaselineNs   = [math]::Round($baselineNs, 2)
        CurrentNs    = [math]::Round($currentNs, 2)
        ChangePercent = ("{0:+0.00%;-0.00%;0.00%}" -f $changePercent)
        Status       = $status
    }
}

# ---------------------------------------------------------------------------
# Report benchmarks present in the current run but absent from the baseline.
# These are new benchmarks that have no reference point for comparison.
# ---------------------------------------------------------------------------
foreach ($name in $currentLookup.Keys | Sort-Object) {
    if (-not $baselineLookup.ContainsKey($name)) {
        $results += [PSCustomObject]@{
            Benchmark    = $name
            BaselineNs   = "N/A"
            CurrentNs    = [math]::Round($currentLookup[$name], 2)
            ChangePercent = "N/A"
            Status       = "NEW"
        }
    }
}

# ---------------------------------------------------------------------------
# Output the comparison table. Format-Table provides a clean, aligned display
# suitable for both terminal output and CI build logs.
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "=========================================================================="
Write-Host " Performance Baseline Comparison Report"
Write-Host "=========================================================================="
Write-Host " Baseline : $BaselinePath"
Write-Host " Current  : $CurrentPath"
Write-Host " Threshold: $([math]::Round($Threshold * 100, 1))%"
Write-Host "=========================================================================="
Write-Host ""

$results | Format-Table -AutoSize -Property @(
    @{ Label = "Benchmark";      Expression = { $_.Benchmark } },
    @{ Label = "Baseline (ns)";  Expression = { $_.BaselineNs }; Alignment = "Right" },
    @{ Label = "Current (ns)";   Expression = { $_.CurrentNs };  Alignment = "Right" },
    @{ Label = "Change (%)";     Expression = { $_.ChangePercent }; Alignment = "Right" },
    @{ Label = "Status";         Expression = { $_.Status } }
)

# ---------------------------------------------------------------------------
# Summary and exit code. In a CI pipeline, a non-zero exit code causes the
# build step to fail, preventing the merge of a performance-regressing change.
# ---------------------------------------------------------------------------
$regressionCount = ($results | Where-Object { $_.Status -eq "REGRESSION" }).Count
$improvedCount   = ($results | Where-Object { $_.Status -eq "IMPROVED" }).Count
$newCount        = ($results | Where-Object { $_.Status -eq "NEW" }).Count

Write-Host "--------------------------------------------------------------------------"
Write-Host " Summary: $($results.Count) benchmarks compared"
Write-Host "   OK:         $($results.Count - $regressionCount - $improvedCount - $newCount)"
Write-Host "   Improved:   $improvedCount"
Write-Host "   Regression: $regressionCount"
Write-Host "   New:        $newCount"
Write-Host "--------------------------------------------------------------------------"

if ($regressionDetected) {
    Write-Host ""
    Write-Host "RESULT: FAIL — $regressionCount benchmark(s) exceeded the $([math]::Round($Threshold * 100, 1))% regression threshold." -ForegroundColor Red
    Write-Host "Review the flagged benchmarks above and investigate before merging."
    Write-Host ""
    exit 1
}
else {
    Write-Host ""
    Write-Host "RESULT: PASS — All benchmarks within acceptable range." -ForegroundColor Green
    Write-Host ""
    exit 0
}
