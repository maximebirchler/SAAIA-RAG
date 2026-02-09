# Tests PowerShell pour M2.1 : RequestId + Error Handling
# Usage: .\tests\M2.1_tests.ps1

$BaseUrl = "http://localhost:5000"
$TestApiKey = "test-key-123"  # À récupérer depuis la DB / bootstrap

Write-Host "======================================" -ForegroundColor Cyan
Write-Host "M2.1 Tests — RequestId + Error Handling" -ForegroundColor Cyan
Write-Host "======================================" -ForegroundColor Cyan
Write-Host ""

# Test 1: /health sans X-Request-Id doit retourner X-Request-Id
Write-Host "Test 1: GET /health (sans X-Request-Id)" -ForegroundColor Yellow
try {
    $resp = Invoke-WebRequest -Uri "$BaseUrl/health" -Method Get -Headers @{} -ErrorAction Continue
    $requestId = $resp.Headers["X-Request-Id"]
    Write-Host "✓ Response header X-Request-Id: $requestId" -ForegroundColor Green
    Write-Host "  Body: $($resp.Content)" -ForegroundColor Gray
} catch {
    Write-Host "✗ Failed: $_" -ForegroundColor Red
}
Write-Host ""

# Test 2: /health avec X-Request-Id fourni par le client
Write-Host "Test 2: GET /health (avec X-Request-Id client)" -ForegroundColor Yellow
$customRequestId = "client-request-" + [guid]::NewGuid().ToString()
try {
    $resp = Invoke-WebRequest -Uri "$BaseUrl/health" -Method Get -Headers @{ "X-Request-Id" = $customRequestId } -ErrorAction Continue
    $returnedRequestId = $resp.Headers["X-Request-Id"]
    if ($returnedRequestId -eq $customRequestId) {
        Write-Host "✓ Server returned the same X-Request-Id: $returnedRequestId" -ForegroundColor Green
    } else {
        Write-Host "✗ RequestId mismatch: expected $customRequestId, got $returnedRequestId" -ForegroundColor Red
    }
} catch {
    Write-Host "✗ Failed: $_" -ForegroundColor Red
}
Write-Host ""

# Test 3: /ready sans auth doit retourner X-Request-Id et requestId dans body
Write-Host "Test 3: GET /ready (sans auth)" -ForegroundColor Yellow
try {
    $resp = Invoke-WebRequest -Uri "$BaseUrl/ready" -Method Get -ErrorAction Continue
    $requestId = $resp.Headers["X-Request-Id"]
    $body = $resp.Content | ConvertFrom-Json
    
    Write-Host "✓ Response header X-Request-Id: $requestId" -ForegroundColor Green
    Write-Host "  Body requestId: $($body.requestId)" -ForegroundColor Gray
    
    if ($body.requestId -eq $requestId) {
        Write-Host "✓ Header and body requestId match" -ForegroundColor Green
    } else {
        Write-Host "✗ RequestId mismatch between header and body" -ForegroundColor Red
    }
} catch {
    Write-Host "✗ Failed: $_" -ForegroundColor Red
}
Write-Host ""

# Test 4: Missing API key → 401 avec { error, requestId }
Write-Host "Test 4: POST /rag/search sans X-Api-Key → 401" -ForegroundColor Yellow
try {
    $body = @{
        query = "test"
        topK = 5
    } | ConvertTo-Json
    
    $resp = Invoke-WebRequest -Uri "$BaseUrl/rag/search" -Method Post -Body $body -ContentType "application/json" -Headers @{} -ErrorAction Continue
    Write-Host "✗ Should have failed with 401" -ForegroundColor Red
} catch {
    if ($_.Exception.Response.StatusCode -eq 401) {
        $requestId = $_.Exception.Response.Headers["X-Request-Id"]
        $body = $_.Exception.Response.Content | ConvertFrom-Json
        
        Write-Host "✓ Got 401 Unauthorized" -ForegroundColor Green
        Write-Host "  Header X-Request-Id: $requestId" -ForegroundColor Gray
        Write-Host "  Body: error=$($body.error), requestId=$($body.requestId)" -ForegroundColor Gray
        
        if ($requestId -and $body.requestId) {
            Write-Host "✓ Both header and body have requestId" -ForegroundColor Green
        } else {
            Write-Host "✗ Missing requestId in header or body" -ForegroundColor Red
        }
    } else {
        Write-Host "✗ Wrong status code: $($_.Exception.Response.StatusCode)" -ForegroundColor Red
    }
}
Write-Host ""

# Test 5: Invalid API key → 401 avec { error, requestId }
Write-Host "Test 5: POST /rag/search avec X-Api-Key invalide → 401" -ForegroundColor Yellow
try {
    $body = @{
        query = "test"
        topK = 5
    } | ConvertTo-Json
    
    $resp = Invoke-WebRequest -Uri "$BaseUrl/rag/search" -Method Post -Body $body -ContentType "application/json" -Headers @{ "X-Api-Key" = "invalid-key-xyz" } -ErrorAction Continue
    Write-Host "✗ Should have failed with 401" -ForegroundColor Red
} catch {
    if ($_.Exception.Response.StatusCode -eq 401) {
        $body = $_.Exception.Response.Content | ConvertFrom-Json
        Write-Host "✓ Got 401 Unauthorized" -ForegroundColor Green
        Write-Host "  Body: error=$($body.error), requestId=$($body.requestId)" -ForegroundColor Gray
    } else {
        Write-Host "✗ Wrong status code: $($_.Exception.Response.StatusCode)" -ForegroundColor Red
    }
}
Write-Host ""

# Test 6: Bad request (missing query) → 400 avec { error, requestId }
Write-Host "Test 6: POST /rag/search avec query vide → 400" -ForegroundColor Yellow
if (-not [string]::IsNullOrWhiteSpace($TestApiKey)) {
    try {
        $body = @{
            query = ""
            topK = 5
        } | ConvertTo-Json
        
        $resp = Invoke-WebRequest -Uri "$BaseUrl/rag/search" -Method Post -Body $body -ContentType "application/json" -Headers @{ "X-Api-Key" = $TestApiKey } -ErrorAction Continue
        Write-Host "✗ Should have failed with 400" -ForegroundColor Red
    } catch {
        if ($_.Exception.Response.StatusCode -eq 400) {
            $body = $_.Exception.Response.Content | ConvertFrom-Json
            Write-Host "✓ Got 400 Bad Request" -ForegroundColor Green
            Write-Host "  Body: error=$($body.error), requestId=$($body.requestId)" -ForegroundColor Gray
        } else {
            Write-Host "✗ Wrong status code: $($_.Exception.Response.StatusCode)" -ForegroundColor Red
        }
    }
} else {
    Write-Host "⊘ Skipped (no test API key configured)" -ForegroundColor Yellow
}
Write-Host ""

Write-Host "======================================" -ForegroundColor Cyan
Write-Host "M2.1 Tests Complete" -ForegroundColor Cyan
Write-Host "======================================" -ForegroundColor Cyan
