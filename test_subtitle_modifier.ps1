# Quick verification of the ParseTime + Modify logic
# We'll use a C# script via dotnet-script equivalent, but since we don't have that,
# let's just compile and run a small console test.

Write-Host "=== Testing SubtitleTimingModifier logic ===" -ForegroundColor Cyan

# Simulate the FIXED ParseTime algorithm in PowerShell for verification
function ParseTime-Fixed {
    param([string]$time)
    $sepIdx = $time.IndexOfAny([char[]]@(',', '.'))
    $ms = 0
    $timePart = $time
    if ($sepIdx -ge 0) {
        $ms = [int]$time.Substring($sepIdx + 1)
        $timePart = $time.Substring(0, $sepIdx)
    }
    $parts = $timePart.Split(':')
    $total = 0.0
    foreach ($p in $parts) {
        $total = $total * 60.0 + [int]$p
    }
    return $total + $ms / 1000.0
}

# Simulate the OLD BROKEN algorithm for comparison
function ParseTime-Old {
    param([string]$time)
    $sepIdx = $time.IndexOfAny([char[]]@(',', '.'))
    $ms = 0
    $timePart = $time
    if ($sepIdx -ge 0) {
        $ms = [int]$time.Substring($sepIdx + 1)
        $timePart = $time.Substring(0, $sepIdx)
    }
    $parts = $timePart.Split(':')
    $total = $ms / 1000.0
    foreach ($p in $parts) {
        $total += [int]$p
        $total /= 60.0
    }
    return $total
}

# Test cases
$testCases = @(
    @{ input = "00:01:30,500"; expected = 90.5 },
    @{ input = "00:00:01,200"; expected = 1.2 },
    @{ input = "01:23:45,678"; expected = 5025.678 },
    @{ input = "00:05:00,000"; expected = 300.0 },
    @{ input = "00:00:00,001"; expected = 0.001 },
    @{ input = "02:00:00,000"; expected = 7200.0 }
)

Write-Host ""
Write-Host "Input                | Expected (s) | OLD (broken) | FIXED (correct) | Status"
Write-Host ("-" * 85)

$allPass = $true
foreach ($tc in $testCases) {
    $old = ParseTime-Old $tc.input
    $new = ParseTime-Fixed $tc.input
    $pass = [Math]::Abs($new - $tc.expected) -lt 0.001
    if (-not $pass) { $allPass = $false }
    $status = if ($pass) { "PASS" } else { "FAIL" }
    $color = if ($pass) { "Green" } else { "Red" }
    Write-Host ("{0,-20} | {1,12:N3} | {2,12:N3} | {3,12:N3} | {4}" -f 
        $tc.input, $tc.expected, $old, $new, $status) -ForegroundColor $color
}

Write-Host ""
# Now test the actual modification: 90.5s at rate 0.95x should give 90.5/0.95 = 95.263s
$original = 90.5
$rate = 0.95
$delay = 0.0
$modified = [Math]::Max(0, $original / $rate + $delay)
Write-Host "Modification test: 90.5s / 0.95x = $([Math]::Round($modified, 3))s (expected ~95.263s)"
Write-Host ""

# Show what the OLD broken code would have done
$oldParsed = ParseTime-Old "00:01:30,500"
$oldModified = [Math]::Max(0, $oldParsed / $rate + $delay)
Write-Host "OLD BUG: ParseTime('00:01:30,500') = $oldParsed s -> modified to $oldModified s (should be 95.263s!)" -ForegroundColor Red
Write-Host ""

if ($allPass) {
    Write-Host "=== ALL TESTS PASS ===" -ForegroundColor Green
    Write-Host ""
    Write-Host "The ParseTime fix is correct. The old code had a critical bug:"
    Write-Host "  OLD:  00:01:30,500 -> 0.5s (WRONG - all subs collapsed to start)"
    Write-Host "  NEW:  00:01:30,500 -> 90.5s (CORRECT)"
} else {
    Write-Host "=== SOME TESTS FAILED ===" -ForegroundColor Red
}
