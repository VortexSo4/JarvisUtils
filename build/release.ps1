<#
Сборка релиза JarvisUtils: publish → упаковка Velopack (установщик + пакеты обновления).

  .\build\release.ps1 -Version 1.0.1                  # только собрать в .\Releases
  .\build\release.ps1 -Version 1.0.1 -Upload -Token X  # собрать и опубликовать релиз на GitHub

Результат в .\Releases:
  JarvisUtils-win-Setup.exe   — установщик для пользователя (ставится без прав администратора,
                                при необходимости сам доставит .NET 8 Desktop Runtime)
  *.nupkg, releases.win.json  — то, что скачивает автообновление

Обычно релиз собирает GitHub Actions (.github/workflows/release.yml) по пушу тега v1.0.1 —
этот скрипт для локальной проверки или ручной публикации.
#>
param(
    [Parameter(Mandatory)][string]$Version,
    [switch]$Upload,
    [string]$Token = $env:GITHUB_TOKEN
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$repo = 'https://github.com/VortexSo4/JarvisUtils'
$publish = Join-Path $root 'publish'
$releases = Join-Path $root 'Releases'

Push-Location $root
try {
    dotnet tool restore
    if ($LASTEXITCODE) { throw 'dotnet tool restore failed' }

    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
    dotnet publish JarvisUtils/JarvisUtils.csproj -c Release -r win-x64 --self-contained false `
        -p:Version=$Version -o $publish
    if ($LASTEXITCODE) { throw 'dotnet publish failed' }

    # прошлые релизы — для дельта-обновлений (качается меньше); первый релиз — без них
    if ($Upload -or $Token) {
        dotnet vpk download github --repoUrl $repo --token $Token -o $releases
    }

    dotnet vpk pack --packId JarvisUtils --packVersion $Version --packDir $publish `
        --mainExe JarvisUtils.exe --packTitle JarvisUtils --packAuthors VortexSo4 `
        --icon JarvisUtils/App/JarvisUtils.ico --framework net8.0-x64-desktop -o $releases
    if ($LASTEXITCODE) { throw 'vpk pack failed' }

    if ($Upload) {
        if (-not $Token) { throw 'Нужен токен GitHub: -Token или переменная GITHUB_TOKEN' }
        dotnet vpk upload github --repoUrl $repo --token $Token -o $releases `
            --publish --releaseName "JarvisUtils $Version" --tag "v$Version"
        if ($LASTEXITCODE) { throw 'vpk upload failed' }
    }

    Write-Host "Готово: $releases" -ForegroundColor Cyan
}
finally {
    Pop-Location
}
