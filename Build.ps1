param(
    [string]$PackageDirectory = $PSScriptRoot,
    [string]$OutputPath = (Join-Path $PSScriptRoot 'build\Install.exe')
)
$ErrorActionPreference = 'Stop'
$taskPackagePath = (Resolve-Path -LiteralPath $PackageDirectory).Path
$taskManifestPath = Join-Path $taskPackagePath 'package.json'
if (-not (Test-Path -LiteralPath $taskManifestPath -PathType Leaf)) {
    throw 'package.json was not found in PackageDirectory.'
}
$taskSourcePath = Join-Path $PSScriptRoot 'src\Installer.cs'
$taskManifestHash = (Get-FileHash -LiteralPath $taskManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
$taskSource = Get-Content -LiteralPath $taskSourcePath -Raw -Encoding UTF8
if (-not $taskSource.Contains('__MANIFEST_SHA256__')) {
    throw 'The source template does not contain the manifest hash placeholder.'
}
$taskSource = $taskSource.Replace('__MANIFEST_SHA256__', $taskManifestHash)
$taskOutputPath = [IO.Path]::GetFullPath($OutputPath)
$taskBuildPath = Join-Path $PSScriptRoot 'build'
New-Item -ItemType Directory -Path $taskBuildPath,([IO.Path]::GetDirectoryName($taskOutputPath)) -Force | Out-Null
$taskGeneratedPath = Join-Path $taskBuildPath 'Installer.generated.cs'
[IO.File]::WriteAllText($taskGeneratedPath, $taskSource, [Text.UTF8Encoding]::new($false))
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $taskCompiler)) {
    $taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $taskCompiler)) {
    throw 'The .NET Framework C# compiler csc.exe was not found.'
}
$taskArguments = @(
    '/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/utf8output',
    '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll',
    '/reference:System.Web.Extensions.dll',
    ('/win32manifest:' + (Join-Path $PSScriptRoot 'src\installer.manifest')),
    ('/out:' + $taskOutputPath), $taskGeneratedPath
)
& $taskCompiler @taskArguments
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
Write-Output ('Built: ' + $taskOutputPath)
Write-Output ('Embedded package.json SHA-256: ' + $taskManifestHash)
