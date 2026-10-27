param([switch]$Test, [string]$OutputDirectory = 'dist')
$ErrorActionPreference = 'Stop'
$frameworkPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath (Join-Path $frameworkPath 'csc.exe'))) {
    $frameworkPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$compilerPath = Join-Path $frameworkPath 'csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) {
    throw 'Can cai .NET Framework 4.8 tren Windows de build ung dung.'
}
$outputPath = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) { [System.IO.Path]::GetFullPath($OutputDirectory) } else { [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $OutputDirectory)) }
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$references = @('System.dll', 'System.Core.dll', 'System.Xaml.dll') | ForEach-Object { '/reference:' + (Join-Path $frameworkPath $_) }
$references += @('WindowsBase.dll', 'PresentationCore.dll', 'PresentationFramework.dll') | ForEach-Object { '/reference:' + (Join-Path $frameworkPath "WPF\$_") }
$arguments = @('/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/warn:4', '/utf8output', '/codepage:65001',
    ('/out:' + (Join-Path $outputPath 'DonDepOCung.exe')),
    ('/win32manifest:' + (Join-Path $PSScriptRoot 'app.manifest')),
    ('/resource:' + (Join-Path $PSScriptRoot 'src\MainWindow.xaml') + ',CleanSystem.MainWindow.xaml'))
& $compilerPath @arguments @references (Join-Path $PSScriptRoot 'src\Cleaner.cs') (Join-Path $PSScriptRoot 'src\Commands.cs') (Join-Path $PSScriptRoot 'src\RecycleBin.cs') (Join-Path $PSScriptRoot 'src\ShellRecycleBin.cs') (Join-Path $PSScriptRoot 'src\WindowsUpdate.cs') (Join-Path $PSScriptRoot 'src\TempElevation.cs') (Join-Path $PSScriptRoot 'src\Program.cs')
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Host "Built: $outputPath\DonDepOCung.exe"
if ($Test) {
    & $compilerPath /nologo /target:exe /warn:4 /utf8output /codepage:65001 "/out:$outputPath\CleanSystem.Tests.exe" /reference:System.dll /reference:System.Core.dll ('/reference:' + (Join-Path $frameworkPath 'WPF\WindowsBase.dll')) (Join-Path $PSScriptRoot 'src\Cleaner.cs') (Join-Path $PSScriptRoot 'src\Commands.cs') (Join-Path $PSScriptRoot 'src\RecycleBin.cs') (Join-Path $PSScriptRoot 'src\ShellRecycleBin.cs') (Join-Path $PSScriptRoot 'src\WindowsUpdate.cs') (Join-Path $PSScriptRoot 'tests\CleanerTests.cs') (Join-Path $PSScriptRoot 'tests\CommandTests.cs') (Join-Path $PSScriptRoot 'tests\RecycleBinTests.cs') (Join-Path $PSScriptRoot 'tests\WindowsUpdateTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
    & (Join-Path $outputPath 'CleanSystem.Tests.exe') (Join-Path $PSScriptRoot 'test-output')
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    $smokeProcess = Start-Process -FilePath (Join-Path $outputPath 'DonDepOCung.exe') -ArgumentList '--smoke-test' -WindowStyle Hidden -PassThru -Wait
    if ($smokeProcess.ExitCode -ne 0) { throw "UI smoke test failed. See $outputPath\smoke-test.txt." }
    Write-Host "UI smoke test passed; previews: $outputPath\preview.png and preview-developer.png"
}
