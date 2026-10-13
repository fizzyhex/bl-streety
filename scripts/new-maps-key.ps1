<#
.SYNOPSIS
    makes a google maps key for streety with the gcloud cli.

.DESCRIPTION
    sets up a "bl-streety" google cloud project (or reuses yours), turns on the map tiles + geocoding apis, and
    creates an api key that can only call those two. run it again later to swap the key out or add another.

    needs the google cloud cli: https://cloud.google.com/sdk/docs/install (or: winget install Google.CloudSDK)

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\new-maps-key.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\new-maps-key.ps1 -KeyName "streety laptop"
#>
param(
    # what the key's called in the cloud console; asked for when left out
    [string]$KeyName
)

$ErrorActionPreference = 'Stop'

$ProjectName = 'bl-streety'
$Services = @('tile.googleapis.com', 'geocoding-backend.googleapis.com')
$DefaultKeyName = "streety $($env:COMPUTERNAME.ToLowerInvariant())"

# arguments go in as a plain array: gcloud is a .cmd on windows, and anything fancier gets mangled on the way
function Invoke-Gcloud([string[]]$Arguments, [switch]$Interactive) {
    if ($Interactive) {
        # left alone so prompts and sign-in links reach the user
        $output = & gcloud @Arguments
        $chatter = @()
    }
    else {
        # gcloud narrates on stderr (progress, whole operation results, keys included), so that's held back and
        # only shown when a call fails. windows powershell turns redirected stderr into errors, so 'Stop' is
        # relaxed for this function only
        $ErrorActionPreference = 'Continue'
        $all = & gcloud @Arguments 2>&1
        $output = $all | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] }
        $chatter = $all | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] }
    }

    if ($LASTEXITCODE -ne 0) {
        $chatter | ForEach-Object { Write-Host "$_" -ForegroundColor DarkGray }
        throw "gcloud $($Arguments[0..1] -join ' ') failed (exit $LASTEXITCODE)"
    }

    return $output
}

function Invoke-GcloudJson([string[]]$Arguments) {
    $text = (Invoke-Gcloud ($Arguments + '--format=json')) -join "`n"

    if (-not $text.Trim()) {
        return @()
    }

    # windows powershell hands a json array over as one object; returning the variable unrolls it, so callers can
    # wrap the result in @() whatever the count
    $parsed = $text | ConvertFrom-Json
    return $parsed
}

function Read-Choice([string]$Prompt, [string[]]$Allowed, [string]$Default) {
    while ($true) {
        $answer = (Read-Host $Prompt).Trim().ToLowerInvariant()

        if ($answer -eq '' -and $Default) { return $Default }
        if ($Allowed -contains $answer) { return $answer }

        Write-Host "pick one of: $($Allowed -join ', ')" -ForegroundColor Yellow
    }
}

function Select-FromList([string]$Prompt, [object[]]$Items, [scriptblock]$Label) {
    for ($i = 0; $i -lt $Items.Count; $i++) {
        Write-Host "  [$($i + 1)] $(& $Label $Items[$i])"
    }

    $numbers = 1..$Items.Count | ForEach-Object { "$_" }
    $picked = Read-Choice -Prompt $Prompt -Allowed $numbers -Default '1'
    return $Items[[int]$picked - 1]
}

# --- cli + sign in ----------------------------------------------------------------------------------------------

if (-not (Get-Command gcloud -ErrorAction SilentlyContinue)) {
    Write-Host "the google cloud cli isn't installed." -ForegroundColor Red
    Write-Host "get it from https://cloud.google.com/sdk/docs/install (or: winget install Google.CloudSDK), then run this again."
    exit 1
}

$signedIn = @(Invoke-GcloudJson @('auth', 'list') | Where-Object { $_.status -eq 'ACTIVE' })

if ($signedIn.Count -eq 0) {
    Write-Host "sign in to google in the browser window that opens..."
    Invoke-Gcloud @('auth', 'login') -Interactive | Out-Null
    $signedIn = @(Invoke-GcloudJson @('auth', 'list') | Where-Object { $_.status -eq 'ACTIVE' })
}

Write-Host "signed in as $($signedIn[0].account)"

# --- project ----------------------------------------------------------------------------------------------------

$project = @(Invoke-GcloudJson @('projects', 'list') |
    Where-Object { $_.name -eq $ProjectName -and $_.lifecycleState -eq 'ACTIVE' })[0]

$existing = $null -ne $project

