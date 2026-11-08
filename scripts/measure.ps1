param(
    [string]$Dotnet = 'dotnet',
    [int]$Port = 5085,
    [switch]$ConcurrentMax
)

$ErrorActionPreference = 'Stop'
if (-not $env:ConnectionStrings__RegisterReconciliation) { throw 'Set ConnectionStrings__RegisterReconciliation to a fresh measurement database.' }
if (-not $env:Operators__ivan) { throw 'Set Operators__ivan before starting the measurement.' }
$headers = @{ 'X-Api-Key' = $env:Operators__ivan }

$project = Split-Path $PSScriptRoot -Parent
$dll = Join-Path $project 'src/RegisterReconciliation/bin/Release/net10.0/RegisterReconciliation.dll'
if (-not (Test-Path -LiteralPath $dll)) { throw 'Build with dotnet build -c Release first.' }

$data = Join-Path $project '.local/measure'
[IO.Directory]::CreateDirectory($data) | Out-Null
$internal = Join-Path $data 'internal.csv'
$external = Join-Path $data 'external.csv'
$header = 'external_id,amount,currency,occurred_at,order_reference'
$scale = if ($ConcurrentMax) { 3 } else { 1 }
$rowsPerFile = 50000 * $scale
$exactRows = 25000 * $scale
$matched = 45000 * $scale
$ambiguous = 10000 * $scale
$orderSuffix = if ($ConcurrentMax) { 'x' * 112 } else { '' }

foreach ($path in $internal, $external) {
    $file = [IO.StreamWriter]::new($path, $false, [Text.UTF8Encoding]::new($false))
    try {
        $file.NewLine = "`n"
        $file.WriteLine($header)
        for ($i = 1; $i -le $rowsPerFile; $i++) {
            if ($i -le $exactRows) {
                $file.WriteLine("exact-$i,12.25,USD,2026-02-01T10:00:00Z,order-$i$orderSuffix")
            } elseif ($i -le $matched) {
                if ($path -eq $external) {
                    $file.WriteLine("fallback-$i,24.50,USD,2026-02-01T10:03:00Z,order-$i$orderSuffix")
                } else {
                    $file.WriteLine(",24.50,USD,2026-02-01T10:00:00Z,order-$i$orderSuffix")
                }
            } else {
                $file.WriteLine(",6.00,USD,2026-02-01T10:00:00Z,hot-order$orderSuffix")
            }
        }
    } finally {
        $file.Dispose()
    }
}

function Invoke-Burst($Url, [scriptblock]$CreateContent) {
    $client = [Net.Http.HttpClient]::new()
    $client.Timeout = [TimeSpan]::FromMinutes(3)
    $requests = @()
    $accepted = @()
    $rejected = 0
    try {
        $pending = foreach ($attempt in 1..4) {
            $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post, $Url)
            $requests += $request
            $request.Headers.Add('X-Api-Key', $env:Operators__ivan)
            $request.Headers.ExpectContinue = $true
            $request.Content = & $CreateContent
            $client.SendAsync($request)
        }
        foreach ($task in $pending) {
            $response = $task.GetAwaiter().GetResult()
            try {
                if ([int]$response.StatusCode -eq 429) {
                    $rejected++
                } elseif ([int]$response.StatusCode -eq 201) {
                    $accepted += ($response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json)
                } else {
                    throw "Unexpected HTTP $([int]$response.StatusCode) for $Url."
                }
            } finally {
                $response.Dispose()
            }
        }
        if ($accepted.Count -ne 1 -or $rejected -ne 3) { throw "Expected one accepted request and three 429s for $Url." }
        $accepted[0]
    } finally {
        foreach ($request in $requests) { $request.Dispose() }
        $client.Dispose()
    }
}

$base = "http://127.0.0.1:$Port"
$hostProcess = Start-Process $Dotnet -ArgumentList ('"{0}" --urls {1}' -f $dll, $base) -PassThru `
    -WindowStyle Hidden -RedirectStandardOutput (Join-Path $data 'app.log') `
    -RedirectStandardError (Join-Path $data 'app.err.log')
try {
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        if ($hostProcess.HasExited) { throw "API stopped. See $data/app.log and app.err.log." }
        try {
            $ready = (Invoke-WebRequest "$base/health/ready" -TimeoutSec 2).StatusCode -eq 200
            if ($ready) { break }
        } catch {
            Start-Sleep -Milliseconds 200
        }
    }
    if (-not $ready) { throw 'API startup timed out.' }

    $timer = [Diagnostics.Stopwatch]::StartNew()
    if ($ConcurrentMax) {
        $left, $right = foreach ($source in 'internal', 'external') {
            $path = Join-Path $data "$source.csv"
            Invoke-Burst "$base/imports/$source" {
                $content = [Net.Http.StreamContent]::new([IO.File]::OpenRead($path))
                $content.Headers.ContentType = [Net.Http.Headers.MediaTypeHeaderValue]::new('text/csv')
                $content
            }
        }
    } else {
        $left = Invoke-RestMethod -Method Post -Uri "$base/imports/internal" -Headers $headers -InFile $internal -ContentType 'text/csv'
        $right = Invoke-RestMethod -Method Post -Uri "$base/imports/external" -Headers $headers -InFile $external -ContentType 'text/csv'
    }
    $timer.Stop()
    $importsMs = $timer.ElapsedMilliseconds
    if ($left.reused -or $right.reused -or $left.batch.rowCount -ne $rowsPerFile -or $right.batch.rowCount -ne $rowsPerFile) {
        throw 'Use a fresh measurement database. Both files must be newly imported in full.'
    }

    $timer.Restart()
    $body = @{ internalBatchId = $left.batch.id; externalBatchId = $right.batch.id } | ConvertTo-Json
    $run = if ($ConcurrentMax) {
        Invoke-Burst "$base/runs" { [Net.Http.StringContent]::new($body, [Text.Encoding]::UTF8, 'application/json') }
    } else {
        Invoke-RestMethod -Method Post -Uri "$base/runs" -Headers $headers -ContentType 'application/json' -Body $body
    }
    $timer.Stop()
    if ($run.counts.matched -ne $matched -or $run.counts.ambiguous -ne $ambiguous) {
        throw 'Unexpected reconciliation counts.'
    }
    $hostProcess.Refresh()
    [pscustomobject]@{
        inputRows = $left.batch.rowCount + $right.batch.rowCount
        rejected429 = if ($ConcurrentMax) { 9 } else { 0 }
        inputBytes = (Get-Item $internal).Length + (Get-Item $external).Length
        importsMs = $importsMs
        runMs = $timer.ElapsedMilliseconds
        appPeakWorkingSetMiB = [math]::Round($hostProcess.PeakWorkingSet64 / 1MB, 1)
        runId = $run.id
        matched = $run.counts.matched
        ambiguous = $run.counts.ambiguous
    } | ConvertTo-Json
} finally {
    if (-not $hostProcess.HasExited) { Stop-Process -Id $hostProcess.Id }
    $hostProcess.Dispose()
}
