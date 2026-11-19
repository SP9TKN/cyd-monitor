# Sends 600-byte ICMP echoes to the panel at ~100 per second and records, per packet, whether and
# when the reply came back. This is the channel a UDP data plane would see (same radio, same
# lwIP stack, similar datagram size), so it answers: how often is a packet lost, how long are
# the loss bursts, and does anything reorder? Output: one CSV row per packet.
param([Parameter(Mandatory = $true)][string]$Target, [int]$Seconds = 300, [int]$IntervalMs = 10, [int]$Bytes = 600, [string]$Out)

Add-Type -TypeDefinition @"
using System; using System.Diagnostics; using System.Net.NetworkInformation; using System.Threading;
public static class Prober {
  public static long[] SentUs, RttUs, DoneOrder; public static int Count; static int doneCounter;
  public static void Run(string ip, int count, int intervalMs, int bytes, int timeoutMs) {
    Count = count; SentUs = new long[count]; RttUs = new long[count]; DoneOrder = new long[count];
    for (int i = 0; i < count; i++) { RttUs[i] = -1; DoneOrder[i] = -1; }
    var pings = new Ping[64]; for (int i = 0; i < pings.Length; i++) { pings[i] = new Ping(); pings[i].PingCompleted += OnDone; }
    var payload = new byte[bytes]; var opt = new PingOptions(64, true);
    var sw = Stopwatch.StartNew(); double tickUs = 1e6 / Stopwatch.Frequency;
    for (int i = 0; i < count; i++) {
      long due = (long)i * intervalMs * 1000;
      while (sw.ElapsedTicks * tickUs < due) { if (due - sw.ElapsedTicks * tickUs > 2000) Thread.Sleep(1); else Thread.SpinWait(50); }
      SentUs[i] = (long)(sw.ElapsedTicks * tickUs);
      try { pings[i % 64].SendAsync(ip, timeoutMs, payload, opt, i); } catch { }
    }
    Thread.Sleep(timeoutMs + 500);
  }
  static void OnDone(object s, PingCompletedEventArgs e) {
    int i = (int)e.UserState;
    if (e.Error == null && !e.Cancelled && e.Reply != null && e.Reply.Status == IPStatus.Success) {
      RttUs[i] = (long)e.Reply.RoundtripTime * 1000; DoneOrder[i] = Interlocked.Increment(ref doneCounter);
    }
  }
}
"@
$count = [int]($Seconds * 1000 / $IntervalMs)
$start = Get-Date
"start $($start.ToString('yyyy-MM-dd HH:mm:ss.fff')) target $Target count $count interval ${IntervalMs}ms bytes $Bytes" | Out-File $Out -Encoding utf8
[Prober]::Run($Target, $count, $IntervalMs, $Bytes, 400)
$sb = New-Object System.Text.StringBuilder
for ($i = 0; $i -lt $count; $i++) {
    [void]$sb.AppendLine(("{0},{1},{2},{3}" -f $i, $start.AddTicks([Prober]::SentUs[$i] * 10).ToString('HH:mm:ss.fff'), [Prober]::RttUs[$i], [Prober]::DoneOrder[$i]))
}
$sb.ToString() | Out-File $Out -Append -Encoding utf8
"end $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff')" | Out-File $Out -Append -Encoding utf8
