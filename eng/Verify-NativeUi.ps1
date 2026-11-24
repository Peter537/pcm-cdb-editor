#Requires -Version 7.0

[CmdletBinding()]
param(
    [string] $EditorPath = (Join-Path $PSScriptRoot '../src/PcmCdbEditor.App/bin/Release/net10.0-windows10.0.19041.0/win-x64/PcmCdbEditor.exe'),
    [string] $PythonCommand = 'python'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (!$IsWindows -or ![Environment]::UserInteractive -or ![Environment]::Is64BitProcess) {
    throw 'Native UI verification requires 64-bit PowerShell on an interactive Windows desktop.'
}
$editor = Get-Item -LiteralPath $EditorPath
if ($editor.PSIsContainer -or $editor.Name -ne 'PcmCdbEditor.exe') {
    throw 'Select a built PcmCdbEditor.exe.'
}
$python = Get-Command $PythonCommand -CommandType Application | Select-Object -First 1
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing.Common

# TableView 1.4.1 has no UI Automation action for tapping a cell or column header.
# Keep native input restricted to the editor process created by this runner.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Threading;
public static class UiAcceptanceInput
{
  [StructLayout(LayoutKind.Sequential)] public struct Point { public int X; public int Y; }
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(Point point);
  [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr window, uint flags);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [StructLayout(LayoutKind.Sequential)] public struct Mouse { public int X,Y; public uint Data,Flags,Time; public UIntPtr Extra; }
  [StructLayout(LayoutKind.Sequential)] public struct Keyboard { public ushort Key,Scan; public uint Flags,Time; public UIntPtr Extra; }
  [StructLayout(LayoutKind.Explicit)] public struct Input {
    [FieldOffset(0)] public uint Type;
    [FieldOffset(8)] public Mouse Mouse;
    [FieldOffset(8)] public Keyboard Key;
  }
  [DllImport("user32.dll", SetLastError=true)] public static extern uint SendInput(uint count, Input[] input, int size);
  static void Send(params Input[] input) {
    var n = SendInput((uint)input.Length, input, Marshal.SizeOf(typeof(Input)));
    if (n != input.Length) throw new InvalidOperationException("SendInput accepted " + n + " of " + input.Length + " events (Windows error " + Marshal.GetLastWin32Error() + ").");
  }
  static Input MouseEvent(uint flags, int data=0) { return new Input { Type=0, Mouse=new Mouse{Flags=flags,Data=unchecked((uint)data)} }; }
  static Input KeyEvent(ushort key,uint flags) { return new Input { Type=1, Key=new Keyboard{Key=key,Flags=flags} }; }
  public static void Focus(IntPtr hwnd)
  {
    if (GetForegroundWindow() != hwnd)
    {
      ShowWindow(hwnd, 9);
      SetForegroundWindow(hwnd);
      for (int attempt=0; attempt<20 && GetForegroundWindow()!=hwnd; attempt++)
        Thread.Sleep(30);
    }
    if (GetForegroundWindow() != hwnd)
      throw new InvalidOperationException("The owned editor is not the foreground window. Foreground handle " + GetForegroundWindow() + "; target " + hwnd);
  }
  public static void PointAt(IntPtr hwnd, int x, int y)
  {
    Focus(hwnd);
    var target = WindowFromPoint(new Point { X = x, Y = y });
    if (target != hwnd && GetAncestor(target, 2) != hwnd)
      throw new InvalidOperationException("The measured point does not belong to the owned editor.");
    if (!SetCursorPos(x,y)) throw new InvalidOperationException("The cursor could not be positioned.");
  }
  public static void Click(IntPtr hwnd, int x, int y)
  {
    PointAt(hwnd,x,y);
    Send(MouseEvent(2),MouseEvent(4));
  }
  public static void Key(IntPtr hwnd, byte key)
  {
    Focus(hwnd);
    Send(KeyEvent(key,0), KeyEvent(key,2));
  }
}
'@

function Wait-Observation {
    param([string] $Description, [scriptblock] $Observe)

    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        if ($process.HasExited) {
            throw "The editor exited during $Description (code $($process.ExitCode))."
        }
        try {
            $result = & $Observe
            if ($result) { return $result }
        }
        catch [Windows.Automation.ElementNotAvailableException] {
            # Retry with the current automation tree after a view changes.
        }
        Start-Sleep -Milliseconds 100
    } while ($timer.Elapsed.TotalSeconds -lt 12)

    throw "Timed out waiting for $Description."
}

function Find-Element {
    param(
        [Windows.Automation.AutomationElement] $Root,
        [Windows.Automation.AutomationProperty] $Property,
        [object] $Value
    )

    $condition = [Windows.Automation.PropertyCondition]::new($Property, $Value)
    $Root.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
}

