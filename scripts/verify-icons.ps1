<#
  图标核验：每个 exe 的**文件图标**到底是不是"作者头像 + 指定底色"。

  为什么需要它：图标有三处落点（exe 文件图标 / 桌面快捷方式继承目标图标 / 右下角托盘图标），
  其中 exe 文件图标和托盘图标各自独立——少设一处就会出现"装出来图标不对"。
  这里用 [System.Drawing.Icon]::ExtractAssociatedIcon 把 exe 里编译进去的图标抓出来，
  数它的底色像素，直接给 PASS/FAIL（不靠肉眼）。

  用法：
      powershell -File scripts\verify-icons.ps1 -Exe a.exe,b.exe -Expect black
      powershell -File scripts\verify-icons.ps1 -Exe dist\*.exe -Expect red
  底色可选：black(#000000) / red(#C62828) / 任意 #RRGGBB
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string[]]$Exe,
    [Parameter(Mandatory = $true)][string]$Expect
)

Add-Type -AssemblyName System.Drawing
$expectRgb = switch ($Expect.ToLower()) {
    'black' { @(0, 0, 0) }
    'red'   { @(198, 40, 40) }
    default {
        if ($Expect -match '^#?([0-9a-fA-F]{6})$') {
            $h = $Matches[1]
            @([Convert]::ToInt32($h.Substring(0,2),16), [Convert]::ToInt32($h.Substring(2,2),16), [Convert]::ToInt32($h.Substring(4,2),16))
        } else { Write-Host "底色写法不认识：$Expect" -ForegroundColor Red; exit 2 }
    }
}

$files = @()
# 容错：命令行里写成 "-Exe a,b,c" 时会被当成**一个**字符串传进来（PowerShell 的经典坑），这里再拆一次
$patterns = @()
foreach ($e in $Exe) { $patterns += ($e -split ',' | Where-Object { $_.Trim().Length -gt 0 } | ForEach-Object { $_.Trim() }) }
foreach ($pattern in $patterns) { $files += Get-ChildItem -Path $pattern -ErrorAction SilentlyContinue }
if ($files.Count -eq 0) { Write-Host "没有匹配到 exe：$($Exe -join ', ')" -ForegroundColor Red; exit 2 }

$pass = 0; $fail = 0
foreach ($f in $files) {
    try {
        $ico = [System.Drawing.Icon]::ExtractAssociatedIcon($f.FullName)
        if ($null -eq $ico) { throw 'ExtractAssociatedIcon 返回空' }
        $bmp = $ico.ToBitmap()
        $w = $bmp.Width; $h = $bmp.Height
        $hit = 0; $tot = 0
        foreach ($y in 0, 1, ($h - 2), ($h - 1)) {
            foreach ($x in 0, 1, ($w - 2), ($w - 1)) {
                $c = $bmp.GetPixel($x, $y); $tot++
                if ([Math]::Abs($c.R - $expectRgb[0]) + [Math]::Abs($c.G - $expectRgb[1]) + [Math]::Abs($c.B - $expectRgb[2]) -le 24) { $hit++ }
            }
        }
        $c0 = $bmp.GetPixel(1, 1)
        $ok = $hit -ge [Math]::Ceiling($tot * 0.6)     # 四角至少 60% 命中期望底色
        if ($ok) { $pass++ } else { $fail++ }
        Write-Host ("  {0}  {1}  {2}x{3}  四角命中底色 {4}/{5}  角点RGB=({6},{7},{8})" -f
            $(if ($ok) { 'PASS' } else { 'FAIL' }), $f.Name, $w, $h, $hit, $tot, $c0.R, $c0.G, $c0.B) `
            -ForegroundColor $(if ($ok) { 'Green' } else { 'Red' })
        $bmp.Dispose(); $ico.Dispose()
    } catch {
        $fail++
        Write-Host ("  FAIL  " + $f.Name + "  " + $_.Exception.Message) -ForegroundColor Red
    }
}
Write-Host ''
Write-Host ("图标核验（期望底色 $Expect）：" + $pass + " 通过，" + $fail + " 失败")
exit $(if ($fail -eq 0) { 0 } else { 1 })
