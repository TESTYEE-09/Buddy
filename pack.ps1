# Build Release + Thunderstore zip (run from repo root)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
dotnet build "$root\src\LethalAICrewmate.csproj" -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$dll = "$root\src\bin\Release\netstandard2.1\LethalAICrewmate.dll"
$pkg = "$root\ThunderstorePackage"
Copy-Item $dll "$pkg\LethalAICrewmate.dll" -Force

$ver = (Get-Content "$pkg\manifest.json" -Raw | ConvertFrom-Json).version_number
$zip = "$root\LethalAICrewmate-$ver.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }

$staging = "$root\_ts_staging"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging | Out-Null
Copy-Item "$pkg\manifest.json","$pkg\README.md","$pkg\CHANGELOG.md","$pkg\icon.png","$pkg\LethalAICrewmate.dll" $staging
Compress-Archive -Path "$staging\*" -DestinationPath $zip -Force
Remove-Item $staging -Recurse -Force
Write-Host "Built $zip"
Get-Item $zip, "$pkg\LethalAICrewmate.dll" | Format-Table Name, Length, LastWriteTime