function Wait-Element {
    param([string] $Value, [switch] $ByName, [switch] $Enabled)

    $property = [Windows.Automation.AutomationElement]::AutomationIdProperty
    if ($ByName) { $property = [Windows.Automation.AutomationElement]::NameProperty }
    Wait-Observation $Value {
        $element = Find-Element $window $property $Value
        if ($null -ne $element -and !$element.Current.IsOffscreen -and (!$Enabled -or $element.Current.IsEnabled)) {
            $element
        }
    }
}

function Invoke-Element {
    param([Windows.Automation.AutomationElement] $Element)

    $pattern = $Element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}

function Select-Element {
    param([Windows.Automation.AutomationElement] $Element)

    $pattern = $Element.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern)
    $pattern.Select()
}

function Select-CatalogTable {
    param([string] $Name)

    $catalog = Wait-Element 'TableCatalogList'
    $table = Wait-Observation "the $Name catalog entry" {
        $label = Find-Element $catalog ([Windows.Automation.AutomationElement]::NameProperty) $Name
        while ($null -ne $label -and !$label.Equals($catalog)) {
            $pattern = $null
            if ($label.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
                return $label
            }
            $label = [Windows.Automation.TreeWalker]::RawViewWalker.GetParent($label)
        }
    }
    Select-Element $table
}

function Click-Element {
    param([Windows.Automation.AutomationElement] $Element)

    $bounds = $Element.Current.BoundingRectangle
    if ($bounds.IsEmpty -or $bounds.Width -le 1 -or $bounds.Height -le 1) {
        throw 'The target has no rendered rectangle.'
    }
    if ([UiAcceptanceInput]::GetForegroundWindow() -ne $process.MainWindowHandle) {
        try { $window.SetFocus() }
        catch [System.Management.Automation.MethodInvocationException] { }
    }
    try {
        [UiAcceptanceInput]::Click($process.MainWindowHandle,
            [int]($bounds.X + $bounds.Width / 2), [int]($bounds.Y + $bounds.Height / 2))
    }
    catch { throw "Could not click '$($Element.Current.Name)' at $bounds. $($_.Exception.Message)" }
}

function Find-GridValue {
    param([string] $Name)

    $grid = Find-Element $window ([Windows.Automation.AutomationElement]::AutomationIdProperty) 'CurrentTableGrid'
    if ($grid) {
        $element = Find-Element $grid ([Windows.Automation.AutomationElement]::NameProperty) $Name
        if ($element -and !$element.Current.IsOffscreen) { $element }
    }
}

function Wait-GridValue {
    param([string] $Name)
    Wait-Observation "grid value $Name" { Find-GridValue $Name }
}

function Switch-TableTab {
    param([string] $Name, [string] $GridValue)

    $tabs = Wait-Element 'OpenTableTabs'
    $tab = Wait-Observation "open tab $Name" {
        $item = Find-Element $tabs ([Windows.Automation.AutomationElement]::NameProperty) $Name
        if ($item -and !$item.Current.IsOffscreen -and $item.Current.IsEnabled) { $item }
    }
    Click-Element $tab
    $null = Wait-GridValue $GridValue
    $null = Wait-Element 'RefreshTableCommand' -Enabled
}

function New-RecoveryFixture {
    param([string] $Name, [int] $RowCount)

    $sessionId = [guid]::NewGuid()
    $directory = Join-Path $dataRoot "Sessions/$($sessionId.ToString('N'))"
    $null = New-Item -ItemType Directory -Path $directory
    $source = Join-Path $run "$Name.cdb"
    $cdb = Join-Path $directory 'working.cdb'
    $sqlite = Join-Path $directory 'working.sqlite'
    [IO.File]::WriteAllText($source, 'Synthetic native UI test marker')
    [IO.File]::Copy($source, $cdb)
    @'
import contextlib
import sqlite3
import sys

with contextlib.closing(sqlite3.connect(sys.argv[1])) as db, db:
    db.execute("CREATE TABLE sample (id INTEGER PRIMARY KEY, note TEXT NOT NULL, qty INTEGER NOT NULL DEFAULT 1)")
    db.execute("CREATE TABLE trap_control(fail INTEGER NOT NULL)")
    db.execute("INSERT INTO trap_control VALUES(0)")
    db.execute("""
        CREATE VIEW zz_refresh_trap AS
        SELECT CASE WHEN fail=1 THEN json_extract('invalid-fixture-json', '$') ELSE 1 END AS id
        FROM trap_control
        """)
    db.execute("""
        CREATE VIEW zz_slow_count AS
        WITH RECURSIVE counter(n) AS (
            VALUES(1) UNION ALL SELECT n + 1 FROM counter WHERE n < 50000000
        )
        SELECT n AS id FROM counter
        """)
    db.executemany("INSERT INTO sample(note) VALUES (?)", ((f"row-{number:03}",) for number in range(int(sys.argv[2]))))
    db.execute("UPDATE sample SET qty = 10000 + id")
    columns = [f"c{number:02}" for number in range(1, 25)]
    db.execute("CREATE TABLE zz_wide (id INTEGER PRIMARY KEY, " + ", ".join(f"{col} TEXT" for col in columns) + ")")
    db.executemany("INSERT INTO zz_wide VALUES (" + ", ".join("?" for _ in range(25)) + ")",
                   ((row, *(f"{col}-row{row}" for col in columns)) for row in range(1, 7)))
'@ | & $python.Source - $sqlite $RowCount
    if ($LASTEXITCODE -ne 0) { throw 'Could not generate the SQLite fixture.' }
    $timestamp = [DateTimeOffset]::UtcNow.ToString('O')
    @{
        SessionId = $sessionId
        SourceCdbPath = $source
        SaveTargetCdbPath = $source
        SessionDirectory = $directory
        WorkingCdbPath = $cdb
        WorkingSqlitePath = $sqlite
        IsDirty = $true
        Lifecycle = 8 # EditorSessionLifecycle.Recoverable
        SaveDestinationBaseline = @{
            Path = $source
            Length = (Get-Item -LiteralPath $source).Length
            Sha256 = (Get-FileHash -LiteralPath $source).Hash
        }
        CreatedAtUtc = $timestamp
        UpdatedAtUtc = $timestamp
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $directory 'session.json') -Encoding utf8

    [pscustomobject]@{ Source = $source; Directory = $directory; Sqlite = $sqlite }
}

