# 用 Windows SAPI 合成三段测试音频（中 / 英 / 日 + 混说），16 kHz / 单声道 / 16-bit WAV。
#
# 为什么需要它：阶段 C/D 的基准语料不能用系统音效（那不是语音），也不该等真会议录音。
# SAPI 合成的是"朗读腔"，比真会议简单，但足以验证**接入链路、载荷形状、解析器、速度**。
# 真实会议录音的 WER 必须另测（朗读集和远场多人不是一回事，见 asr-runtime §11 R4）。
#
# 用法：
#   pwsh ./make-test-audio.ps1                                  # 生成到 ./samples
#   pwsh ./make-test-audio.ps1 -OutDir D:\asr-corpus            # 指定目录
#   pwsh ./make-test-audio.ps1 -ListVoices                       # 看本机有哪些发音人

param(
    [string]$OutDir = (Join-Path $PSScriptRoot 'samples'),
    [switch]$ListVoices
)

$ErrorActionPreference = 'Stop'

$voice = New-Object -ComObject SAPI.SpVoice

if ($ListVoices) {
    $voice.GetVoices() | ForEach-Object { $_.GetDescription() }
    return
}

# SAFT16kHz16BitMono = 18（SpeechAudioFormatType 枚举）
$SAFT_16KHZ_16BIT_MONO = 18

$cases = @(
    @{ Name = 'zh-60s';  Lang = 'zh'; Voice = 'Chinese (Simplified)'; Text = '今天我们先过一下部署方案。现在的问题是全量切换的回滚时间没有人算过。我建议先灰度两周，观察错误率和延迟，跑稳了再切全量。回滚的八分钟这个数字要写进验收标准。另外，王五负责补一份回滚演练记录，下周三之前给。' }
    @{ Name = 'en-60s';  Lang = 'en'; Voice = 'English (United States)'; Text = 'Let us review the deployment plan for this quarter. The main concern is that nobody has measured the rollback time for a full cutover. I suggest we run canary at twenty percent traffic for two weeks, watch the error rate and latency, and only then switch to full traffic. The eight minute rollback figure should go into the acceptance criteria. Wang will write up the rollback drill record before next Wednesday.' }
    @{ Name = 'ja-60s';  Lang = 'ja'; Voice = 'Japanese'; Text = 'では、次にリリースの話をします。今回の懸念は、フルカットオーバーのロールバック時間を誰も計測していないことです。まずカナリアで二週間、エラー率とレイテンシーを見てから全体に切り替えることを提案します。八分というロールバック時間は受け入れ基準に書き込みます。ロールバック演習の記録は王さんが来週の水曜日までに用意します。' }
    @{ Name = 'mixed-60s'; Lang = 'mix'; Voice = 'Chinese (Simplified)'; Text = '这个 deploy 我建议先做 canary，二十 percent 的流量跑两周。ロールバックの時間は、えーと、八分钟くらいです。デプロイは来週の火曜日でいいですか。それから acceptance criteria に rollback time を書き込みます。' }
)

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

foreach ($c in $cases) {
    $target = $voice.GetVoices() | Where-Object { $_.GetDescription() -like "*$($c.Voice)*" } | Select-Object -First 1
    if (-not $target) {
        Write-Warning "找不到发音人「$($c.Voice)」，跳过 $($c.Name)。用 -ListVoices 看本机有哪些。"
        continue
    }

    $voice.Voice = $target

    $path = Join-Path $OutDir "$($c.Name).wav"
    $stream = New-Object -ComObject SAPI.SpFileStream
    $fmt = New-Object -ComObject SAPI.SpAudioFormat
    $fmt.Type = $SAFT_16KHZ_16BIT_MONO
    $stream.Format = $fmt
    $stream.Open($path, 3)   # 3 = SSFMCreateForWrite

    $voice.AudioOutputStream = $stream
    $voice.Speak($c.Text, 0)
    $stream.Close()

    # 释放 COM，否则下一次 Open 会报"文件被占用"
    [System.Runtime.InteropServices.Marshal]::ReleaseComObject($stream) | Out-Null
    [System.Runtime.InteropServices.Marshal]::ReleaseComObject($fmt) | Out-Null

    $size = (Get-Item $path).Length
    # 16 kHz / 单声道 / 16-bit → 每秒 32000 字节
    $secs = [math]::Round($size / 32000, 1)
    "{0,-12} {1,-4} {2,8:N0} B  {3,6} s  {4}" -f $c.Name, $c.Lang, $size, $secs, $path
}

[System.Runtime.InteropServices.Marshal]::ReleaseComObject($voice) | Out-Null
