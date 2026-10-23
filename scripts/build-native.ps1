$ErrorActionPreference = 'Stop'
$workspacePath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$vswherePath = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswherePath)) { throw 'Servono Visual Studio Build Tools con gli strumenti C++.' }
$visualStudioPath = & $vswherePath -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $visualStudioPath) { throw 'Compilatore C++ non trovato.' }
$environmentScript = Join-Path $visualStudioPath 'VC\Auxiliary\Build\vcvars64.bat'
$outputPath = Join-Path $workspacePath 'bin\native'
[IO.Directory]::CreateDirectory($outputPath) | Out-Null
$buildCommands = @(
    '@echo off',
    ('call "' + $environmentScript + '" >nul'),
    'if errorlevel 1 exit /b 1',
    ('cl /nologo /std:c++17 /W4 /WX /EHsc /MT /O2 /LD /DUNICODE /D_UNICODE /Fo"' + $outputPath + '\SoundboardApo.obj" /Fe"' + $outputPath + '\SoundboardApo.dll" "' + $workspacePath + '\native\SoundboardApo.cpp" /link /MANIFEST:NO /DEF:"' + $workspacePath + '\native\SoundboardApo.def" ole32.lib propsys.lib advapi32.lib uuid.lib'),
    'if errorlevel 1 exit /b 1'
)
$buildCommands += 'exit /b 0'
$commandFile = Join-Path $outputPath 'build-native.cmd'
[IO.File]::WriteAllLines($commandFile, $buildCommands, [Text.Encoding]::Default)
& $env:ComSpec /d /c $commandFile
if ($LASTEXITCODE -ne 0) { throw 'Compilazione del componente nativo non riuscita.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install-microphone.ps1') -Destination $outputPath