function Select-RecoveryFixture {
    param([string] $Source)

    $list = Wait-Element 'RecoverableSessionList'
    $item = Wait-Observation 'the requested synthetic session' {
        $condition = [Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::ControlTypeProperty,
            [Windows.Automation.ControlType]::ListItem)
        foreach ($candidate in $list.FindAll([Windows.Automation.TreeScope]::Descendants, $condition)) {
            if ($candidate.Current.Name.StartsWith($Source, [StringComparison]::Ordinal)) {
                return $candidate
            }
        }
    }
    Select-Element $item
}

function Read-Observation {
    param([string] $Sqlite)

    $result = @'
import contextlib
import pathlib
import sqlite3
import sys

uri = pathlib.Path(sys.argv[1]).as_uri() + "?mode=ro"
with contextlib.closing(sqlite3.connect(uri, uri=True, timeout=1)) as db:
    row = db.execute("SELECT count(*), sum(note = ? AND typeof(note) = ?) FROM sample", ("written-by-native-ui", "text")).fetchone()
    print(f"{row[0]}:{row[1]}")
'@ | & $python.Source - $Sqlite
    if ($LASTEXITCODE -ne 0) { throw 'Could not read the working SQLite observation.' }
    $result
}

function Set-RefreshTrap {
    param([string] $Sqlite, [bool] $Enabled)

    @'
import contextlib
import sqlite3
import sys

with contextlib.closing(sqlite3.connect(sys.argv[1], timeout=3)) as db, db:
    db.execute("UPDATE trap_control SET fail=?", (int(sys.argv[2]),))
'@ | & $python.Source - $Sqlite ([int] $Enabled)
    if ($LASTEXITCODE -ne 0) { throw 'Could not update the synthetic refresh fixture.' }
}

function Read-GridNote {
    param([string] $Sqlite, [int] $Id)

    $value = @'
import contextlib
import pathlib
import sqlite3
import sys

with contextlib.closing(sqlite3.connect(pathlib.Path(sys.argv[1]).as_uri() + "?mode=ro", uri=True, timeout=3)) as db:
    print(db.execute("SELECT note FROM sample WHERE id=?", (int(sys.argv[2]),)).fetchone()[0])
'@ | & $python.Source - $Sqlite $Id
    if ($LASTEXITCODE -ne 0) { throw 'Could not read the inline edit fixture.' }
    $value
}

function Start-InlineEdit {
    param([string] $CellValue, [switch] $RequireExactCell)

    $condition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::ControlTypeProperty,
        [Windows.Automation.ControlType]::Edit)
    $cell = Wait-GridValue $CellValue
    Click-Element $cell
    Start-Sleep -Milliseconds 300
    [UiAcceptanceInput]::Key($process.MainWindowHandle, 113) # F2
    $findEditor = {
        $grid = Wait-Element 'GridControl'
        $textbox = $grid.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
        if ($textbox -and !$textbox.Current.IsOffscreen) { $textbox }
    }
    Start-Sleep -Milliseconds 350
    $openedEditor = & $findEditor
    if ($openedEditor -and $RequireExactCell -and
        $openedEditor.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value -ne $CellValue) {
        # TableView's deferred focus restore can still target the previous cell after a tab switch.
        [UiAcceptanceInput]::Key($process.MainWindowHandle, 27) # Escape without editing
        $null = Wait-Observation 'the previous inline editor to close' { -not (& $findEditor) }
        $openedEditor = $null
    }
    if (!$openedEditor) {
        Click-Element (Wait-GridValue $CellValue)
        Start-Sleep -Milliseconds 85
        Click-Element (Wait-GridValue $CellValue)
    }
    $textbox = Wait-Observation "the inline editor for $CellValue" $findEditor
    $value = $textbox.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)
    if ($RequireExactCell -and $value.Current.Value -ne $CellValue) {
        throw 'The inline editor opened on the wrong fixture cell.'
    }
    $value
}

