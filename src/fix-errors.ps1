# Fix ScrollableMemoryMenu.cs
$path = "H:\ValleyTalk-master\src\ScrollableMemoryMenu.cs"
$lines = [System.IO.File]::ReadAllLines($path, [System.Text.Encoding]::UTF8)

# Fix 1: Lines 242-245 (0-indexed: 241-244) - move assignment inside lambda body
$lines[241] = "                        // 返回记忆主菜单"
$lines[242] = "                        Game1.activeClickableMenu = this;"
$lines[243] = "                    });"
$lines[244] = "        }"

# Fix 2: Line 274 (0-indexed: 273) - unterminated string literal
$lines[273] = '                string hint = "暂无记忆。点击下方按钮添加。";'

# Fix 3: Lines 407-408 (0-indexed: 406-407) - swapped order
$lines[406] = "            _returnMenu.RefreshEntries();"
$lines[407] = "            Game1.activeClickableMenu = _returnMenu;"

[System.IO.File]::WriteAllLines($path, $lines, [System.Text.Encoding]::UTF8)
Write-Output "ScrollableMemoryMenu.cs fixed."

# Fix Character.cs
$path2 = "H:\ValleyTalk-master\src\Character.cs"
$lines2 = [System.IO.File]::ReadAllLines($path2, [System.Text.Encoding]::UTF8)

# Fix: Replace lines 254-256 (0-indexed: 253-255) with single line using \n\n
$lines2[253] = '                prompts.System = memoryCtx + "\n\n" + prompts.System;'
$lines2[254] = ""
$lines2[255] = ""

[System.IO.File]::WriteAllLines($path2, $lines2, [System.Text.Encoding]::UTF8)
Write-Output "Character.cs fixed."
