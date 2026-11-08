param(
    [string]$BaseUrl = 'http://127.0.0.1:5084',
    [string]$OperatorKey = $env:Operators__ivan,
    [string]$ReviewerKey = $env:Reviewers__olga,
    [string]$OutputPath = (Join-Path $PSScriptRoot '../.local/demo-report.csv')
)

$ErrorActionPreference = 'Stop'
if (-not $ReviewerKey) { throw 'Set Reviewers__olga or pass -ReviewerKey.' }
if (-not $OperatorKey) { throw 'Set Operators__ivan or pass -OperatorKey.' }
$operatorHeaders = @{ 'X-Api-Key' = $OperatorKey }
$reviewerHeaders = @{ 'X-Api-Key' = $ReviewerKey }

function Import-CsvBatch($Source, $Name) {
    Invoke-RestMethod -Method Post -Uri "$BaseUrl/imports/$Source" -Headers $operatorHeaders -ContentType 'text/csv' `
        -InFile (Join-Path $PSScriptRoot "../demo/$Name.csv")
}

function Start-Reconciliation($Internal, $External) {
    $body = @{ internalBatchId = $Internal; externalBatchId = $External } | ConvertTo-Json
    Invoke-RestMethod -Method Post -Uri "$BaseUrl/runs" -Headers $operatorHeaders -ContentType 'application/json' -Body $body
}

$external = Import-CsvBatch external external
$invalid = Import-CsvBatch internal invalid
$errors = Invoke-RestMethod "$BaseUrl/imports/$($invalid.batch.id)/errors" -Headers $operatorHeaders
$errors.items | Format-Table line, raw, error

$blocked = Invoke-WebRequest -Method Post -Uri "$BaseUrl/runs" -Headers $operatorHeaders -SkipHttpErrorCheck `
    -ContentType 'application/json' -Body (@{
        internalBatchId = $invalid.batch.id
        externalBatchId = $external.batch.id
    } | ConvertTo-Json)
if ($errors.items.Count -ne 2 -or $blocked.StatusCode -ne 422) { throw 'Invalid import was not reported or blocked.' }

$internal = Import-CsvBatch internal internal
$duplicate = Import-CsvBatch internal internal
if (-not $duplicate.reused -or $internal.batch.id -ne $duplicate.batch.id) { throw 'Import was not reused.' }

$run = Start-Reconciliation $internal.batch.id $external.batch.id
$repeat = Start-Reconciliation $internal.batch.id $external.batch.id
$expected = @{ matched = 2; 'amount-mismatch' = 1; 'missing-internal' = 1; 'missing-external' = 1; duplicate = 3; ambiguous = 3 }
foreach ($category in $expected.Keys) {
    if ($run.counts.$category -ne $expected[$category]) { throw "Unexpected count for $category." }
}
if ($run.id -eq $repeat.id) { throw 'Repeat run did not create a new version.' }

$firstPage = Invoke-RestMethod "$BaseUrl/runs/$($run.id)/results?limit=3" -Headers $reviewerHeaders
$secondPage = Invoke-RestMethod "$BaseUrl/runs/$($run.id)/results?after=$($firstPage.nextAfter)" -Headers $reviewerHeaders
$all = $firstPage.items + $secondPage.items
$again = (Invoke-RestMethod "$BaseUrl/runs/$($repeat.id)/results" -Headers $reviewerHeaders).items
if (($all | ConvertTo-Json -Depth 8 -Compress) -cne ($again | ConvertTo-Json -Depth 8 -Compress)) {
    throw 'Run results differ.'
}
$all | Format-Table position, category, method, candidateCount

$target = $all | Where-Object { $_.category -eq 'ambiguous' -and $_.internal }
$url = "$BaseUrl/runs/$($run.id)/results/$($target.position)/resolution"
$body = @{ comment = '=Провайдер подтвердил повтор выгрузки. Строку оставили без пары.' } | ConvertTo-Json
$unauthorized = Invoke-WebRequest -Method Post -Uri $url -ContentType 'application/json' -Body $body -SkipHttpErrorCheck
if ($unauthorized.StatusCode -ne 401) { throw 'Resolution accepted without reviewer key.' }
$decision = Invoke-RestMethod -Method Post -Uri $url -Headers $reviewerHeaders -ContentType 'application/json' -Body $body
$decision | Format-List
$conflict = Invoke-WebRequest -Method Post -Uri $url -Headers $reviewerHeaders -ContentType 'application/json' `
    -Body $body -SkipHttpErrorCheck
if ($conflict.StatusCode -ne 409) { throw 'Repeated resolution did not return 409.' }

$fullPath = [IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($fullPath)) | Out-Null
Invoke-WebRequest "$BaseUrl/runs/$($run.id)/export" -Headers $reviewerHeaders -OutFile $fullPath
$report = Import-Csv -LiteralPath $fullPath
$audit = $report | Where-Object { $_.position -eq $target.position }
if ($report.Count -ne 11 -or $audit.category -ne 'ambiguous' -or $audit.reviewer -ne $decision.author -or
    -not $audit.resolved_at -or $audit.comment -ne ("'" + $decision.comment) -or $audit.internal_order -ne "'=order-6") {
    throw 'CSV is missing the original discrepancy, reviewer or safe user text.'
}

"Run $($run.id), repeat $($repeat.id). Report: $fullPath"
