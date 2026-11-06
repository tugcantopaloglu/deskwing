param([string]$CompanionPath,[switch]$DryRun)
$ErrorActionPreference='Stop'
if(!$CompanionPath){
    $CompanionPath=Join-Path $PSScriptRoot 'DeskWing-Companion.exe'
    if(!(Test-Path -LiteralPath $CompanionPath)){$CompanionPath=Join-Path $PSScriptRoot 'TabletDeck-Companion.exe'}
}
$taskExecutable=(Resolve-Path -LiteralPath $CompanionPath).Path
if([IO.Path]::GetFileName($taskExecutable) -notin @('DeskWing-Companion.exe','TabletDeck-Companion.exe')){throw 'Select the DeskWing companion executable.'}
$taskBlocks=@(Get-NetFirewallApplicationFilter -Program $taskExecutable -ErrorAction SilentlyContinue | Get-NetFirewallRule | Where-Object {$_.Enabled -eq 'True' -and $_.Direction -eq 'Inbound' -and $_.Action -eq 'Block'})
Write-Output ('Companion: '+$taskExecutable)
Write-Output ('Matching inbound block rules to disable: '+$taskBlocks.Count)
Write-Output 'Allow TCP 8743 and 8744 from LocalSubnet only, on Private/Public profiles. No internet port forwarding is configured.'
if($DryRun){Write-Output 'Dry run: firewall unchanged.';return}
$taskIdentity=[Security.Principal.WindowsIdentity]::GetCurrent()
if(!([Security.Principal.WindowsPrincipal]::new($taskIdentity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Windows requires administrator rights. Run this script from an Administrator PowerShell window.'}
$taskHash=[Security.Cryptography.SHA256]::Create()
try{$taskSuffix=([BitConverter]::ToString($taskHash.ComputeHash([Text.Encoding]::UTF8.GetBytes($taskExecutable.ToUpperInvariant())))).Replace('-','').Substring(0,16)}finally{$taskHash.Dispose()}
$taskRule='DeskWing-Local-'+$taskSuffix
$taskExisting=Get-NetFirewallRule -Name $taskRule -ErrorAction SilentlyContinue
if(!$taskExisting){
    # An existing rule keeps its identity and access scope; only its displayed label changes.
    $taskExisting=Get-NetFirewallRule -Name ('TabletDeck-Local-'+$taskSuffix) -ErrorAction SilentlyContinue
}
if($taskExisting){
    $taskExisting | Set-NetFirewallRule -DisplayName 'DeskWing local tablet control' | Out-Null
}else{
    New-NetFirewallRule -Name $taskRule -DisplayName 'DeskWing local tablet control' -Direction Inbound -Action Allow -Program $taskExecutable -Protocol TCP -LocalPort 8743,8744 -RemoteAddress LocalSubnet -Profile Private,Public -EdgeTraversalPolicy Block | Out-Null
}
foreach($taskBlock in $taskBlocks){$taskBlock | Disable-NetFirewallRule | Out-Null}
Write-Output 'Local tablet Wi-Fi access configured. Keep the companion running and use its physical LAN address.'
