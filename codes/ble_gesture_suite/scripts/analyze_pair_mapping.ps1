[CmdletBinding()]
param(
	[string[]]$CsvPath,
	[string]$CsvPathList,

	[int]$PreFrames = 35,
	[int]$PostFrames = 65
)

if (($null -eq $CsvPath -or $CsvPath.Count -eq 0) -and ![string]::IsNullOrWhiteSpace($CsvPathList)) {
	$CsvPath = ($CsvPathList -split '[;,]') | ForEach-Object { $_.Trim() } | Where-Object { $_.Length -gt 0 }
}

if ($null -eq $CsvPath -or $CsvPath.Count -eq 0) {
	Write-Error "Provide at least one CSV via -CsvPath or -CsvPathList."
	exit 1
}

function Get-Mean($arr, $prop) {
	if ($arr.Count -eq 0) { return $null }
	return ($arr | Measure-Object -Property $prop -Average).Average
}

function Get-DominantAxis($x, $y, $z) {
	$m = @{
		X = [math]::Abs($x)
		Y = [math]::Abs($y)
		Z = [math]::Abs($z)
	}
	return ($m.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 1).Key
}

function Get-SecondAbs($x, $y, $z) {
	$vals = @([math]::Abs($x), [math]::Abs($y), [math]::Abs($z)) | Sort-Object -Descending
	if ($vals.Count -lt 2) { return 0.0 }
	return [double]$vals[1]
}

function Get-AxisValue($row, $axis) {
	switch ($axis) {
		'X' { return [double]$row.dx }
		'Y' { return [double]$row.dy }
		'Z' { return [double]$row.dz }
		default { return 0.0 }
	}
}

function Analyze-File($path, $preFrames, $postFrames) {
	if (!(Test-Path $path)) {
		Write-Warning "CSV not found: $path"
		return $null
	}

	$rows = Import-Csv $path
	$imuRows = $rows | Where-Object { $_.event -eq 'IMU' } | ForEach-Object {
		[pscustomobject]@{
			seq = [int]$_.seq
			ax = [double]$_.ax
			ay = [double]$_.ay
			az = [double]$_.az
		}
	}

	$markRows = $rows | Where-Object { $_.event -eq 'MARK' } | ForEach-Object {
		[pscustomobject]@{
			seq = [int]$_.seq
			note = ($_.note -as [string]).Trim().ToUpperInvariant()
		}
	}

	$allMarks = @('L', 'R', 'U', 'D', 'F', 'B')
	$single = @()

	foreach ($m in $markRows | Where-Object { $allMarks -contains $_.note }) {
		$pre = $imuRows | Where-Object { $_.seq -ge ($m.seq - $preFrames) -and $_.seq -lt $m.seq }
		$post = $imuRows | Where-Object { $_.seq -gt $m.seq -and $_.seq -le ($m.seq + $postFrames) }

		if ($pre.Count -eq 0 -or $post.Count -eq 0) {
			continue
		}

		$dx = (Get-Mean $post 'ax') - (Get-Mean $pre 'ax')
		$dy = (Get-Mean $post 'ay') - (Get-Mean $pre 'ay')
		$dz = (Get-Mean $post 'az') - (Get-Mean $pre 'az')
		$dom = Get-DominantAxis $dx $dy $dz
		$domAbs = switch ($dom) {
			'X' { [math]::Abs($dx) }
			'Y' { [math]::Abs($dy) }
			'Z' { [math]::Abs($dz) }
		}
		$second = Get-SecondAbs $dx $dy $dz
		$ratio = if ($second -lt 1e-6) { 999.0 } else { $domAbs / $second }

		$single += [pscustomobject]@{
			file = $path
			mark = $m.note
			seq = $m.seq
			dx = [math]::Round($dx, 1)
			dy = [math]::Round($dy, 1)
			dz = [math]::Round($dz, 1)
			dominantAxis = $dom
			dominantAbs = [math]::Round($domAbs, 1)
			dominantRatio = [math]::Round($ratio, 2)
		}
	}

	$pairDefs = @(
		@{ Name = 'L-R'; A = 'L'; B = 'R' },
		@{ Name = 'U-D'; A = 'U'; B = 'D' },
		@{ Name = 'F-B'; A = 'F'; B = 'B' }
	)

	$pairSummary = @()
	foreach ($p in $pairDefs) {
		$a = $single | Where-Object { $_.mark -eq $p.A } | Select-Object -First 1
		$b = $single | Where-Object { $_.mark -eq $p.B } | Select-Object -First 1
		if ($null -eq $a -or $null -eq $b) {
			$pairSummary += [pscustomobject]@{
				file = $path
				pair = $p.Name
				aMark = $p.A
				bMark = $p.B
				status = 'missing-mark'
				dominantAxis = ''
				contrastAbs = 0.0
				consistency = 0.0
				axisDirection = ''
				recommendation = 'collect this pair'
			}
			continue
		}

		$cx = [double]$a.dx - [double]$b.dx
		$cy = [double]$a.dy - [double]$b.dy
		$cz = [double]$a.dz - [double]$b.dz

		$sx = [double]$a.dx + [double]$b.dx
		$sy = [double]$a.dy + [double]$b.dy
		$sz = [double]$a.dz + [double]$b.dz

		$dom = Get-DominantAxis $cx $cy $cz
		$domContrast = switch ($dom) {
			'X' { [math]::Abs($cx) }
			'Y' { [math]::Abs($cy) }
			'Z' { [math]::Abs($cz) }
		}
		$domCancel = switch ($dom) {
			'X' { [math]::Abs($sx) }
			'Y' { [math]::Abs($sy) }
			'Z' { [math]::Abs($sz) }
		}
		$consistency = if ($domContrast -lt 1e-6) { 0.0 } else { 1.0 - [math]::Min(1.0, $domCancel / $domContrast) }

		$status = if ($consistency -ge 0.6) { 'usable' } elseif ($consistency -ge 0.35) { 'weak' } else { 'bad' }
		$recommendation = switch ($status) {
			'usable' { 'can map this pair' }
			'weak' { 're-capture this pair with cleaner rotation' }
			default { 'pair invalid; must re-capture' }
		}

		$aDom = Get-AxisValue $a $dom
		$bDom = Get-AxisValue $b $dom
		$aSign = if ($aDom -ge 0) { '+' } else { '-' }
		$bSign = if ($bDom -ge 0) { '+' } else { '-' }
		$axisDirection = "$($p.A)=$aSign$dom, $($p.B)=$bSign$dom"

		$pairSummary += [pscustomobject]@{
			file = $path
			pair = $p.Name
			aMark = $p.A
			bMark = $p.B
			status = $status
			dominantAxis = $dom
			contrastAbs = [math]::Round($domContrast, 1)
			consistency = [math]::Round($consistency, 2)
			axisDirection = $axisDirection
			recommendation = $recommendation
		}
	}

	return [pscustomobject]@{
		file = $path
		single = $single
		pairSummary = $pairSummary
	}
}