function Test-InlineTabSwitch {
    param([pscustomobject] $Fixture)

    Select-CatalogTable 'zz_wide'
    $null = Wait-GridValue 'c02-row1'
    Switch-TableTab 'sample' 'row-000'

    $editorValue = Start-InlineEdit 'row-000'
    $oldNote = $editorValue.Current.Value
    if ($oldNote -notmatch '^row-(\d{3})$') { throw 'The inline editor opened on an unexpected fixture cell.' }
    $id = 1 + [int]$Matches[1]
    $editorValue.SetValue('committed-by-tab-switch')
    if ($editorValue.Current.Value -ne 'committed-by-tab-switch') { throw 'The inline editor did not receive the new value.' }
    Switch-TableTab 'zz_wide' 'c02-row1'
    if ((Read-GridNote $Fixture.Sqlite $id) -ne 'committed-by-tab-switch') {
        throw 'Switching tabs lost the inline value from the outgoing row.'
    }
    Switch-TableTab 'sample' 'committed-by-tab-switch'
    Invoke-Element (Wait-Element 'UndoEditCommand' -Enabled)
    $null = Wait-GridValue $oldNote
    if ((Read-GridNote $Fixture.Sqlite $id) -ne $oldNote) { throw 'Undo did not restore the outgoing row.' }
    Invoke-Element (Wait-Element 'RedoEditCommand' -Enabled)
    $null = Wait-GridValue 'committed-by-tab-switch'
    if ((Read-GridNote $Fixture.Sqlite $id) -ne 'committed-by-tab-switch') { throw 'Redo did not restore the inline value.' }
    Invoke-Element (Wait-Element 'UndoEditCommand' -Enabled)
    $null = Wait-GridValue $oldNote

    $historyFile = Join-Path $Fixture.Directory 'edit-history.json'
    $historyBefore = (Get-FileHash -LiteralPath $historyFile).Hash
    $editorValue = Start-InlineEdit 'row-003'
    $editorValue.SetValue('cancelled-inline-edit')
    [UiAcceptanceInput]::Key($process.MainWindowHandle, 27) # Escape
    Switch-TableTab 'zz_wide' 'c02-row1'
    Switch-TableTab 'sample' 'row-000'
    $null = Start-InlineEdit 'row-004'
    Switch-TableTab 'zz_wide' 'c02-row1'
    Switch-TableTab 'sample' 'row-000'

    $editorValue = Start-InlineEdit '10001' -RequireExactCell
    $editorValue.SetValue('invalid-integer')
    $tabs = Wait-Element 'OpenTableTabs'
    Click-Element (Find-Element $tabs ([Windows.Automation.AutomationElement]::NameProperty) 'zz_wide')
    $null = Wait-Element 'Use the typed row editor' -ByName
    if ($editorValue.Current.Value -ne 'invalid-integer' -or !(Find-GridValue 'row-000')) {
        throw 'An invalid edit must keep the outgoing tab and its input available for correction.'
    }
    [UiAcceptanceInput]::Key($process.MainWindowHandle, 27)
    Switch-TableTab 'zz_wide' 'c02-row1'
    if ((Get-FileHash -LiteralPath $historyFile).Hash -ne $historyBefore) {
        throw 'Escaped, unchanged, or invalid inline input changed history.'
    }
    Write-Output 'PASS Tab click committed the exact original cell once, with Undo and Redo. Escaped, unchanged, and invalid input wrote no history.'
}

