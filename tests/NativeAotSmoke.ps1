[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repositoryRoot 'src\Velo\Velo.csproj'
$smokeParent = Join-Path ([IO.Path]::GetTempPath()) 'Velo.NativeAotSmoke'
$smokeRoot = Join-Path $smokeParent ([Guid]::NewGuid().ToString('N'))
$publishDirectory = Join-Path $smokeRoot 'publish'
$velo = Join-Path $publishDirectory 'velo.exe'
$veloHome = Join-Path $smokeRoot 'home'
$workspace = Join-Path $smokeRoot 'workspace'
$fakeBin = Join-Path $smokeRoot 'bin'

function Invoke-Velo {
    param([string[]] $Arguments)

    $startInfo = [Diagnostics.ProcessStartInfo]::new($velo)
    $startInfo.WorkingDirectory = $workspace
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Environment['VELO_HOME'] = $veloHome
    $startInfo.Environment['PATH'] = $fakeBin + [IO.Path]::PathSeparator + $env:PATH
    foreach ($argument in $Arguments) {
        [void] $startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw 'Failed to start the published Velo executable.'
    }

    $output = $process.StandardOutput.ReadToEndAsync()
    $errorOutput = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(30000)) {
        $process.Kill($true)
        $process.WaitForExit()
        $process.Dispose()
        throw "Published Velo command timed out: $($Arguments -join ' ')"
    }

    $result = $output.GetAwaiter().GetResult()
    $errorText = $errorOutput.GetAwaiter().GetResult()
    $exitCode = $process.ExitCode
    $process.Dispose()
    if ($exitCode -ne 0) {
        throw "Published Velo command failed: $($Arguments -join ' ')`n$errorText"
    }
    return $result
}

try {
    New-Item -ItemType Directory -Path $smokeRoot, $veloHome, $workspace, $fakeBin | Out-Null

    & dotnet publish $project -c Release -r win-x64 -o $publishDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "Native AOT publish failed with exit code $LASTEXITCODE."
    }
    if (-not (Test-Path -LiteralPath $velo -PathType Leaf)) {
        throw "Native AOT executable was not produced: $velo"
    }

    Set-Content -LiteralPath (Join-Path $fakeBin 'codex.cmd') `
        -Value '@echo off', 'more > codex-ran.txt' -Encoding ascii

    if (-not (Invoke-Velo @('--help')).Contains('velo run')) {
        throw 'Published executable did not expose the run command.'
    }

    $workId = (Invoke-Velo @('add', '--', 'native-aot-smoke')).Trim()
    if ([string]::IsNullOrWhiteSpace($workId)) {
        throw 'Published executable did not return a work ID.'
    }

    [void] (Invoke-Velo @('run'))
    $work = Get-Content -LiteralPath (Join-Path $veloHome "work\$workId.json") -Raw |
        ConvertFrom-Json
    if ($work.state -ne 'succeeded') {
        throw "Native AOT work finished in state: $($work.state)"
    }
    if (-not (Test-Path -LiteralPath (Join-Path $workspace 'codex-ran.txt'))) {
        throw 'The published executable did not invoke Codex in the workspace.'
    }

    Write-Output "Native AOT smoke test passed: $velo"
}
finally {
    if (Test-Path -LiteralPath $smokeRoot) {
        $resolvedRoot = [IO.Path]::GetFullPath($smokeRoot)
        $resolvedParent = [IO.Path]::GetFullPath($smokeParent).TrimEnd('\') + '\'
        if (-not $resolvedRoot.StartsWith($resolvedParent, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove unexpected smoke-test path: $resolvedRoot"
        }

        for ($attempt = 0; ; $attempt++) {
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
