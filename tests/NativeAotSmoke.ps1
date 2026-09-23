[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPath = Join-Path $repositoryRoot 'src\Velo\velo.csproj'
$fakeCodexProject = Join-Path $repositoryRoot 'tests\Velo.FakeCodex\Velo.FakeCodex.csproj'
$publishDirectory = Join-Path $repositoryRoot 'src\Velo\bin\Release\net10.0\win-x64\publish'
$veloExecutable = Join-Path $publishDirectory 'velo.exe'
$fakeCodexOutput = Join-Path $repositoryRoot 'tests\Velo.FakeCodex\bin\Release\net10.0'
$smokeRootParent = Join-Path ([IO.Path]::GetTempPath()) 'Velo.NativeAotSmoke'
$smokeRoot = Join-Path $smokeRootParent ([Guid]::NewGuid().ToString('N'))
$veloHome = Join-Path $smokeRoot 'home'
$fakeBin = Join-Path $smokeRoot 'fake-bin'
$workerStarted = $false

function Invoke-DotNet {
    param([string[]] $Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') exited with $LASTEXITCODE."
    }
}

function Invoke-Velo {
    param(
        [string[]] $Arguments,
        [bool] $RedirectOutput = $true
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $veloExecutable
    $startInfo.WorkingDirectory = $repositoryRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $RedirectOutput
    $startInfo.RedirectStandardError = $RedirectOutput
    if ($RedirectOutput) {
        $startInfo.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
        $startInfo.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    }
    $startInfo.Environment['VELO_HOME'] = $veloHome
    $startInfo.Environment['PATH'] = $fakeBin + [IO.Path]::PathSeparator + $env:PATH
    foreach ($argument in $Arguments) {
        [void] $startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        throw "Failed to start published Velo executable."
    }

    $standardOutput = if ($RedirectOutput) {
        $process.StandardOutput.ReadToEndAsync()
    } else {
        $null
    }
    $standardError = if ($RedirectOutput) {
        $process.StandardError.ReadToEndAsync()
    } else {
        $null
    }
    if (-not $process.WaitForExit(30000)) {
        $process.Kill($true)
        $process.WaitForExit()
        throw "Published Velo command timed out: $($Arguments -join ' ')"
    }

    $result = [pscustomobject]@{
        ExitCode = $process.ExitCode
        StandardOutput = if ($RedirectOutput) {
            $standardOutput.GetAwaiter().GetResult()
        } else {
            [string]::Empty
        }
        StandardError = if ($RedirectOutput) {
            $standardError.GetAwaiter().GetResult()
        } else {
            [string]::Empty
        }
    }
    $process.Dispose()

    if ($result.ExitCode -ne 0) {
        throw "Published Velo command failed: $($Arguments -join ' ')`nstdout: $($result.StandardOutput)`nstderr: $($result.StandardError)"
    }
    return $result
}

function Wait-ForPath {
    param(
        [string] $Path,
        [string] $Description
    )

    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $Path) {
            return
        }
        Start-Sleep -Milliseconds 100
    }
    throw "Timed out waiting for $Description at $Path."
}

try {
    Invoke-DotNet -Arguments @('publish', $projectPath, '-c', 'Release', '-r', 'win-x64')
    Invoke-DotNet -Arguments @('build', $fakeCodexProject, '-c', 'Release')

    if (-not (Test-Path -LiteralPath $veloExecutable -PathType Leaf)) {
        throw "Native AOT executable was not produced: $veloExecutable"
    }

    New-Item -ItemType Directory -Path $veloHome, $fakeBin | Out-Null
    Copy-Item -Path (Join-Path $fakeCodexOutput '*') -Destination $fakeBin
    Copy-Item -LiteralPath (Join-Path $fakeCodexOutput 'Velo.FakeCodex.exe') `
        -Destination (Join-Path $fakeBin 'codex.exe')

    $help = Invoke-Velo -Arguments @('--help')
    if (-not $help.StandardOutput.Contains('Usage: velo <command> [options]')) {
        throw 'Published executable did not print the expected help output.'
    }

    $add = Invoke-Velo -Arguments @('add', 'b13-native-aot')
    $taskId = $add.StandardOutput.Trim()
    if ([string]::IsNullOrWhiteSpace($taskId)) {
        throw 'Published executable did not return a task ID.'
    }

    [void] (Invoke-Velo -Arguments @('start', '--timeout', '00:01:00') -RedirectOutput $false)
    $workerStarted = $true
    Wait-ForPath -Path (Join-Path $veloHome "tasks\done\$taskId.json") `
        -Description 'the Native AOT task to complete'
    Wait-ForPath -Path (Join-Path $veloHome 'b13-native-aot.completed') `
        -Description 'the fake Codex completion marker'

    $show = Invoke-Velo -Arguments @('show', $taskId)
    if (-not $show.StandardOutput.Contains('State: done')) {
        throw 'Published executable did not report the completed task as done.'
    }
    if (-not $show.StandardOutput.Contains('Prompt: b13-native-aot')) {
        throw 'Published executable did not preserve the task prompt.'
    }

    [void] (Invoke-Velo -Arguments @('stop'))
    $workerStarted = $false
    Write-Output "Native AOT smoke test passed: $veloExecutable"
}
finally {
    if ($workerStarted -or (Test-Path -LiteralPath (Join-Path $veloHome 'velo.pid'))) {
        try { [void] (Invoke-Velo -Arguments @('stop')) } catch { }
    }

    if (Test-Path -LiteralPath $smokeRoot) {
        $resolvedRoot = [IO.Path]::GetFullPath($smokeRoot)
        $resolvedParent = [IO.Path]::GetFullPath($smokeRootParent).TrimEnd('\') + '\'
        if (-not $resolvedRoot.StartsWith($resolvedParent, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove unexpected smoke-test path: $resolvedRoot"
        }

        for ($attempt = 0; $attempt -lt 10; $attempt++) {
            try {
                [IO.Directory]::Delete($resolvedRoot, $true)
                break
            }
            catch {
                if ($attempt -ge 9) {
                    throw
                }
                Start-Sleep -Milliseconds 100
            }
        }
    }
}