function Test-ScrolledHeaderSorting {
    $cell = Wait-GridValue 'c02-row1'
    $scale = [UiAcceptanceInput]::GetDpiForWindow($process.MainWindowHandle) / 96.0
    if ($scale -le 0) { throw 'Could not determine the editor display scale.' }
    Click-Element $cell
    $originCellX = $cell.Current.BoundingRectangle.X
    $scroll = (Wait-Element 'GridControl').GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern)
    if ([Math]::Abs($scroll.Current.HorizontalScrollPercent) -gt 0.08) { throw 'The wide table did not start at the left edge.' }

    function Read-HeaderGlyphs([double] $CellX) {
        # The pinned TableView does not expose its headers in the automation tree.
        # Sample the rendered c02 label above the independently located body cell.
        $bounds = (Wait-Element 'GridControl').Current.BoundingRectangle
        $x = [int]($CellX - 3 * $scale)
        $y = [int]($bounds.Top + 5 * $scale)
        $width = [int](36 * $scale)
        $height = [int](24 * $scale)
        [UiAcceptanceInput]::Focus($process.MainWindowHandle)
        $bitmap = [Drawing.Bitmap]::new($width, $height)
        try {
            $graphics = [Drawing.Graphics]::FromImage($bitmap)
            try { $graphics.CopyFromScreen($x, $y, 0, 0, $bitmap.Size) }
            finally { $graphics.Dispose() }
            $background = $bitmap.GetPixel(0, 0).GetBrightness()
            $mask = for ($row = 0; $row -lt $height; $row++) {
                for ($col = 0; $col -lt $width; $col++) {
                    [Math]::Abs($bitmap.GetPixel($col, $row).GetBrightness() - $background) -gt 0.28
                }
            }
            [pscustomobject]@{ Mask = $mask; Width = $width; Height = $height }
        }
        finally { $bitmap.Dispose() }
    }

    $baseline = Read-HeaderGlyphs $originCellX
    $strokeCount = ($baseline.Mask | Where-Object { $_ } | Measure-Object).Count
    if ($strokeCount -lt 16) { throw 'The unscrolled grid did not render the expected visible header text.' }
    $scroll.SetScrollPercent(1.9, -1)

    function Assert-ScrolledGrid([string] $Phase) {
        $null = Wait-Observation "aligned headers and preserved offset $Phase" {
            $cellX = (Wait-GridValue 'c02-row1').Current.BoundingRectangle.X
            $cellShift = $originCellX - $cellX
            $actualScroll = (Wait-Element 'GridControl').GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern)
            if ($cellShift -lt 35 * $scale -or
                [Math]::Abs($actualScroll.Current.HorizontalScrollPercent - 1.9) -ge 0.08) { return }

            # The UIA rectangle and the rendered glyphs can round to neighboring pixels.
            foreach ($adjustment in 0, -1, 1) {
                $rendered = Read-HeaderGlyphs ($cellX + $adjustment)
                $difference = 0
                for ($i = 0; $i -lt $baseline.Mask.Count; $i++) {
                    if ($baseline.Mask[$i] -ne $rendered.Mask[$i]) { $difference++ }
                }
                if ($difference -le 0.2 * $strokeCount) { return $true }
            }
        }
    }

    Assert-ScrolledGrid 'after scrolling'
    foreach ($descending in $true, $false) {
        $currentCell = (Wait-GridValue 'c02-row1').Current.BoundingRectangle
        $grid = (Wait-Element 'GridControl').Current.BoundingRectangle
        [UiAcceptanceInput]::Click($process.MainWindowHandle,
            [int]($currentCell.X + 45 * $scale), [int]($grid.Top + 16 * $scale))
        $null = Wait-Observation "c02 sorting (descending=$descending)" {
            $first = (Wait-GridValue 'c02-row1').Current.BoundingRectangle.Y
            $last = (Wait-GridValue 'c02-row6').Current.BoundingRectangle.Y
            if (($first -gt $last) -eq $descending -and $first -ne $last) { $true }
        }
        $null = Wait-Element 'RefreshTableCommand' -Enabled
        Assert-ScrolledGrid "after sorting (descending=$descending)"
    }
    Switch-TableTab 'sample' 'row-000'
    Switch-TableTab 'zz_wide' 'c02-row1'
    Assert-ScrolledGrid 'after switching back from the narrow tab'
    if ((Wait-GridValue 'c02-row1').Current.BoundingRectangle.Y -ge (Wait-GridValue 'c02-row6').Current.BoundingRectangle.Y) {
        throw 'The wide tab lost its ascending sort.'
    }
    Write-Output 'PASS Headers and rows moved together and kept their horizontal position through both sorts and a narrow-tab round trip.'
}

function Test-CancelledResume {
    param([pscustomobject] $Active, [pscustomobject] $Candidate)

    Select-Element (Wait-Element 'NavigationRecovery')
    Select-RecoveryFixture $Candidate.Source
    $metadataPath = Join-Path $Candidate.Directory 'session.json'
    $metadataBefore = (Get-FileHash -LiteralPath $metadataPath).Hash
    $code = @'
import sqlite3
import sys

with sqlite3.connect(sys.argv[1], timeout=2) as connection:
    connection.execute("BEGIN EXCLUSIVE")
    print("locked", flush=True)
    sys.stdin.readline()
    connection.rollback()
'@
    $start = [Diagnostics.ProcessStartInfo]::new($python.Source)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    foreach ($argument in @('-c', $code, $Candidate.Sqlite)) { $start.ArgumentList.Add($argument) }
    $lockProcess = [Diagnostics.Process]::Start($start)
    try {
        if ($lockProcess.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(5)).GetAwaiter().GetResult() -ne 'locked') {
            throw 'The synthetic validation lock was not acquired.'
        }
        Invoke-Element (Wait-Element 'ResumeRecoverySessionCommand' -Enabled)
        Invoke-Element (Wait-Element 'Discard' -ByName -Enabled)
        $null = Wait-Element 'Resuming working session' -ByName
        Invoke-Element (Wait-Element 'CancelOperationCommand' -Enabled)
        $null = Wait-Element 'Resume cancelled' -ByName
        if ($lockProcess.HasExited) { throw 'Resume finished after the validation lock was released.' }
        if ((Get-FileHash -LiteralPath $metadataPath).Hash -ne $metadataBefore) {
            throw 'Cancelling blocked validation changed the candidate metadata.'
        }
    }
    finally {
        try { $lockProcess.StandardInput.Close() }
        finally { Stop-TestProcess $lockProcess }
    }

    Select-Element (Wait-Element 'NavigationTables')
    $null = Wait-Element 'UndoEditCommand' -Enabled
    if ((Read-Observation $Active.Sqlite) -ne '501:1' -or (Read-Observation $Candidate.Sqlite) -ne '1:0') {
        throw 'Resume cancellation changed the active edits or the recoverable candidate.'
    }
    Write-Output 'PASS Resume cancelled during locked SQLite validation and kept both sessions and Undo intact.'
}

