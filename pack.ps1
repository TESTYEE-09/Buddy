# Build a release DLL + Thunderstore-ready ZIP from repo root.
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$project = Join-Path $root "src\LethalAICrewmate.csproj"
$manifestPath = Join-Path $root "ThunderstorePackage\manifest.json"
$packageDir = Join-Path $root "ThunderstorePackage"
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1
if ([string]::IsNullOrWhiteSpace($dotnetCommand)) {
    $dotnetCommand = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
}
if (!(Test-Path $dotnetCommand)) { throw "Could not locate dotnet.exe" }

$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$version = [string]$manifest.version_number
if ([string]$manifest.name -ne "LethalAICrewmate") { throw "Unexpected manifest name" }
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Manifest version must be x.y.z: $version" }
if ([string]::IsNullOrWhiteSpace([string]$manifest.description)) { throw "Manifest description is empty" }
if (@($manifest.dependencies) -notcontains "BepInEx-BepInExPack-5.4.2100") { throw "Required BepInEx dependency missing" }

[xml]$projectXml = Get-Content $project -Raw
$projectVersion = [string]($projectXml.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
$pluginText = Get-Content (Join-Path $root "src\Plugin.cs") -Raw
if ($pluginText -notmatch 'ModVersion\s*=\s*"([^"]+)"') { throw "Could not read Plugin.ModVersion" }
$pluginVersion = $Matches[1]
if ($version -ne $projectVersion -or $version -ne $pluginVersion) {
    throw "Version mismatch: manifest=$version csproj=$projectVersion plugin=$pluginVersion"
}

$trackedBinaries = @(git -C $root ls-files 'LethalAICrewmate-*.zip' 'ThunderstorePackage/LethalAICrewmate.dll')
if ($trackedBinaries.Count -gt 0) { throw "Generated release binaries must not be tracked: $($trackedBinaries -join ', ')" }

# Block accidental key shipping before compilation.
$secretPattern = 'gsk_[A-Za-z0-9_-]{20,}'
$extensions = @('.cs', '.md', '.json', '.yml', '.yaml', '.ps1', '.csproj', '.txt')
Get-ChildItem $root -Recurse -File | Where-Object {
    $_.FullName -notmatch '[\\/](bin|obj|\.git)[\\/]' -and $extensions -contains $_.Extension.ToLowerInvariant()
} | ForEach-Object {
    $text = Get-Content $_.FullName -Raw -ErrorAction SilentlyContinue
    if ($text -match $secretPattern) { throw "Possible Groq API key embedded in $($_.FullName)" }
}

& $dotnetCommand restore $project
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $dotnetCommand build $project -c Release --no-restore -p:ContinuousIntegrationBuild=true
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$dll = Join-Path $root "src\bin\Release\netstandard2.1\LethalAICrewmate.dll"
if (!(Test-Path $dll)) { throw "Missing compiled DLL: $dll" }
if ((Get-Item $dll).Length -lt 1024) { throw "Compiled DLL is unexpectedly small" }

$assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName((Resolve-Path $dll)).Version
$parts = $version.Split('.')
if ($assemblyVersion.Major -ne [int]$parts[0] -or $assemblyVersion.Minor -ne [int]$parts[1] -or $assemblyVersion.Build -ne [int]$parts[2]) {
    throw "DLL assembly version $assemblyVersion does not match $version"
}

$bytes = [IO.File]::ReadAllBytes($dll)
$ascii = [Text.Encoding]::ASCII.GetString($bytes)
$utf16 = [Text.Encoding]::Unicode.GetString($bytes)
if ($ascii -match $secretPattern -or $utf16 -match $secretPattern) {
    throw "Possible Groq API key embedded in compiled DLL"
}

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
if ((Get-Item $zip).Length -gt 5MB) { throw "Release ZIP unexpectedly exceeds 5 MiB" }

$verify = Join-Path $root "_ts_verify"
if (Test-Path $verify) { Remove-Item $verify -Recurse -Force }
Expand-Archive $zip -DestinationPath $verify
$inside = Get-ChildItem $verify -File | Select-Object -ExpandProperty Name | Sort-Object
if (($expected -join '|') -ne ($inside -join '|')) { throw "ZIP validation failed: $($inside -join ', ')" }
Remove-Item $verify -Recurse -Force

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$sumPath = Join-Path $root "SHA256SUMS.txt"
"$hash  LethalAICrewmate-$version.zip" | Set-Content $sumPath -Encoding ascii
Write-Host "Built $zip"
Write-Host "SHA256 $hash"
Get-Item $zip, $dll, $sumPath | Format-Table Name, Length, LastWriteTime