if ($existing) {
    $projectId = $project.projectId
    Write-Host "found your $ProjectName project ($projectId)"
}
else {
    # project ids are global, so the name stays bl-streety and the id gets a random tail
    $suffix = -join ((1..6) | ForEach-Object { 'abcdefghijklmnopqrstuvwxyz0123456789'[(Get-Random -Maximum 36)] })
    $projectId = "$ProjectName-$suffix"

    Write-Host "creating the $ProjectName project ($projectId)..."
    Invoke-Gcloud @('projects', 'create', $projectId, "--name=$ProjectName", '--quiet') | Out-Null
}

# --- billing: the map tiles api won't answer without it ---------------------------------------------------------

$billing = Invoke-GcloudJson @('billing', 'projects', 'describe', $projectId)

if (-not $billing.billingEnabled) {
    $accounts = @(Invoke-GcloudJson @('billing', 'accounts', 'list') | Where-Object { $_.open })

    if ($accounts.Count -eq 0) {
        Write-Host "google needs a billing account before the map tiles api will work." -ForegroundColor Yellow
        Write-Host "add one at https://console.cloud.google.com/billing, then run this again."
        exit 1
    }

    Write-Host "the map tiles api needs billing turned on for $projectId."

    $billingAccount = if ($accounts.Count -eq 1) { $accounts[0] } else {
        Select-FromList -Prompt "which billing account? [1]" -Items $accounts -Label { param($a) "$($a.displayName) ($($a.name))" }
    }

    if ((Read-Choice -Prompt "link $($billingAccount.displayName) to $projectId? [y/n]" -Allowed @('y', 'n')) -ne 'y') {
        Write-Host "left billing alone; the key won't load tiles until it's on."
        exit 1
    }

    $billingId = $billingAccount.name -replace '^billingAccounts/', ''
    Invoke-Gcloud @('billing', 'projects', 'link', $projectId, "--billing-account=$billingId") | Out-Null
}

# --- apis -------------------------------------------------------------------------------------------------------

Write-Host "turning on the map tiles + geocoding apis..."
Invoke-Gcloud (@('services', 'enable') + $Services + @('apikeys.googleapis.com', "--project=$projectId", '--quiet')) | Out-Null

# --- replace or add ---------------------------------------------------------------------------------------------

$oldKey = $null

if ($existing) {
    $keys = @(Invoke-GcloudJson @('services', 'api-keys', 'list', "--project=$projectId"))

    if ($keys.Count -gt 0) {
        Write-Host "this project already has $($keys.Count) key(s)."
        $mode = Read-Choice -Prompt "[r]eplace one (deletes the old key) or register a [n]ew one? [r/n]" -Allowed @('r', 'n')

        if ($mode -eq 'r') {
            $oldKey = if ($keys.Count -eq 1) { $keys[0] } else {
                Select-FromList -Prompt "which one should go? [1]" -Items $keys -Label { param($k) $k.displayName }
            }

            # replacing keeps the old name unless one was asked for
            if (-not $KeyName) { $KeyName = $oldKey.displayName }
        }
    }
}

if (-not $KeyName) {
    $KeyName = (Read-Host "name your key [$DefaultKeyName]").Trim()
    if (-not $KeyName) { $KeyName = $DefaultKeyName }
}

# --- the key ----------------------------------------------------------------------------------------------------

Write-Host "creating key `"$KeyName`"..."

$targets = $Services | ForEach-Object { "--api-target=service=$_" }
$newKey = (Invoke-GcloudJson (@('services', 'api-keys', 'create', "--project=$projectId", "--display-name=$KeyName") + $targets + '--quiet')).response

if (-not $newKey.keyString) {
    throw "made the key but couldn't read it back; check https://console.cloud.google.com/apis/credentials?project=$projectId"
}

$paste = "mapskey:$($newKey.keyString)"

try { Set-Clipboard -Value $paste; $copied = $true } catch { $copied = $false }

Write-Host ""
if ($copied) {
    Write-Host "token created and copied! press ctrl+v in the bonelab window to register it:" -ForegroundColor Green
}
else {
    Write-Host "token created! paste this into the bonelab window to register it:" -ForegroundColor Green
}
Write-Host $paste
Write-Host ""
Write-Host "alternatively, set maps_key in UserData/MelonPreferences.cfg."

# only once the new key's safely out does the old one go. google refuses to delete a key used in the last week
# unless told not to check, and replacing was asked for, so it's told
if ($oldKey -and $oldKey.name -ne $newKey.name) {
    Write-Host ""
    Write-Host "deleting the old key `"$($oldKey.displayName)`"..."

    try {
        Invoke-Gcloud @('services', 'api-keys', 'delete', $oldKey.name, '--no-check-existing-usage', '--quiet') | Out-Null
    }
    catch {
        Write-Host "couldn't delete it; remove it at https://console.cloud.google.com/apis/credentials?project=$projectId" -ForegroundColor Yellow
    }
}
