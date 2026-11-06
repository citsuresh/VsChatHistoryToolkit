[CmdletBinding()]
param(
    [string]$RepositoryPath = 'C:\MyFiles\Git\VsChatHistoryToolkit',
    [string]$SharedPath = 'C:\Shared'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$project = Join-Path $RepositoryPath 'src\VsChatHistoryToolkit.Cli\VsChatHistoryToolkit.Cli.csproj'
$readme = Join-Path $RepositoryPath 'README.md'
$contract = Join-Path $RepositoryPath 'docs\CLI_CONTRACT.md'
foreach ($file in @($project, $readme, $contract)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        throw "Required source file not found: $file. Specify -RepositoryPath for your checkout."
    }
}
Get-Command dotnet -ErrorAction Stop | Out-Null

$destination = Join-Path $SharedPath 'VsChatHistoryToolkit-win-x64'
Write-Host "Publishing toolkit to $destination"
& dotnet publish $project --configuration Release --runtime win-x64 --self-contained true --output $destination -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) {
    throw "Publish failed with exit code $LASTEXITCODE. Do not share the output folder until a build succeeds."
}

Copy-Item -LiteralPath $readme -Destination $destination -Force
$docs = Join-Path $destination 'docs'
New-Item -ItemType Directory -Path $docs -Force | Out-Null
Copy-Item -LiteralPath $contract -Destination $docs -Force

foreach ($name in @('chathistory.exe', 'chathistory.dll', 'VsChatHistoryToolkit.Core.dll', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'e_sqlite3.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $destination $name) -PathType Leaf)) {
        throw "Published package is missing $name."
    }
}

$executable = Join-Path $destination 'chathistory.exe'
$helpOutput = & $executable --help
if ($LASTEXITCODE -ne 0) {
    throw "Published executable verification failed with exit code $LASTEXITCODE."
}
if (($helpOutput -join "`n") -notmatch 'search Visual Studio Copilot Chat history') {
    throw 'Published executable returned unexpected help output.'
}

Write-Host "Build and verification succeeded. Copy the entire folder to the other Windows x64 machine:"
Write-Host $destination
