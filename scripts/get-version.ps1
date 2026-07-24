<#
.SYNOPSIS
  Prints the plugin version read from Version.props.
#>
$ErrorActionPreference = 'Stop'

$propsPath = Join-Path $PSScriptRoot '..\Version.props'
[xml]$xml = Get-Content -LiteralPath $propsPath
$node = $xml.SelectSingleNode('//Version')
if ($null -eq $node -or [string]::IsNullOrWhiteSpace($node.InnerText)) {
    throw "No <Version> element found in Version.props."
}
Write-Output $node.InnerText.Trim()
