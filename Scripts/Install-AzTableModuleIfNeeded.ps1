$ErrorActionPreference = "Stop"
<#
.SYNOPSIS
Installs a module if it is needed.

.DESCRIPTION
Installs a module if it is needed.

.EXAMPLE
Install-AzTableModuleIfNeeded

#>
function Install-AzTableModuleIfNeeded {
    [CmdletBinding()]
    param(
    )
        $scriptsDirectory = Split-Path $PSScriptRoot -Parent

        . ($scriptsDirectory + '\Scripts\Install-ModuleIfNeeded.ps1')
        Install-ModuleIfNeeded -Name AzTable -Version "2.0.1" -Verbose
}