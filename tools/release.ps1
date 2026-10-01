# インストーラー（Velopack）を作る
#
# 使い方（リポジトリのルートで）:
#   powershell -ExecutionPolicy Bypass -File .\tools\release.ps1
#   powershell -ExecutionPolicy Bypass -File .\tools\release.ps1 -IncludeModel   # AI モデルも同梱する
#
# 出力: artifacts\releases\ に MyDicomViewer-win-Setup.exe（インストーラー）と更新用のファイル一式
# GitHub Releases にこれらを置くと、インストール済みのアプリが自動更新を検出できる。
param(
    [switch]$IncludeModel,
    [string]$Version
)
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

if (-not $Version) {
    $Version = ([xml](Get-Content 'MyDicomViewer\MyDicomViewer.csproj' -Raw)).Project.PropertyGroup.Version |
        Where-Object { $_ } | Select-Object -First 1
}
Write-Host "MyDicomViewer $Version のインストーラーを作成します" -ForegroundColor Cyan

$publish = 'artifacts\publish'
$releases = 'artifacts\releases'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

# 1. 実行に必要なものを1つのフォルダにまとめる（.NET ランタイム同梱。利用者の PC に .NET は不要）
dotnet publish 'MyDicomViewer\MyDicomViewer.csproj' -c Release -r win-x64 --self-contained true -o $publish
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish に失敗しました' }

# 2. 秘密情報が紛れ込んでいないか確認（キーを含む開発用設定は配布しない）
$leaked = Get-ChildItem $publish -Recurse -Filter 'appsettings.Local.json'
if ($leaked) { throw "配布物に appsettings.Local.json が含まれています。中止しました: $($leaked.FullName)" }

# 3. AI モデルは学習データの利用規約を確認したうえで、明示的に指定した場合だけ同梱する
if (-not $IncludeModel) {
    Get-ChildItem (Join-Path $publish 'Models') -Include 'pneumonia.onnx', 'model_meta.json' -Recurse -ErrorAction SilentlyContinue |
        Remove-Item -Force
    Write-Host 'AI モデルは同梱していません（同梱する場合は -IncludeModel を付けて実行）' -ForegroundColor Yellow
}

# 4. インストーラーと更新パッケージを作る
dotnet tool update -g vpk --version 1.2.161
if ($LASTEXITCODE -ne 0) { throw 'vpk のインストールに失敗しました' }
vpk pack --packId MyDicomViewer --packVersion $Version --packDir $publish --mainExe MyDicomViewer.exe --outputDir $releases
if ($LASTEXITCODE -ne 0) { throw 'vpk pack に失敗しました' }

Write-Host "`n完成しました: $((Resolve-Path $releases).Path)" -ForegroundColor Green
Write-Host 'MyDicomViewer-win-Setup.exe を実行するとインストールできます。'