function Start-TestEditor {
    param([IO.FileInfo] $Binary, [string] $ProfileDirectory)

    $start = [Diagnostics.ProcessStartInfo]::new($Binary.FullName)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $Binary.DirectoryName
    $start.Environment['PCM_CDB_EDITOR_DATA_ROOT'] = $ProfileDirectory
    [Diagnostics.Process]::Start($start)
}

function Wait-TestEditorWindow {
    Wait-Observation 'the editor window' {
        $process.Refresh()
        if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
            [Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
        }
    }
}

function Stop-TestProcess {
    param([Diagnostics.Process] $Child)

    try {
        if (!$Child.HasExited -and !$Child.WaitForExit(500)) {
            $Child.Kill($true)
            if (!$Child.WaitForExit(5000)) { throw "Test process $($Child.Id) is still running." }
        }
    }
    finally { $Child.Dispose() }
}

function Test-ConverterCancellation {
    $dataRoot = Join-Path $run 'converter-data'
    $appRoot = Join-Path $run 'converter-app'
    @'
import os
import shutil
import sys

def link_or_copy(source, destination):
    try:
        os.link(source, destination)
    except OSError:
        shutil.copy2(source, destination)

shutil.copytree(sys.argv[1], sys.argv[2], copy_function=link_or_copy,
                ignore=shutil.ignore_patterns("SQLiteExporter.exe", "publish"))
'@ | & $python.Source - $editor.DirectoryName $appRoot
    if ($LASTEXITCODE -ne 0) { throw 'Could not isolate the test editor from the real exporter.' }
    $converterRoot = Join-Path $appRoot 'third_party/SQLiteExporter'
    $null = [IO.Directory]::CreateDirectory($converterRoot)
    $fixtureCode = Join-Path $repositoryRoot 'tests/PcmCdbEditor.IntegrationTests/Fixtures/FakeConverterProgram.cs'
    $compiler = Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    & $compiler /nologo /target:exe "/out:$(Join-Path $converterRoot 'SQLiteExporter.exe')" $fixtureCode
    if ($LASTEXITCODE -ne 0) { throw 'Could not compile the stalled exporter fixture.' }

    $fixture = New-RecoveryFixture 'converter-original' 1
    $target = Join-Path $run 'converter-open.cdb'
    [IO.File]::WriteAllText($target, 'Synthetic Open candidate')
    @{ Preferences = @{ RecentFiles = @($target) } } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $dataRoot 'settings.json') -Encoding utf8
    $scenarioPath = Join-Path $converterRoot 'scenario.txt'
    $marker = Join-Path $converterRoot 'parent.pid'
    $initialHash = (Get-FileHash -LiteralPath $fixture.Source).Hash
    $process = Start-TestEditor (Get-Item -LiteralPath (Join-Path $appRoot $editor.Name)) $dataRoot
    try {
        $window = Wait-TestEditorWindow
        Select-RecoveryFixture $fixture.Source
        Invoke-Element (Wait-Element 'ResumeRecoverySessionCommand' -Enabled)
        $null = Wait-Element 'RefreshTableCommand' -Enabled

        function Invoke-OpenSyntheticCandidate {
            Invoke-Element (Wait-Element 'MoreButton' -Enabled)
            Invoke-Element (Wait-Element 'ShowRecentDatabasesCommand' -Enabled)
            $list = Wait-Element 'Recent database paths' -ByName
            $item = Wait-Observation 'the synthetic recent file' {
                $entry = Find-Element $list ([Windows.Automation.AutomationElement]::NameProperty) $target
                while ($entry -and !$entry.Equals($list)) {
                    $pattern = $null
                    if ($entry.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
                        return $entry
                    }
                    $entry = [Windows.Automation.TreeWalker]::RawViewWalker.GetParent($entry)
                }
            }
            Select-Element $item
            Invoke-Element (Wait-Element 'Open selected' -ByName -Enabled)
            Invoke-Element (Wait-Element 'Discard' -ByName -Enabled)
        }

        foreach ($operation in 'Open', 'Save') {
            [IO.File]::WriteAllText($scenarioPath, 'sleep')
            if (Test-Path -LiteralPath $marker) { Remove-Item -LiteralPath $marker }
            if ($operation -eq 'Open') {
                Invoke-OpenSyntheticCandidate
            }
            else {
                $save = Wait-Observation 'the Save command' {
                    $button = Find-Element $window ([Windows.Automation.AutomationElement]::AutomationIdProperty) 'SaveDatabaseCommand'
                    if ($button -and $button.Current.IsEnabled) { $button }
                }
                Invoke-Element $save
            }
            $exporterId = Wait-Observation "$operation exporter started" {
                if (Test-Path -LiteralPath $marker) {
                    $value = (Get-Content -Raw -LiteralPath $marker).Trim()
                    if ($value -match '^\d+$') { [int]$value }
                }
            }
            Invoke-Element (Wait-Element 'CancelOperationCommand' -Enabled)
            $null = Wait-Element "$operation cancelled" -ByName
            $null = Wait-Element 'RefreshTableCommand' -Enabled
            if (Get-Process -Id $exporterId -ErrorAction SilentlyContinue) { throw 'The cancelled exporter is still running.' }
            if ((Get-FileHash -LiteralPath $fixture.Source).Hash -ne $initialHash -or
                (Read-Observation $fixture.Sqlite) -ne '1:0' -or
                @(Get-ChildItem -LiteralPath (Join-Path $dataRoot 'Sessions') -Directory | Where-Object Name -Match '^[a-f0-9]{32}$').Count -ne 1) {
                throw "$operation cancellation did not preserve the existing workspace and source."
            }
        }

        [IO.File]::WriteAllText($scenarioPath, 'nonzero')
        Invoke-OpenSyntheticCandidate
        $null = Wait-Element 'Could not open the database' -ByName
        Write-Output 'PASS Open and Save show cancellation titles for a stopped exporter. A nonzero exporter exit still shows an error.'

        [IO.File]::WriteAllText($scenarioPath, 'copy-snapshot')
        $null = Wait-Element 'RefreshTableCommand' -Enabled
        if (!$process.CloseMainWindow()) { throw 'Could not request Save and close.' }
        Invoke-Element (Wait-Element 'Save and close' -ByName -Enabled)
        if (!$process.WaitForExit(12000) -or $process.ExitCode -ne 0 -or
            (Test-Path -LiteralPath $fixture.Directory) -or (Read-Observation $fixture.Source) -ne '1:0') {
            throw 'Save and close did not publish the working database and close the session.'
        }
        Write-Output 'PASS Save and close published the working database and closed the session.'
    }
    finally { Stop-TestProcess $process }
}

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
. (Join-Path $PSScriptRoot 'Release-Filesystem.ps1')
$root = Join-Path $repositoryRoot 'artifacts'
Assert-NoReparsePointInDirectoryChain -Path $root -Root $repositoryRoot -Purpose 'Native verification storage'
$runName = 'native-ui-' + [guid]::NewGuid().ToString('N')
$run = Join-Path $root $runName
$dataRoot = Join-Path $run 'data'
$null = [IO.Directory]::CreateDirectory($run)
Write-Host "Native verification scratch: $run"
$process = $null

