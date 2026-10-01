# ビルドとテストをまとめて実行し、結果を build.log に保存する
# 使い方: このフォルダで PowerShell を開き
#   powershell -ExecutionPolicy Bypass -File .\build.ps1
$ErrorActionPreference = 'Continue'
Set-Location -Path $PSScriptRoot
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$env:DOTNET_CLI_UI_LANGUAGE = 'en'   # ログを英語で残す（文字化け防止）
$log = Join-Path $PSScriptRoot 'build.log'

function Invoke-Step([string]$title, [scriptblock]$command) {
    Write-Host "`n=== $title ===" -ForegroundColor Cyan
    $output = & $command 2>&1 | Out-String
    $code = $LASTEXITCODE
    Write-Host $output
    "`n=== $title (exit $code) ===`n$output" | Out-File $log -Append -Encoding utf8
    return $code
}

"=== $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') build & test / dotnet $(dotnet --version) ===" | Out-File $log -Encoding utf8

$buildExit = Invoke-Step 'build' { dotnet build MyDicomViewer.slnx --configuration Debug }
$testExit = -1
if ($buildExit -eq 0) {
    $testExit = Invoke-Step 'test' { dotnet test --solution MyDicomViewer.slnx --configuration Debug --no-build }
}

"=== RESULT: build=$buildExit test=$testExit ===" | Out-File $log -Append -Encoding utf8
if ($buildExit -eq 0 -and $testExit -eq 0) {
    Write-Host "`nビルドとテストに成功しました" -ForegroundColor Green
} else {
    Write-Host "`n失敗しました。上に表示されたエラーを確認してください" -ForegroundColor Red
}
