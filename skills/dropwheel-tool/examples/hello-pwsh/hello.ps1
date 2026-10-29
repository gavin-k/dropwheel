param(
    [Parameter(Mandatory)] [string] $Source,
    [Parameter(Mandatory)] [string] $Output
)
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$ErrorActionPreference = 'Stop'

Write-Output 'progress 0.1'
Write-Output ("detail " + [IO.Path]::GetFileName($Source))
Copy-Item -LiteralPath $Source -Destination $Output
Write-Output 'progress 1'