try {
    $original = New-RecoveryFixture 'original' 500
    $replacement = New-RecoveryFixture 'replacement' 1
    $initialHash = (Get-FileHash -LiteralPath $original.Source -Algorithm SHA256).Hash
    @{ Preferences = @{ PageSize = 500 } } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $dataRoot 'settings.json') -Encoding utf8

    $process = Start-TestEditor $editor $dataRoot
    $window = Wait-TestEditorWindow

    Select-RecoveryFixture $original.Source
    Invoke-Element (Wait-Element 'ResumeRecoverySessionCommand' -Enabled)
    $insert = Wait-Element 'InsertTableRowCommand' -Enabled
    if ((Wait-Element 'NextTablePageCommand').Current.IsEnabled) {
        throw 'Next is enabled for a table with exactly one full page.'
    }

    Invoke-Element $insert
    $note = Wait-Element 'note value' -ByName -Enabled
    $pattern = $note.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)
    $pattern.SetValue('written-by-native-ui')
    Invoke-Element (Wait-Element 'Insert' -ByName -Enabled)
    $null = Wait-Element 'UndoEditCommand' -Enabled
    if ((Read-Observation $original.Sqlite) -ne '501:1') { throw 'The typed insertion did not reach SQLite.' }

    Invoke-Element (Wait-Element 'UndoEditCommand' -Enabled)
    $null = Wait-Element 'RedoEditCommand' -Enabled
    if ((Read-Observation $original.Sqlite) -ne '500:0') { throw 'Undo did not remove exactly the inserted row.' }

    Invoke-Element (Wait-Element 'RedoEditCommand' -Enabled)
    $null = Wait-Element 'UndoEditCommand' -Enabled
    if ((Read-Observation $original.Sqlite) -ne '501:1') { throw 'Redo did not restore the typed insertion.' }
    Write-Output 'PASS Native grid, typed insertion, Undo, and Redo observed through SQLite.'

    Test-InlineTabSwitch $original
    Test-ScrolledHeaderSorting
    Test-CancelledResume $original $replacement

    Select-CatalogTable 'zz_refresh_trap'
    $tabs = Wait-Element 'OpenTableTabs'
    $null = Wait-Observation 'the refresh test tab' {
        Find-Element $tabs ([Windows.Automation.AutomationElement]::NameProperty) 'zz_refresh_trap'
    }
    $undo = Wait-Element 'UndoEditCommand' -Enabled
    Set-RefreshTrap -Sqlite $original.Sqlite -Enabled $true
    Invoke-Element $undo
    $null = Wait-Element 'RedoEditCommand' -Enabled
    $null = Wait-Element 'Could not refresh the table' -ByName
    if ((Read-Observation $original.Sqlite) -ne '500:0') { throw 'The refresh failure lost the committed Undo.' }
    $metadata = Get-Content -Raw -LiteralPath (Join-Path $original.Directory 'session.json') | ConvertFrom-Json
    if ($metadata.MutationPending) { throw 'The refresh failure left a pending mutation marker.' }

    Invoke-Element (Wait-Element 'RefreshTableCommand' -Enabled)
    $null = Wait-Element 'Could not update the table query' -ByName
    Set-RefreshTrap -Sqlite $original.Sqlite -Enabled $false
    Invoke-Element (Wait-Element 'RefreshTableCommand' -Enabled)
    $null = Wait-Observation 'refresh succeeds and clears the query warning' {
        $warning = Find-Element $window ([Windows.Automation.AutomationElement]::NameProperty) 'Could not update the table query'
        if ($null -eq $warning) { $true }
    }
    if ((Read-Observation $original.Sqlite) -ne '500:0') { throw 'The successful refresh retry changed the working rows.' }
    Write-Output 'PASS Durable Undo survived a failed refresh and retry, then Refresh recovered.'
    Invoke-Element (Wait-Element 'RedoEditCommand' -Enabled)
    $null = Wait-Element 'UndoEditCommand' -Enabled

    Select-Element (Wait-Element 'NavigationRecovery')
    Select-RecoveryFixture $replacement.Source
    # Discovery succeeds; reading the first table must fail before the existing session closes.
    @"