$allResults = @()
foreach ($path in $CsvPath) {
	$result = Analyze-File $path $PreFrames $PostFrames
	if ($null -eq $result) { continue }
	$allResults += $result

	Write-Output ""
	Write-Output "=== File: $path ==="
	Write-Output "--- Single Mark Delta ---"
	$result.single | Sort-Object seq | Format-Table mark, seq, dx, dy, dz, dominantAxis, dominantAbs, dominantRatio -AutoSize
	Write-Output "--- Pairwise Mapping Quality ---"
	$result.pairSummary | Format-Table pair, status, dominantAxis, contrastAbs, consistency, axisDirection, recommendation -AutoSize
}

if ($allResults.Count -eq 0) {
	Write-Error "No valid CSV files analyzed."
	exit 1
}

$allPairs = $allResults | ForEach-Object { $_.pairSummary }
$pairNames = @('L-R', 'U-D', 'F-B')
$final = @()

foreach ($pairName in $pairNames) {
	$candidates = $allPairs | Where-Object { $_.pair -eq $pairName -and $_.status -ne 'missing-mark' }
	if ($candidates.Count -eq 0) {
		$final += [pscustomobject]@{
			pair = $pairName
			status = 'missing'
			selectedFile = ''
			dominantAxis = ''
			consistency = 0.0
			axisDirection = ''
			nextAction = 'collect this pair'
		}
		continue
	}

	$best = $candidates |
		Sort-Object -Property @{ Expression = { if ($_.status -eq 'usable') { 2 } elseif ($_.status -eq 'weak') { 1 } else { 0 } }; Descending = $true }, @{ Expression = { [double]$_.consistency }; Descending = $true }, @{ Expression = { [double]$_.contrastAbs }; Descending = $true } |
		Select-Object -First 1

	$nextAction = if ($best.status -eq 'usable') { 'accepted' } elseif ($best.status -eq 'weak') { 'optional re-capture' } else { 'must re-capture' }

	$final += [pscustomobject]@{
		pair = $pairName
		status = $best.status
		selectedFile = $best.file
		dominantAxis = $best.dominantAxis
		consistency = $best.consistency
		axisDirection = $best.axisDirection
		nextAction = $nextAction
	}
}

Write-Output ""
Write-Output "=== Consolidated Pair Mapping (Best Per Pair) ==="
$final | Format-Table -AutoSize

