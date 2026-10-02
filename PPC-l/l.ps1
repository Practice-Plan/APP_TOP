$ppcPath = Join-Path $env:APPDATA 'wang.station/ppc.exe'
$ppc32Path = Join-Path $env:APPDATA 'wang.station/ppc32.exe'

if (Test-Path $ppcPath) {
	& $ppcPath
} else {
	& $ppc32Path
}