import sqlite3, sys
with sqlite3.connect(sys.argv[1]) as connection:
    connection.execute("CREATE VIEW aaa_broken AS SELECT json_extract('not-json', '$') AS id")
"@ | & $python.Source - $replacement.Sqlite
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the failing candidate view.' }
    Invoke-Element (Wait-Element 'ResumeRecoverySessionCommand' -Enabled)
    Invoke-Element (Wait-Element 'Discard' -ByName -Enabled)
    $null = Wait-Element 'Could not resume the session' -ByName
    Select-Element (Wait-Element 'NavigationTables')
    $null = Wait-Element 'UndoEditCommand' -Enabled
    if ((Read-Observation $original.Sqlite) -ne '501:1') { throw 'The failed replacement lost the original working copy.' }
    if ((Get-FileHash -LiteralPath $original.Source -Algorithm SHA256).Hash -ne $initialHash) {
        throw 'The original source marker was modified.'
    }
    Write-Output 'PASS Failed recovery replacement kept the prior session and its edits.'

    Select-CatalogTable 'zz_slow_count'

    $pendingCount = Wait-Observation 'the outstanding SQLite count' {
        $condition = [Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::ControlTypeProperty,
            [Windows.Automation.ControlType]::Text)
        foreach ($label in $window.FindAll([Windows.Automation.TreeScope]::Descendants, $condition)) {
            if ($label.Current.Name.StartsWith('zz_slow_count', [StringComparison]::Ordinal) -and
                $label.Current.Name.Contains('of counting')) {
                return $label
            }
        }
    }

    if (!$process.CloseMainWindow()) { throw 'Could not request window close while counting.' }
    $discard = Wait-Element 'Discard and close' -ByName -Enabled
    if (!$pendingCount.Current.Name.Contains('of counting')) { throw 'The count finished before discard confirmation.' }
    Invoke-Element $discard
    if (!$process.WaitForExit(12000)) { throw 'Editor did not exit after confirmed discard.' }
    if ($process.ExitCode -ne 0 -or (Test-Path -LiteralPath $original.Directory)) {
        throw 'The editor did not exit cleanly and remove the discarded session.'
    }
    if (!(Test-Path -LiteralPath $replacement.Directory)) {
        throw 'Discard unexpectedly removed another recovery session.'
    }
    Write-Output 'PASS Confirmed discard with a count still pending removed only the active session.'
    Test-ConverterCancellation
}
finally {
    if ($null -ne $process) {
        Stop-TestProcess $process
    }

    Remove-ReleaseScratch -Path $run -ArtifactsRoot $root
    if (Test-Path -LiteralPath $run) { throw "Native verification scratch remains at $run." }
}
