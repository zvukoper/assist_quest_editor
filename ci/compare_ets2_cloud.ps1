param(
    [string]$GameRoot = 'E:\Users\Docs\Euro Truck Simulator 2',
    [string]$CloudCsv = 'F:\repo\assist_quest_editor\.ci-state\ets2-snapshots\baseline-cloud.csv',
    [string]$RemoteRoot = 'E:\Steam\userdata\40536066\227300\remote'
)
$ErrorActionPreference = 'Stop'
$cloud = @{}
foreach ($r in (Import-Csv -LiteralPath $CloudCsv)) { $cloud[$r.CloudPath] = $r.Sha1 }

$rows = foreach ($rel in $cloud.Keys) {
    $local = $rel -replace '^profiles/', ''
    $sp = Join-Path (Join-Path $GameRoot 'steam_profiles') $local
    $lp = Join-Path (Join-Path $GameRoot 'profiles') $local
    $rm = Join-Path $RemoteRoot $rel
    $spsha = if (Test-Path -LiteralPath $sp) { (Get-FileHash -LiteralPath $sp -Algorithm SHA1).Hash.ToLower() } else { '' }
    $lpsha = if (Test-Path -LiteralPath $lp) { (Get-FileHash -LiteralPath $lp -Algorithm SHA1).Hash.ToLower() } else { '' }
    $rmsha = if (Test-Path -LiteralPath $rm) { (Get-FileHash -LiteralPath $rm -Algorithm SHA1).Hash.ToLower() } else { '' }
    [pscustomobject]@{
        CloudPath     = $rel
        Cloud        = $cloud[$rel]
        SteamMatch   = ($spsha -eq $cloud[$rel])
        SteamExists  = [bool]$spsha
        RemoteMatch  = ($rmsha -eq $cloud[$rel])
        RemoteExists = [bool]$rmsha
        LocalMatch   = ($lpsha -eq $cloud[$rel])
        LocalExists  = [bool]$lpsha
    }
}
$rows | Sort-Object CloudPath | Export-Csv -LiteralPath ($CloudCsv -replace '-cloud\.csv$', '-cloudmatch.csv') -NoTypeInformation -Encoding UTF8

'--- summary ---'
"cloud entries           : $($rows.Count)"
"match in steam_profiles : $(($rows | Where-Object SteamMatch).Count)"
"exists in steam_profiles: $(($rows | Where-Object SteamExists).Count)"
"match in remote (staging): $(($rows | Where-Object RemoteMatch).Count)"
"exists in remote         : $(($rows | Where-Object RemoteExists).Count)"
"match in profiles       : $(($rows | Where-Object LocalMatch).Count)"
''
'--- per profile: cloud / remote(staging) / steam_profiles ---'
$rows | Group-Object { ($_.CloudPath -split '/')[1] } | ForEach-Object {
    $name = $_.Name
    $c = $_.Count
    $rm = ($_.Group | Where-Object RemoteExists).Count
    $sp = ($_.Group | Where-Object SteamExists).Count
    "{0,-34} cloud={1,-4} remote={2,-4} steam_profiles={3}" -f $name, $c, $rm, $sp
}
