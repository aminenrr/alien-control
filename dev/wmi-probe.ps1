# 1.1 WMI sensor probe (READ-ONLY: only Thermal_Information reads, nothing is set). Run in an ADMIN PowerShell.
$ErrorActionPreference = 'Stop'
$ns = 'root\WMI'; $cls = 'AWCCWmiMethodFunction'
$inst = Get-CimInstance -Namespace $ns -ClassName $cls | Select-Object -First 1
if (-not $inst) { "no $cls instance"; return }
$m = (Get-CimClass -Namespace $ns -ClassName $cls).CimClassMethods['Thermal_Information']
if (-not $m) { "no Thermal_Information method"; return }
$inName  = ($m.Parameters | Where-Object { $_.Qualifiers.Name -contains 'In' }  | Select-Object -First 1).Name
$outName = ($m.Parameters | Where-Object { $_.Qualifiers.Name -contains 'Out' } | Select-Object -First 1).Name
"params: in=$inName out=$outName"
function TI([int]$op, [int]$arg) {
  $v = [uint32]($op -bor ($arg -shl 8))
  $r = Invoke-CimMethod -InputObject $inst -MethodName Thermal_Information -Arguments @{ $inName = $v }
  [uint32]$r.$outName
}
function TQ([int]$op, [int]$arg) { try { TI $op $arg } catch { $null } }
$desc = TI 2 0
"sysdesc: 0x{0:X8}" -f $desc
$fans = $desc -band 0xFF; $temps = ($desc -shr 8) -band 0xFF; $unk = ($desc -shr 16) -band 0xFF; $profs = ($desc -shr 24) -band 0xFF
"fans=$fans temps=$temps unknown=$unk profiles=$profs"
$ids = @()
for ($i = 0; $i -lt ($fans + $temps); $i++) {
  $id = TQ 3 $i
  if ($null -eq $id) { "index ${i}: failed"; continue }
  "index ${i}: raw 0x{0:X8}" -f $id
  $ids += ($id -band 0xFF)
}
foreach ($id in $ids) {
  $t = TQ 4 $id; $rpm = TQ 5 $id; $mx = TQ 9 $id
  "id 0x{0:X2}: temp={1} rpm={2} maxrpm={3}" -f $id, $t, $rpm, $mx
}
$sw = [Diagnostics.Stopwatch]::StartNew()
for ($k = 0; $k -lt 20; $k++) { foreach ($id in $ids) { $null = TQ 4 $id; $null = TQ 5 $id } }
"20 full WMI reads: {0} ms each" -f [int]($sw.ElapsedMilliseconds / 20)
$cli = Get-ChildItem -Path . , .\tools -Filter alienfan-cli.exe -ErrorAction SilentlyContinue | Select-Object -First 1
if ($cli) {
  "--- alienfan-cli temp rpm (for comparison) ---"; & $cli.FullName temp rpm
  $sw.Restart(); for ($k = 0; $k -lt 5; $k++) { $null = & $cli.FullName temp rpm }
  "5 CLI reads: {0} ms each" -f [int]($sw.ElapsedMilliseconds / 5)
} else { "(run from the AlienControl folder to also compare with alienfan-cli)" }
