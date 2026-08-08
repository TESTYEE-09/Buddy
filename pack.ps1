# Build a release DLL + Thunderstore-ready ZIP from repo root.
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$project = Join-Path $root "src\LethalAICrewmate.csproj"
$manifestPath = Join-Path $root "ThunderstorePackage\manifest.json"
$packageDir = Join-Path $root "ThunderstorePackage"

$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$version = [string]$manifest.version_number
if ([string]::IsNullOrWhiteSpace($version)) { throw "Manifest version is empty" }

[xml]$projectXml = Get-Content $project -Raw
$projectVersion = [string]($projectXml.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
$pluginText = Get-Content (Join-Path $root "src\Plugin.cs") -Raw
if ($pluginText -notmatch 'ModVersion\s*=\s*"([^"]+)"') { throw "Could not read Plugin.ModVersion" }
$pluginVersion = $Matches[1]
if ($version -ne $projectVersion -or $version -ne $pluginVersion) {
    throw "Version mismatch: manifest=$version csproj=$projectVersion plugin=$pluginVersion"
}

# Block accidental key shipping before compilation.
$secretPattern = 'gsk_[A-Za-z0-9_-]{20,}'
$extensions = @('.cs', '.md', '.json', '.yml', '.yaml', '.ps1', '.csproj', '.txt')
Get-ChildItem $root -Recurse -File | Where-Object {
    $_.FullName -notmatch '[\\/](bin|obj|\.git)[\\/]' -and $extensions -contains $_.Extension.ToLowerInvariant()
} | ForEach-Object {
    $text = Get-Content $_.FullName -Raw -ErrorAction SilentlyContinue
    if ($text -match $secretPattern) { throw "Possible Groq API key embedded in $($_.FullName)" }
}

dotnet restore $project
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet build $project -c Release --no-restore -p:ContinuousIntegrationBuild=true
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$dll = Join-Path $root "src\bin\Release\netstandard2.1\LethalAICrewmate.dll"
if (!(Test-Path $dll)) { throw "Missing compiled DLL: $dll" }
$ascii = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($dll))
if ($ascii -match $secretPattern) { throw "Possible Groq API key embedded in compiled DLL" }

$staging = Join-Path $root "_ts_staging"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging | Out-Null

Copy-Item $dll (Join-Path $staging "LethalAICrewmate.dll")
Copy-Item (Join-Path $packageDir "manifest.json") (Join-Path $staging "manifest.json")
Copy-Item (Join-Path $packageDir "README.md") (Join-Path $staging "README.md")
Copy-Item (Join-Path $packageDir "CHANGELOG.md") (Join-Path $staging "CHANGELOG.md")
Copy-Item (Join-Path $packageDir "icon.png") (Join-Path $staging "icon.png")

$expected = @("LethalAICrewmate.dll", "manifest.json", "README.md", "CHANGELOG.md", "icon.png") | Sort-Object
$actual = Get-ChildItem $staging -File | Select-Object -ExpandProperty Name | Sort-Object
if (($expected -join '|') -ne ($actual -join '|')) { throw "Unexpected package contents: $($actual -join ', ')" }

$zip = Join-Path $root "LethalAICrewmate-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $staging "*") -DestinationPath $zip -CompressionLevel Optimal
Remove-Item $staging -Recurse -Force

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "Built $zip"
Write-Host "SHA256 $hash"
Get-Item $zip, $dll | Format-Table Name, Length, LastWriteTime
