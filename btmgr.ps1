param([switch]$Status, [int]$Click = 0, [int]$WaitMs = 2500)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -Namespace W -Name N -MemberDefinition '[DllImport("user32.dll",EntryPoint="SendMessageW")] public static extern IntPtr SendMessage(IntPtr h,uint m,IntPtr w,IntPtr l);'
$root = [System.Windows.Automation.AutomationElement]::RootElement
function Get-Win {
  foreach ($p in (Get-Process BtMgr -ErrorAction SilentlyContinue)) {
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
    $w = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $c)
    if ($w -ne $null) { return $w }
  }
  return $null
}
$walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
function Get-El($el, $want) {
  if ($el.Current.AutomationId -eq $want) { return $el }
  $c = $walker.GetFirstChild($el)
  while ($c -ne $null) { $r = Get-El $c $want; if ($r -ne $null) { return $r }; $c = $walker.GetNextSibling($c) }
  return $null
}
$win = Get-Win
if ($win -eq $null) { 'BtMgr window not found'; exit 1 }
if ($Click -gt 0) {
  $el = Get-El $win "$Click"
  if ($el -eq $null) { "control $Click not found"; exit 1 }
  [void][W.N]::SendMessage([IntPtr]$el.Current.NativeWindowHandle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero)
  Start-Sleep -Milliseconds $WaitMs
}
foreach ($id in 10005,10011,10016,10006,1012) {
  $el = Get-El $win "$id"
  if ($el -ne $null) { "{0,-6} {1}" -f $id, $el.Current.Name }
}