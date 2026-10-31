"""Apply one UI step only to the child of an owned native smoke launcher."""
from __future__ import annotations

import ctypes
import json
import os
from pathlib import Path
import subprocess
import sys
import time

sys.path.insert(0, str(Path.cwd()))


def main() -> None:
    """Verify process identity and perform one explicitly requested test UI action.

    Raises:
        RuntimeError: Ownership or foreground focus cannot be established.
    """
    ctypes.windll.user32.SetProcessDPIAware()
    import win32api
    import win32con
    import win32gui
    import win32process
    import pyautogui as pag
    from PIL import ImageGrab
    from server.tools.process_info import process_path

    folder = Path(os.environ['ENB_NATIVE_SMOKE_RUN_DIR']).resolve()
    owner = json.loads((folder / 'owner.json').read_text())
    proxy = win32api.OpenProcess(0x1000, False, owner['proxy_pid'])
    try:
        if win32process.GetProcessTimes(proxy)['CreationTime'].timestamp() != owner['proxy_birth']:
            raise RuntimeError('Owned proxy creation time changed')
        if Path(process_path(int(proxy))).resolve() != Path(owner['proxy_path']).resolve():
            raise RuntimeError('Owned proxy executable changed')
    finally:
        proxy.Close()

    pinned_path = folder / 'ui-owner.json'
    if pinned_path.exists():
        pinned = json.loads(pinned_path.read_text())
    else:
        # Integer-only interpolation into a fixed read-only process query.
        query = (
            f"Get-CimInstance Win32_Process -Filter \"ParentProcessId={int(owner['proxy_pid'])}\" "
            "| Where-Object Name -eq 'client.exe' | Select-Object -ExpandProperty ProcessId"
        )
        result = subprocess.run(
            ['powershell.exe', '-NoProfile', '-Command', query],
            capture_output=True, text=True, check=True,
            creationflags=subprocess.CREATE_NO_WINDOW,
        )
        children = result.stdout.split()
        if len(children) != 1:
            raise RuntimeError('Owned proxy does not have exactly one game child yet')
        child = win32api.OpenProcess(0x1000, False, int(children[0]))
        try:
            if Path(process_path(int(child))).resolve() != Path(owner['client_path']).resolve():
                raise RuntimeError('Owned game child executable does not match')
            pinned = {
                'pid': int(children[0]),
                'birth': win32process.GetProcessTimes(child)['CreationTime'].timestamp(),
            }
            pinned_path.write_text(json.dumps(pinned, indent=2))
        finally:
            child.Close()

    child = win32api.OpenProcess(0x1000, False, pinned['pid'])
    try:
        if win32process.GetProcessTimes(child)['CreationTime'].timestamp() != pinned['birth']:
            raise RuntimeError('Owned client PID was reused')
        if Path(process_path(int(child))).resolve() != Path(owner['client_path']).resolve():
            raise RuntimeError('Owned client executable changed')
        windows: list[int] = []

        def collect(window: int, unused: object) -> None:
            """Collect only visible windows of the pinned game process."""
            if win32process.GetWindowThreadProcessId(window)[1] == pinned['pid'] and win32gui.IsWindowVisible(window):
                windows.append(window)

        win32gui.EnumWindows(collect, None)
        window = next(
            window for window in windows
            if win32gui.GetWindowText(window).startswith(('Earth & Beyond', 'ENB_local_'))
        )
        action = sys.argv[1]
        if action == 'close':
            win32gui.PostMessage(window, win32con.WM_CLOSE, 0, 0)
            return
        if action != 'screenshot':
            win32gui.ShowWindow(window, win32con.SW_RESTORE)
            try:
                win32gui.SetForegroundWindow(window)
            except win32gui.error:
                foreground = win32gui.GetForegroundWindow()
                thread = win32process.GetWindowThreadProcessId(foreground)[0]
                current = win32api.GetCurrentThreadId()
                ctypes.windll.user32.AttachThreadInput(current, thread, True)
                try:
                    win32gui.SetForegroundWindow(window)
                finally:
                    ctypes.windll.user32.AttachThreadInput(current, thread, False)
            time.sleep(.7)

        def check_focus() -> None:
            """Stop input if another process has taken foreground focus."""
            if win32process.GetWindowThreadProcessId(win32gui.GetForegroundWindow())[1] != pinned['pid']:
                raise RuntimeError('Owned game is not foreground; input stopped')

        def click(x: int, y: int) -> None:
            """Use the established Pluto login coordinates in the owned client."""
            check_focus()
            pag.click(*win32gui.ClientToScreen(window, (x, y)))

        if action == 'agree':
            check_focus()
            children: list[int] = []
            win32gui.EnumChildWindows(window, lambda item, unused: children.append(item), None)
            button = next(item for item in children if win32gui.GetWindowText(item) == 'I Agree')
            win32gui.SendMessage(button, win32con.BM_CLICK, 0, 0)
        elif action == 'skip-intro':
            click(640, 380)
        elif action == 'login':
            for point, variable in (
                ((300, 190), 'ENB_NATIVE_PROBE_USERNAME'),
                ((300, 245), 'ENB_NATIVE_PROBE_PASSWORD'),
            ):
                click(*point)
                time.sleep(1)
                check_focus()
                pag.press('end')
                pag.press('backspace', presses=40, interval=.01)
                for character in os.environ[variable]:
                    check_focus()
                    pag.write(character, interval=.1)
            check_focus()
            pag.press('enter')
            return  # Never save an image of credentials entered in the form.
        elif action == 'select':
            click(220, 52)
            time.sleep(7)
            click(1207, 713)
        elif action == 'inventory':
            check_focus()
            pag.press('i')
        elif action == 'quit':
            check_focus()
            pag.press('enter')
            pag.write('/quit', interval=.1)
            pag.press('enter')
        elif action != 'screenshot':
            raise ValueError('Use agree, skip-intro, login, select, inventory, screenshot, quit, or close')
        time.sleep(.6)
        if win32gui.IsWindow(window):
            ImageGrab.grab(bbox=win32gui.GetWindowRect(window), all_screens=True).save(folder / 'current-ui.png')
    finally:
        child.Close()


if __name__ == '__main__':
    main()
