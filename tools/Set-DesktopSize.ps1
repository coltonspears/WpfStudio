<#
.SYNOPSIS
Sets the primary display's resolution, for CI machines whose desktop is smaller than the window a test lays out.

.DESCRIPTION
GitHub's windows-latest runners start with a 1024x768 desktop. Windows caps a window at the desktop's size, so the shell smoke
test, which checks the layout of WpfStudio's default 1480x940 window, got a 1044x788 one there. This changes the display mode
with ChangeDisplaySettingsEx, which works on Windows Server and Windows alike, and prints the size it ends with.
#>
param(
    [int] $Width = 1920,
    [int] $Height = 1080
)
$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;

public static class DesktopSize
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DisplayMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public short SpecVersion, DriverVersion, Size, DriverExtra;
        public int Fields, PositionX, PositionY, DisplayOrientation, DisplayFixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public short LogPixels;
        public int BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency, IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool EnumDisplaySettings(string device, int mode, ref DisplayMode displayMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int ChangeDisplaySettingsEx(string device, ref DisplayMode displayMode, System.IntPtr window, int flags, System.IntPtr parameter);
}
'@

# PowerShell passes $null to a string parameter as "", which names no display, so the primary display is named outright.
Add-Type -AssemblyName System.Windows.Forms
$device = [System.Windows.Forms.Screen]::PrimaryScreen.DeviceName
$CurrentSettings = -1
$PelsWidthAndHeight = 0x00180000
$UpdateRegistry = 0x00000001

$mode = New-Object DesktopSize+DisplayMode
$mode.Size = [System.Runtime.InteropServices.Marshal]::SizeOf($mode)
if (-not [DesktopSize]::EnumDisplaySettings($device, $CurrentSettings, [ref] $mode)) { throw "The current display mode of $device could not be read." }
"Desktop of $device was $($mode.PelsWidth)x$($mode.PelsHeight)"
if ($mode.PelsWidth -ne $Width -or $mode.PelsHeight -ne $Height) {
    $mode.PelsWidth = $Width
    $mode.PelsHeight = $Height
    $mode.Fields = $PelsWidthAndHeight
    $result = [DesktopSize]::ChangeDisplaySettingsEx($device, [ref] $mode, [System.IntPtr]::Zero, $UpdateRegistry, [System.IntPtr]::Zero)
    # DISP_CHANGE_SUCCESSFUL is 0; 1 means it applies after a restart, which a CI run cannot wait for.
    if ($result -ne 0) { throw "The display refused ${Width}x${Height} (ChangeDisplaySettingsEx returned $result)." }
}
[void] [DesktopSize]::EnumDisplaySettings($device, $CurrentSettings, [ref] $mode)
"Desktop is $($mode.PelsWidth)x$($mode.PelsHeight)"
if ($mode.PelsWidth -ne $Width -or $mode.PelsHeight -ne $Height) { throw "The desktop is $($mode.PelsWidth)x$($mode.PelsHeight), not ${Width}x${Height}." }
