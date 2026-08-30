# Generates a synthetic two-speaker "meeting" WAV (16 kHz, 16-bit, mono) using Windows TTS.
#
# The content is entirely invented and the voices are synthetic, so nothing here touches real
# recordings - which is the point: benchmark material must never be production audio.
#
# Two alternating voices with varied speaking rate give the diarizer real work to do. Note the
# consequence measured in docs/Streaming_Capture_and_Live_Transcript.md section 3.3: the opening
# turns are long, so clips shorter than ~20 s contain ONE speaker and are therefore much cheaper
# than a real chunk. Read the 20 s row, not the 15 s one.
#
#   .\make-audio.ps1                 # writes base.wav beside this script
#   .\make-audio.ps1 -Out other.wav

param([string] $Out = "")

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Speech

if (-not $Out) { $Out = Join-Path $PSScriptRoot "base.wav" }

# Invented dialogue - fictional company, fictional people, fictional numbers.
$turns = @(
  @{ v = "Hazel"; r = 0;  t = "Right, shall we make a start. The agenda today is the Northwind rollout, the warehouse integration, and then whatever is left over from last fortnight." },
  @{ v = "Zira";  r = 1;  t = "Sounds good. I have got the numbers from the pilot region if you want to take those first, because they change how we think about the second phase quite a lot." },
  @{ v = "Hazel"; r = 0;  t = "Go on then, let us have them." },
  @{ v = "Zira";  r = 1;  t = "So across the four pilot depots we processed about eleven thousand consignments in six weeks. The scanning accuracy came out at ninety four percent, which is below the target of ninety eight, but the failures cluster almost entirely in one depot." },
  @{ v = "Hazel"; r = -1; t = "Which depot, and do we know why." },
  @{ v = "Zira";  r = 1;  t = "The northern one. And yes, we think so. They are running the older handheld units, and the firmware on those does not do the retry loop properly. When a barcode is damp or creased it gives up after a single attempt instead of trying three times." },
  @{ v = "Hazel"; r = 0;  t = "That is a hardware refresh then, not a software problem. How many units are we talking about and what does that cost us." },
  @{ v = "Zira";  r = 0;  t = "About sixty units. The replacement is roughly two hundred and forty pounds each, so call it fifteen thousand all in, plus a day of training per shift. It is not nothing but it is well inside the contingency we set aside in March." },
  @{ v = "Hazel"; r = 1;  t = "Fine. Let us book that in for next month rather than trying to squeeze it into this one. Put it on the risk register in the meantime so nobody is surprised when the accuracy figures look poor in the next report." },
  @{ v = "Zira";  r = 0;  t = "Will do. The second thing is the warehouse integration. We have got the connector working in the test environment, but it is slower than we expected under load." },
  @{ v = "Hazel"; r = 0;  t = "Slower in what way. Is it the network or is it the processing." },
  @{ v = "Zira";  r = 1;  t = "It is the processing, and specifically it is the reconciliation step. Every time a pallet is checked in, the connector re-reads the entire manifest rather than just the delta. On a small manifest you never notice. On a two thousand line manifest it takes about forty seconds." },
  @{ v = "Hazel"; r = -1; t = "Forty seconds is far too long. What happens to the operator while that is running." },
  @{ v = "Zira";  r = 1;  t = "They wait, which is the problem. In the pilot they started working around it by batching all the check-ins to the end of the shift, which completely defeats the point of having live stock levels in the first place." },
  @{ v = "Hazel"; r = 0;  t = "So we have built something that people are actively avoiding using. That is worth saying out loud." },
  @{ v = "Zira";  r = 0;  t = "It is, and I do not think it is hard to fix. The delta logic already exists on the ingest side, it was just never wired into the reconciliation path. I would guess a week of work and a fortnight of testing." },
  @{ v = "Hazel"; r = 1;  t = "Then let us do that before we widen the rollout at all. There is no sense putting a slow tool in front of another two hundred people. Who is picking that up." },
  @{ v = "Zira";  r = 0;  t = "I will take it, unless you want it to go to the platform team. They wrote the original ingest code so they know it better than I do." },
  @{ v = "Hazel"; r = 0;  t = "Give it to them, but stay close to it. You are the one who found it and you understand the operational side, which they do not." },
  @{ v = "Zira";  r = 1;  t = "Fair enough. Last item then, the leftovers from last time. The training materials are done and signed off. The depot manager handbook is still in draft because we are waiting on the legal review of the data retention section." },
  @{ v = "Hazel"; r = -1; t = "How long have we been waiting on that." },
  @{ v = "Zira";  r = 0;  t = "Three weeks now. I chased it last Tuesday and got an acknowledgement but no date." },
  @{ v = "Hazel"; r = 0;  t = "I will chase that myself, I know the person. Anything else before we finish." },
  @{ v = "Zira";  r = 1;  t = "Only that the second phase kick-off is provisionally the twelfth, and I would like to move it back a fortnight given what we have just said about the connector. There is no point starting phase two with a known performance problem." },
  @{ v = "Hazel"; r = 0;  t = "Agreed, move it. Send a note round explaining why, because people have already booked travel and they will want a reason rather than just a new date." },
  @{ v = "Zira";  r = 0;  t = "Understood. I will get that out this afternoon." },
  @{ v = "Hazel"; r = 0;  t = "Good. Thank you both, that was quicker than I expected. Let us pick this up again in a fortnight." },
  @{ v = "Zira";  r = 1;  t = "One more thing actually, sorry. Do we need to tell the finance team about the handheld replacement, or does that come out of the budget we already hold." },
  @{ v = "Hazel"; r = 0;  t = "It comes out of the contingency, so no approval needed, but tell them anyway as a courtesy. They dislike finding out about spend from the quarterly report." },
  @{ v = "Zira";  r = 0;  t = "Right, I will copy them in on the same note. That is everything from me." },
  @{ v = "Hazel"; r = 0;  t = "Then we are done. Thanks everyone." }
)

# 16 kHz mono matches what whisperx.load_audio resamples to, so the ladder costs the decoder nothing.
$fmt = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(16000, `
  [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, `
  [System.Speech.AudioFormat.AudioChannel]::Mono)

$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
$synth.SetOutputToWaveFile($Out, $fmt)

$voices = $synth.GetInstalledVoices()
foreach ($turn in $turns) {
  $match = $voices | Where-Object { $_.VoiceInfo.Name -like "*$($turn.v)*" } | Select-Object -First 1
  if (-not $match) { throw "TTS voice '$($turn.v)' is not installed. Installed: $(($voices | ForEach-Object { $_.VoiceInfo.Name }) -join ', ')" }
  $synth.SelectVoice($match.VoiceInfo.Name)
  $synth.Rate = $turn.r
  $synth.Speak($turn.t)
}

$synth.SetOutputToNull()
$synth.Dispose()

$len = (Get-Item $Out).Length
Write-Output "wrote $Out"
Write-Output "bytes: $len"
Write-Output "approx seconds: $([math]::Round(($len - 44) / 32000.0, 1))"
Write-Output ""
Write-Output "next: python slice.py"
